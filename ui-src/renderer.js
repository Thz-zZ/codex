'use strict';
const $=id=>document.getElementById(id);
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fmt=v=>Number.isFinite(v)?Number(v).toLocaleString('en-US'):'—';
const compact=v=>Number.isFinite(v)?Intl.NumberFormat('en-US',{notation:'compact',maximumFractionDigits:2}).format(v):'—';
const rate=v=>Number.isFinite(v)?(v*100).toFixed(2)+'%':'—';
const dateTime=v=>v?new Date(v).toLocaleString('zh-CN',{hour12:false}):'—';
function refreshAge(at,now=Date.now()){
  const timestamp=Date.parse(at);if(!Number.isFinite(timestamp))return '';
  const seconds=Math.max(0,Math.floor((now-timestamp)/1000));
  if(seconds<1)return '刚刚';
  if(seconds<60)return seconds+'秒前';
  if(seconds<3600)return Math.floor(seconds/60)+'分钟前';
  if(seconds<86400)return Math.floor(seconds/3600)+'小时前';
  return Math.floor(seconds/86400)+'天前';
}
function updateRefreshTime(){
  const at=currentDetail?.at||currentDetail?.summary?.checkedAt||currentDetail?.checkedAt,age=refreshAge(at);
  const label=age?'上次刷新：'+dateTime(at)+' · '+age:'上次刷新：尚未刷新';
  if($('detailRefreshTime').textContent!==label)$('detailRefreshTime').textContent=label;
}
const adapterLabel=p=>{const type=p.adapter==='auto'?p.detectedAdapter:p.adapter;return (p.adapter==='auto'?'自动 · ':'')+(type==='sub2api'?'Sub2API':type==='newapi'?'NewAPI':'待识别');};
const balanceText=b=>Number.isFinite(b?.value)?b.value.toLocaleString('en-US',{minimumFractionDigits:2,maximumFractionDigits:4}):'—';
let state={providers:[],history:[],config:{}},page='providers',detailId=null,detailEpoch=0,auto=true,nextRefresh=Date.now()+60000,allBusy=false,toastTimer,editorEpoch=0,siteFollowsBase=true,rangePeriod='today',draggedId=null,reorderBusy=false,currentDetail=null,chartGranularity='auto',detectEpoch=0;
const cache=new Map(),pending=new Set(),requests=new Map(),hiddenSeries=new Set(),detailViews=new Map();
let detailViewSave=Promise.resolve();
const detailCacheKey=(id,period,model='all')=>id+':'+period+(model==='all'?'':':model:'+model);
function rememberDetailView(){
  if(!detailId)return;
  detailViews.set(detailId,{period:rangePeriod,model:$('modelFilter').value||'all',granularity:chartGranularity,hiddenSeries:[...hiddenSeries]});
  if(window.relay.detailViewState)detailViewSave=call('detailViewState',Object.fromEntries(detailViews)).catch(error=>toast(error.message,true));
}
function syncModelFilter(models=[],selection=$('modelFilter').value||'all'){
  const names=[...new Set([...models,...(selection==='all'?[]:[selection])])];
  $('modelFilter').innerHTML='<option value="all">全部模型</option>'+names.map(m=>`<option value="${esc(m)}">${esc(m)}</option>`).join('');$('modelFilter').value=selection;
  filterPickers.get('modelFilter')?.sync();
}
function restoreDetailView(id){
  const saved=detailViews.get(id);
  rangePeriod=saved?.period||'today';try{RelayAdapters.range(rangePeriod);}catch{rangePeriod='today';}
  chartGranularity=['auto','hour','day'].includes(saved?.granularity)?saved.granularity:'auto';
  hiddenSeries.clear();for(const key of saved?.hiddenSeries||[])hiddenSeries.add(key);
  syncRangeControls();syncModelFilter([],saved?.model||'all');
}
const detailStorageKey='relay-detail-cache-v1';
function hydrateDetails(){try{for(const r of JSON.parse(localStorage.getItem(detailStorageKey)||'[]'))if(state.providers.some(p=>p.id===r.id&&p.revision===r.revision)&&['today','7'].includes(r.period)&&r.range?.end===RelayAdapters.range(r.period).end&&r.usage){const key=detailCacheKey(r.id,r.period,r.model||'all'),previous=cache.get(key);if(!previous||Date.parse(r.at)>=Date.parse(previous.summary?.checkedAt||previous.checkedAt||previous.at))cache.set(key,r);}}catch{}}
function persistDetails(){try{const values=[...cache.values()].filter(r=>['today','7'].includes(r.period)&&r.usage&&r.range?.end===RelayAdapters.range(r.period).end&&state.providers.some(p=>p.id===r.id&&p.revision===r.revision)).sort((a,b)=>Date.parse(b.at)-Date.parse(a.at)).slice(0,6),saved=[];let size=0;for(const r of values){const length=JSON.stringify(r).length;if(length>512000||size+length>2000000)continue;saved.push(r);size+=length;}localStorage.setItem(detailStorageKey,JSON.stringify(saved));}catch{}}
const rangeLabel=period=>({today:'今天',yesterday:'昨天','7':'最近 7 天','30':'最近 30 天',all:'全部记录 / 近 90 天'})[period]||(period.startsWith('range:')?period.slice(6).replace(':',' ～ '):period);
const usageLabel=()=>rangePeriod==='today'?'今日':rangePeriod==='yesterday'?'昨日':'所选日期';
function matchingCache(id,period=rangePeriod,model='all'){const value=cache.get(detailCacheKey(id,period,model));return value?.revision===state.providers.find(p=>p.id===id)?.revision&&value?.period===period&&value?.range?.end===RelayAdapters.range(period).end?value:null;}
function selectedCache(){return matchingCache(detailId,rangePeriod,$('modelFilter').value||'all');}
const filterPickers=new Map();
function enhanceFilter(id,icon){
  const source=$(id),root=$(id+'Picker'),menuId=id+'Menu',valueId=id+'Value';
  root.innerHTML=`<button type="button" class="filter-trigger" role="combobox" aria-haspopup="listbox" aria-expanded="false" aria-controls="${menuId}" aria-labelledby="${id}Label ${valueId}">${icon}<span id="${valueId}" class="filter-value"></span><svg class="filter-chevron" viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.5" aria-hidden="true"><path d="m4 6 4 4 4-4"/></svg></button><div id="${menuId}" class="filter-menu hidden" role="listbox" aria-labelledby="${id}Label"></div>`;
  const trigger=root.querySelector('button'),menu=$(menuId),hints=['period','listPeriod'].includes(id)?{all:'已返回的记录 · 最多近 90 天',today:'北京时间 00:00 起',yesterday:'前一天的用量','7':'过去 7 天的用量','30':'过去 30 天的用量',custom:'选择某一天或起止日期'}:{};
  let active=0,search='',searchAt=0;
  const options=()=>Array.from(source.options);
  const close=()=>{menu.classList.add('hidden');trigger.setAttribute('aria-expanded','false');trigger.removeAttribute('aria-activedescendant');root.classList.remove('is-open');};
  const highlight=()=>{const items=menu.querySelectorAll('[role=option]');items.forEach((item,i)=>item.classList.toggle('is-focused',i===active));const item=items[active];if(item){trigger.setAttribute('aria-activedescendant',item.id);item.scrollIntoView({block:'nearest'});}};
  const sync=()=>{const opts=options();active=Math.max(0,source.selectedIndex);$(valueId).textContent=opts[active]?.textContent||'全部模型';trigger.dataset.tooltip=$(valueId).textContent;menu.innerHTML=opts.map((o,i)=>`<div id="${id}Option${i}" role="option" aria-selected="${o.selected}" class="filter-option${o.selected?' is-selected':''}" data-index="${i}"><span class="filter-option-copy"><span>${esc(o.textContent)}</span>${hints[o.value]?`<small>${esc(hints[o.value])}</small>`:''}</span><svg class="filter-check" viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><path d="m3 8 3 3 7-7"/></svg></div>`).join('');close();};
  const open=()=>{for(const picker of filterPickers.values())picker.close();document.dispatchEvent(new CustomEvent('relay-dropdown-open',{detail:id}));menu.classList.remove('hidden');root.classList.add('is-open');trigger.setAttribute('aria-expanded','true');active=Math.max(0,source.selectedIndex);highlight();};
  const choose=index=>{const changed=source.selectedIndex!==index;source.selectedIndex=index;sync();trigger.focus();if(changed)source.dispatchEvent(new Event('change',{bubbles:true}));};
  trigger.addEventListener('click',()=>menu.classList.contains('hidden')?open():close());
  menu.addEventListener('pointerdown',event=>event.preventDefault());
  // Prevent the enclosing label from clicking the trigger again after selection.
  menu.addEventListener('click',event=>{const option=event.target.closest('[data-index]');if(option){event.preventDefault();choose(Number(option.dataset.index));}});
  trigger.addEventListener('keydown',event=>{const isOpen=!menu.classList.contains('hidden'),length=source.options.length;if(event.key==='Tab'){close();return;}if(event.key==='Escape'){if(isOpen){event.preventDefault();event.stopPropagation();close();}return;}if(['ArrowDown','ArrowUp','Home','End'].includes(event.key)){event.preventDefault();if(!isOpen)open();else if(event.key==='ArrowDown')active=(active+1)%length;else if(event.key==='ArrowUp')active=(active+length-1)%length;if(event.key==='Home')active=0;if(event.key==='End')active=length-1;highlight();}else if(['Enter',' '].includes(event.key)){event.preventDefault();if(isOpen)choose(active);else open();}else if(event.key.length===1&&!event.ctrlKey&&!event.metaKey&&!event.altKey){event.preventDefault();if(!isOpen)open();search=Date.now()-searchAt>700?event.key:search+event.key;searchAt=Date.now();const index=options().findIndex(o=>o.textContent.toLowerCase().startsWith(search.toLowerCase()));if(index>=0){active=index;highlight();}}});
  root.addEventListener('focusout',event=>{if(!root.contains(event.relatedTarget))close();});
  document.addEventListener('pointerdown',event=>{if(!root.contains(event.target))close();});
  document.addEventListener('relay-dropdown-open',event=>{if(event.detail!==id)close();});
  filterPickers.set(id,{sync,close,open});sync();
}
async function call(name,input){const result=await window.relay[name](input);if(!result.ok)throw new Error(result.error);return result.data;}
function toast(message,error=false){clearTimeout(toastTimer);$('toast').textContent=message;$('toast').className='toast'+(error?' error':'');toastTimer=setTimeout(()=>$('toast').classList.add('hidden'),error?9000:6500);}
function confirmAction(title,text){return new Promise(resolve=>{$('confirmTitle').textContent=title;$('confirmText').textContent=text;$('confirmDialog').showModal();let done=false;const finish=value=>{if(done)return;done=true;$('confirmDialog').close();$('confirmDialog').removeEventListener('cancel',cancel);resolve(value);};const cancel=e=>{e.preventDefault();finish(false);};$('confirmCancel').onclick=()=>finish(false);$('confirmOkay').onclick=()=>finish(true);$('confirmDialog').addEventListener('cancel',cancel);});}
function setState(value){state=value;for(const summary of state.usageCache||[]){const key=summary.id+':today',existing=cache.get(key);if(!existing||Date.parse(summary.checkedAt)>Date.parse(existing.summary?.checkedAt||existing.checkedAt||existing.at))cache.set(key,summary);}hydrateDetails();renderState();}
function cacheFresh(value){const record=value?.summary||value,at=Date.parse(record&&'checkedAt' in record?record.checkedAt:record?.at);return Number.isFinite(at)&&Date.now()-at>=0&&Date.now()-at<60000;}
function cachedTime(value){return value?new Date(value).toLocaleString('zh-CN',{month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',hour12:false}):'尚未查询';}
function navigate(target){
  chartHover?.hide();page=target==='providers'&&detailId?'details':target;
  for(const picker of filterPickers.values())picker.close();
  for(const name of ['providers','details','settings','sessions'])$(name+'Page').classList.toggle('hidden',name!==page);
  const main=document.querySelector('main');main.classList.toggle('sessions-layout',page==='sessions');if(page==='sessions')main.scrollTop=0;
  document.querySelectorAll('[data-nav]').forEach(b=>b.classList.toggle('selected',b.dataset.nav===page));
  renderSelectedHeader();renderProviders();
  window.RelaySessions?.onNavigate(page);
  if(page==='details'&&!matchMedia('(prefers-reduced-motion: reduce)').matches)$('detailsPage').animate([{opacity:.72,transform:'translateY(3px)'},{opacity:1,transform:'translateY(0)'}],{duration:160,easing:'ease-out'});
}
function renderSelectedHeader(){
  const p=page==='details'?state.providers.find(p=>p.id===detailId):null;
  $('pageTitle').textContent=p?.name||(page==='settings'?'设置':page==='sessions'?'会话管理':'中转工作台');
  $('selectedMeta').textContent=p?p.baseUrl+' · '+adapterLabel(p)+' · 模型：'+(p.model||state.config.model||'沿用 Codex 设置'):page==='settings'?'管理 Codex 配置与本地数据':page==='sessions'?'浏览本地对话 · 导出、导入与整理会话':'为 Codex 管理中转与用量';
  $('selectedMeta').dataset.tooltip=$('selectedMeta').textContent;
  $('selectedStatus').className='badge'+(p?' '+(p.active?'live':'viewing'):' hidden');
  $('selectedStatus').textContent=p?.active?'使用中':'未应用';
  $('detailActions').dataset.id=p?.id||'';
  $('detailActions').innerHTML=p?'<div class="provider-edit-actions" role="group" aria-label="中转管理"><button class="button" data-action="edit"><svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m15 5 4 4M4 20l4-1 12-12a2.8 2.8 0 0 0-4-4L4 15z"/></svg>编辑</button><button class="icon-button delete-selected" data-action="delete" aria-label="删除中转" data-tooltip="删除中转"><svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 10v7M14 10v7"/></svg></button></div><button class="button apply-provider" data-action="switch"><svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'+(p.active?'<path d="M20 7v5h-5M4 17v-5h5M6 7a7 7 0 0 1 12-1l2 6M4 12l2 6a7 7 0 0 0 12-1"/>':'<path d="M4 12h16m-6-6 6 6-6 6"/>')+'</svg>'+(p.active?'重新应用':'应用到 Codex')+'</button>':'';
}
function renderState(){
  $('providerCount').textContent=state.providers.length;
  const changed=!state.providers.some(p=>p.id===detailId);
  for(const id of detailViews.keys())if(!state.providers.some(p=>p.id===id))detailViews.delete(id);
  if(changed){detailId=(state.providers.find(p=>p.active)||state.providers[0])?.id||null;detailEpoch++;restoreDetailView(detailId);}
  $('configPath').textContent=state.config.dir||'';$('dataPath').textContent=state.dataDir||'';
  syncStartupSettings();
  navigate(['settings','sessions'].includes(page)?page:detailId?'details':'providers');
  if(page==='details'&&(changed||currentDetail?.revision!==state.providers.find(p=>p.id===detailId)?.revision))renderDetail(selectedCache()||null);
}
function renderProviders(){
  if(draggedId)return;
  if(!state.providers.length){$('providerList').innerHTML='<div class="sidebar-empty">还没有中转<br>点击上方添加，或导入当前配置</div>';return;}
  $('providerList').innerHTML=state.providers.map((p,i)=>{
    const busy=[...pending].some(k=>k.startsWith(p.id+':')),today=matchingCache(p.id,'today'),week=matchingCache(p.id,'7');
    const entry=week&&(!today||Date.parse(week.at)>Date.parse(today.summary?.checkedAt||today.checkedAt||today.at))?week:today,snapshot=entry?.summary||entry,balance=snapshot?.balance;
    const balanceLabel=Number.isFinite(balance?.value)?`余额 ${balanceText(balance)} USD`:busy&&!entry?'正在查询余额':balance?.message?'余额暂不可用':'余额未查询';
    const balanceClass=Number.isFinite(balance?.value)?(balance.value<0?' negative':' positive'):'';
    return `<article class="provider-card${p.active?' active':''}${page==='details'&&p.id===detailId?' is-viewing':''}" data-id="${esc(p.id)}"><button class="drag-handle" data-tooltip="拖动排序；Alt + ↑ / ↓ 也可移动" aria-label="拖动排序 ${esc(p.name)}"${reorderBusy?' disabled':''}><svg viewBox="0 0 16 24" width="11" height="18" fill="currentColor" aria-hidden="true"><circle cx="5" cy="6" r="1.3"/><circle cx="11" cy="6" r="1.3"/><circle cx="5" cy="12" r="1.3"/><circle cx="11" cy="12" r="1.3"/><circle cx="5" cy="18" r="1.3"/><circle cx="11" cy="18" r="1.3"/></svg></button><button class="provider-select" data-action="details" aria-current="${page==='details'&&p.id===detailId?'true':'false'}" aria-label="查看 ${esc(p.name)} 的余额与用量"><span class="avatar tone-${i%4}">${esc(p.name.slice(0,1))}</span><span class="provider-info"><span class="provider-title"><strong data-tooltip="${esc(p.name)}">${esc(p.name)}</strong>${p.active?'<span class="badge live">使用中</span>':''}</span><span class="provider-subtitle${balanceClass}" data-tooltip="${esc(balance?.message||balance?.source||balanceLabel)}">${esc(balanceLabel)}</span></span></button><button class="icon-button refresh-provider" data-action="refresh" data-tooltip="刷新${esc(p.name)}今天的余额与用量" aria-label="刷新${esc(p.name)}"${busy?' disabled aria-busy="true"':''}>↻</button></article>`;
  }).join('');
}
let draftDetection=null;
function updateAccountForm(){
  const choice=$('adapter').value,p=state.providers.find(p=>p.id===$('providerId').value),type=choice==='auto'?(draftDetection?.adapter||p?.detectedAdapter):choice;
  $('adapterHint').textContent=choice==='auto'?(type?'自动识别 · '+(type==='sub2api'?'Sub2API':'NewAPI'):'点击连通性测试后识别，无需先保存。'):'已手动指定接口类型。';
  $('newapiFields').classList.toggle('hidden',type==='sub2api');const needsId=type==='newapi'&&Boolean($('accountKey').value.trim())&&!$('clearAccount').checked;$('accountUserId').required=needsId;$('accountUserId').disabled=type==='sub2api';$('userIdRequirement').textContent=needsId?'必填':'按需填写';$('userIdRequirement').className='field-tag '+(needsId?'required':'optional');
  $('accountHelp').textContent='按需填写账户查询信息，完成后可在下方测试连接。';
}
function draftQuery(){return {baseUrl:$('baseUrl').value.trim(),siteUrl:siteFollowsBase?'':$('siteUrl').value.trim(),key:$('apiKey').value.trim(),adapter:$('adapter').value,accountKey:$('clearAccount').checked?'':$('accountKey').value.trim(),accountUserId:$('accountUserId').value.trim(),quotaPerUnit:$('quotaPerUnit').value,includeAccount:$('accountOptions').open};}
function invalidateDetection(){detectEpoch++;draftDetection=null;const results=$('connectionResults');if(results)results.innerHTML='<span class="connection-idle">点击测试连接与权限</span>';for(const id of ['baseUrl','apiKey','siteUrl','accountKey','accountUserId','adapter'])$(id).removeAttribute('aria-invalid');const button=$('testConnection');if(button){button.disabled=false;button.textContent='连通性测试';}}
async function testConnectionFromForm(){
  if(!$('providerDialog').open||!$('accountOptions').open)return;
  const epoch=++detectEpoch,input=draftQuery(),signature=JSON.stringify(input),button=$('testConnection');
  button.disabled=true;button.textContent='正在检测…';$('connectionResults').innerHTML='<span class="connection-inline running"><i></i>正在检查连接与权限…</span>';
  for(const id of ['baseUrl','apiKey','siteUrl','accountKey','accountUserId','adapter'])$(id).removeAttribute('aria-invalid');
  const current=()=>epoch===detectEpoch&&$('providerDialog').open&&$('accountOptions').open&&signature===JSON.stringify(draftQuery());
  try{const result=await call('testConnection',input);if(!current())return;draftDetection=result;updateAccountForm();
    const errors=(result.checks||[]).filter(item=>item.status==='error'),warnings=(result.checks||[]).filter(item=>item.status==='warning'),issue=errors[0]||warnings[0],status=errors.length?'error':warnings.length?'warning':'success';
    const details=(result.checks||[]).map(item=>item.label+'：'+item.message).join('\n'),full=issue?issue.label+'：'+issue.message:'连接正常'+(result.label?' · '+result.label:'');
    const summary=full.length>110?full.slice(0,107)+'…':full;
    $('connectionResults').innerHTML='<span class="connection-inline '+status+'" data-tooltip="'+esc(details)+'"><i></i>'+esc(summary)+(errors.length>1?'（另有 '+(errors.length-1)+' 项问题）':'')+'</span>';
    for(const item of result.checks||[])if(item.status==='error'&&$(item.field))$(item.field).setAttribute('aria-invalid','true');
  }catch(error){if(current())$('connectionResults').innerHTML='<span class="connection-inline error"><i></i>'+esc(error.message)+'</span>';}
  finally{if(current()){button.disabled=false;button.textContent='重新测试';}}
}
async function openEditor(id){
  invalidateDetection();modelPicker.reset();const epoch=++editorEpoch,p=id?await call('providerDetails',{id}):null;if(epoch!==editorEpoch)return;$('providerForm').reset();$('providerId').value=p?.id||'';$('formTitle').textContent=p?'编辑中转':'添加中转';$('providerName').value=p?.name||'';$('baseUrl').value=p?.baseUrl||'';$('adapter').value=p?.adapter||'auto';$('modelName').value=p?.model||'';$('modelName').placeholder=state.config.model?'留空沿用 '+state.config.model:'留空保留 Codex 默认设置';$('modelHelp').textContent='应用时留空将沿用 Codex 当前模型'+(state.config.model?'（目前：'+state.config.model+'）':'')+'；填写后则使用指定模型。';$('reasoning').value=p?.reasoning||'';$('accountUserId').value=p?.accountUserId||'';$('quotaPerUnit').value=p?.quotaPerUnit||500000;
  const derived=(p?.baseUrl||'').replace(/\/+$/,'').replace(/\/v1$/i,'');siteFollowsBase=!p||!p.siteUrl||p.siteUrl===derived;$('siteUrl').value=p?.siteUrl||derived;$('notes').value=p?.notes||'';$('apiKey').value=p?.key||'';$('accountKey').value=p?.accountKey||'';setSecretVisibility('apiKey',false);setSecretVisibility('accountKey',false);
  $('apiKey').required=!p;$('apiKeyRequirement').textContent=p?'已保存，可留空':'必填';$('apiKeyRequirement').className='field-tag '+(p?'optional':'required');$('apiKey').placeholder='sk-…';$('accountKey').placeholder='仅账户余额接口需要时填写';$('keyHint').textContent=p?'已填入保存的密钥，默认隐藏。点击小眼睛查看，修改后保存即可替换；清空后保存会保留原密钥。':'用于 Codex 调用和记录查询，默认隐藏，可点小眼睛查看。';$('accountOptions').open=Boolean(p);$('formError').textContent='';updateAccountForm();for(const id of ['adapter','reasoning'])filterPickers.get(id)?.sync();$('providerDialog').showModal();$('providerName').focus();
}
function tableCard(title,headers,rows,empty='没有可显示的数据'){return `<section class="table-card"><h2>${esc(title)}</h2><div class="table-scroll">${rows.length?`<table><thead><tr>${headers.map(h=>`<th>${esc(h)}</th>`).join('')}</tr></thead><tbody>${rows.map(row=>'<tr>'+row.map(cell=>`<td>${cell}</td>`).join('')+'</tr>').join('')}</tbody></table>`:`<div class="table-empty">${esc(empty)}</div>`}</div></section>`;}
function trendRows(){const u=currentDetail?.usage;if(!u)return [];const hourly=u.mode==='logs'&&(chartGranularity==='hour'||chartGranularity==='auto'&&u.range?.start===u.range?.end);return [...(hourly?u.hourly||[]:u.daily||[])].sort((a,b)=>a.name.localeCompare(b.name));}
let chartHover;
function renderCharts(){
  chartHover?.hide();
  const u=currentDetail?.usage;if(!$('trendChart')||!u)return;
  const hourly=u.mode==='logs'&&(chartGranularity==='hour'||chartGranularity==='auto'&&u.range?.start===u.range?.end);
  document.querySelectorAll('[data-grain]').forEach(button=>{button.classList.toggle('selected',button.dataset.grain===chartGranularity);button.setAttribute('aria-pressed',String(button.dataset.grain===chartGranularity));button.disabled=button.dataset.grain==='hour'&&u.mode!=='logs';});
  $('trendChart').innerHTML=RelayCharts.trend(hourly?u.hourly||[]:u.daily||[],{hidden:[...hiddenSeries],hourly});
}
function renderDetail(r,loading=false){
  chartHover?.hide();
  currentDetail=r;
  updateRefreshTime();
  syncModelFilter(r?.usage?.modelNames||[]);
  if(!r){$('detailBody').innerHTML='<div class="empty-state"><div class="empty-icon">↻</div><h2>'+(loading?'正在查询余额与记录':'尚未查询余额与用量')+'</h2><p>'+(loading?'首次连接可能需要几秒钟。':'点击上方“刷新数据”，查看余额、缓存率与使用记录。')+'</p></div>';return;}
  const u=r.usage,summary=r.summary||r,b=r.balance||summary.balance,t=u?.totals||summary.totals,excluded=t?.excluded??t?.missing??0;
  const metric=(label,value,note,featured=false)=>`<div class="metric-card${featured?' featured':''}"><div class="metric-label">${esc(label)}</div><div class="metric-value">${value}</div><div class="metric-note" data-tooltip="${esc(note)}">${esc(note)}</div></div>`;
  let html=`<div class="metrics-grid">${metric(usageLabel()+'消费',balanceText({value:t?.cost})+(Number.isFinite(t?.cost)?'<small>USD</small>':''),Number.isFinite(t?.cost)?'当前 API 密钥 · 实际扣费':'接口未完整提供费用数据')}${metric('Token 缓存命中率',rate(t?.tokenRate),excluded?`${excluded} 条或组无效数据已排除`:'按输入 Token 用量加权')}${metric('缓存读取 Token',compact(t?.read),`总输入 ${compact(t?.input)}`)}${metric('请求数量',fmt(t?.requests),u?.mode==='summary'?'所选时间段汇总':`请求命中率 ${rate(t?.requestRate)}`)}</div>`;
  if(r.usageError)html+=`<div class="notice error">记录查询失败：${esc(r.usageError)}</div>`;
  html+=`<div class="detail-meta"><strong>用量分析 <small>${esc(rangeLabel(rangePeriod))}</small></strong></div>`;
  if(u)html+=`<div class="notice">${esc(u.note)} ${esc(u.costNote||'')}</div>`;
  if(u){
    const grainControl=u.mode==='logs'?'<div class="grain-control" role="group" aria-label="趋势粒度"><button data-grain="auto" aria-pressed="true">自动</button><button data-grain="hour" aria-pressed="false">按时</button><button data-grain="day" aria-pressed="false">按天</button></div>':'';
    html+=`<div class="analytics-grid analytics-wide"><section class="chart-card"><div class="chart-heading"><h2>Token 使用趋势</h2>${grainControl}</div><div id="trendChart"></div></section></div>`;
    if(u.mode==='logs')html+=tableCard('近期请求',['时间','模型','输入 Token','输出 Token','缓存读取','缓存写入','缓存率'],u.logs.map(l=>[l.timestamp?esc(new Date(l.timestamp*1000).toLocaleString('zh-CN',{hour12:false})):'—',esc(l.model),fmt(l.input),fmt(l.output),fmt(l.read),fmt(l.write),`<span class="rate-cell" data-tooltip="${esc(l.reason||'')}">${rate(l.rate)}</span>`]),'暂无近期请求。');
  }
  if(!u)html+='<div class="detail-placeholder"><span class="secure-dot"></span> 已显示上次保存的数据，点击刷新可加载完整用量与趋势。</div>';
  $('detailBody').innerHTML=html;renderCharts();syncRefreshControls();
}
async function fetchProvider(id,period=rangePeriod,model='all'){
  const token=id+':'+period+':'+model;if(requests.has(token))return requests.get(token);pending.add(token);renderProviders();
  const task=(async()=>{try{const result=await call('inspect',{id,period,model});const p=state.providers.find(p=>p.id===id);if(!p||p.revision!==result.revision)return null;if(p.adapter==='auto'&&result.adapter)p.detectedAdapter=result.adapter;const previous=matchingCache(id,period);cache.set(detailCacheKey(id,period,model),result);if(model!=='all'&&previous)cache.set(detailCacheKey(id,period),{...previous,balance:result.balance,at:result.at});persistDetails();return result;}finally{pending.delete(token);requests.delete(token);renderProviders();}})();
  requests.set(token,task);return task;
}
async function refreshAll(){
  if(allBusy)return;allBusy=true;let failures=0;const ids=state.providers.filter(p=>p.active).map(p=>p.id),requestedPeriod='today';
  const worker=async()=>{while(ids.length){const id=ids.shift();try{await fetchProvider(id,requestedPeriod);if(page==='details'&&detailId===id&&state.providers.find(p=>p.id===id)?.active&&rangePeriod==='7')await fetchProvider(id,'7');}catch{failures++;}}};
  try{await Promise.all([worker(),worker()]);if(page==='details'&&['today','7'].includes(rangePeriod)&&$('modelFilter').value==='all'&&state.providers.find(p=>p.id===detailId)?.active)renderDetail(matchingCache(detailId)||currentDetail);if(failures)toast(`${failures} 个中转查询失败，请手动重试。`,true);}finally{allBusy=false;nextRefresh=Date.now()+60000;syncRefreshControls();}

}
async function openDetails(id){
  if(!state.providers.some(p=>p.id===id))return;
  const changed=detailId!==id;detailId=id;detailEpoch++;
  if(changed)restoreDetailView(id);
  $('refreshDetail').disabled=[...pending].some(key=>key.startsWith(id+':'));navigate('details');
  renderDetail(selectedCache()||null);
  syncRefreshControls();
}
function reportRefreshFailure(id,result){
  const name=state.providers.find(p=>p.id===id)?.name||'中转';
  const usageError=result.usageError||(!result.usage?'接口未返回用量数据。':'');
  if(usageError)toast(`${name} 用量刷新失败：${usageError}`,true);
  else if(!Number.isFinite(result.balance?.value))toast(`${name} 余额刷新失败：${result.balance?.message||'接口未返回账户余额。'}`,true);
}
async function refreshDetails(notifyFailure=true){
  if(!detailId)return;const id=detailId,epoch=++detailEpoch,model=$('modelFilter').value,period=$('period').value;$('refreshDetail').disabled=true;if(!currentDetail)renderDetail(null,true);
  try{const result=await fetchProvider(id,period,model);if(result&&notifyFailure)reportRefreshFailure(id,result);if(result&&detailId===id&&epoch===detailEpoch&&period===rangePeriod){renderSelectedHeader();renderDetail(result);}}catch(e){if(notifyFailure||epoch===detailEpoch)toast(`${state.providers.find(p=>p.id===id)?.name||'中转'} 刷新失败：${e.message}`,true);if(epoch===detailEpoch){const saved=selectedCache();if(saved)renderDetail(saved);else $('detailBody').innerHTML=`<div class="notice error">${esc(e.message)}</div>`;}}finally{if(epoch===detailEpoch)$('refreshDetail').disabled=false;nextRefresh=Date.now()+60000;}
}
window.openDetails=openDetails;window.refreshDetails=refreshDetails;
document.querySelectorAll('[data-nav]').forEach(b=>b.addEventListener('click',()=>{detailEpoch++;navigate(b.dataset.nav);}));
$('addProvider').onclick=()=>openEditor().catch(e=>toast(e.message,true));$('closeDialog').onclick=$('cancelDialog').onclick=closeEditor;$('adapter').onchange=()=>{invalidateDetection();updateAccountForm();};
function setSecretVisibility(id,visible){const input=$(id),button=$(id+'Eye'),label=id==='apiKey'?'API 密钥':'账户访问令牌';input.type=visible?'text':'password';button.setAttribute('aria-pressed',String(visible));button.setAttribute('aria-label',(visible?'隐藏':'显示')+label);button.dataset.tooltip=(visible?'隐藏':'显示')+label;button.classList.toggle('revealed',visible);}
function clearEditorSecrets(){for(const id of ['apiKey','accountKey']){$(id).value='';setSecretVisibility(id,false);}}
function closeEditor(){invalidateDetection();modelPicker.reset();for(const id of ['adapter','reasoning'])filterPickers.get(id)?.close();editorEpoch++;clearEditorSecrets();$('providerDialog').close();}
for(const id of ['apiKey','accountKey'])$(id+'Eye').addEventListener('click',()=>setSecretVisibility(id,$(id).type==='password'));
function derivedSite(value){if(!value.trim())return '';try{const u=new URL(/^[a-z][a-z\d+.-]*:\/\//i.test(value)?value:'https://'+value);return u.origin+u.pathname.replace(/\/+$/,'').replace(/\/v1$/i,'');}catch{return '';}}
$('baseUrl').addEventListener('input',()=>{if(siteFollowsBase)$('siteUrl').value=derivedSite($('baseUrl').value);});$('siteUrl').addEventListener('input',()=>{siteFollowsBase=!$('siteUrl').value.trim();});
for(const id of ['baseUrl','apiKey','siteUrl','accountKey','accountUserId','quotaPerUnit','clearAccount'])$(id).addEventListener('input',()=>{invalidateDetection();updateAccountForm();});
$('accountOptions').addEventListener('toggle',()=>{invalidateDetection();updateAccountForm();});
$('testConnection').addEventListener('click',testConnectionFromForm);
async function importCurrent(){try{setState(await call('importCurrent'));toast('已导入当前中转。可编辑接口类型，或点击应用写入 Codex。');await refreshAll();}catch(e){toast(e.message,true);}}
$('importCurrent').onclick=importCurrent;
$('providerForm').addEventListener('submit',async e=>{
  e.preventDefault();$('formError').textContent='';$('saveProvider').disabled=true;
  const input={id:$('providerId').value||undefined,name:$('providerName').value,baseUrl:$('baseUrl').value,adapter:$('adapter').value,key:$('apiKey').value,model:$('modelName').value,reasoning:$('reasoning').value,accountKey:$('accountKey').value,accountUserId:$('accountUserId').value,quotaPerUnit:$('quotaPerUnit').value,siteUrl:siteFollowsBase?'':$('siteUrl').value,notes:$('notes').value,clearAccount:$('clearAccount').checked};
  try{setState(await call('saveProvider',input));closeEditor();toast('中转已保存。点击“应用”切换 Codex 配置。');}catch(error){$('formError').textContent=error.message;}finally{$('saveProvider').disabled=false;}
});
$('providerDialog').addEventListener('cancel',e=>{e.preventDefault();closeEditor();});
$('providerDialog').addEventListener('close',()=>{if(!$('providerDialog').open)clearEditorSecrets();});
async function handleProviderAction(e){
  const b=e.target.closest('[data-action]');if(!b)return;const id=b.closest('[data-id]')?.dataset.id,p=state.providers.find(p=>p.id===id);
  try{switch(b.dataset.action){case'refresh':b.disabled=true;if(page==='details'&&detailId===id&&rangePeriod==='today')await refreshDetails();else{const result=await fetchProvider(id,'today');if(result)reportRefreshFailure(id,result);}break;case'edit':await openEditor(id);break;case'details':if(!draggedId)await openDetails(id);break;case'switch':b.disabled=true;setState(await call('switchProvider',{id}));toast(`已应用 ${p.name}。重新启动 Codex，或在 CLI 开启新会话后生效。`);await openDetails(id);break;case'delete':if(await confirmAction('删除中转',`删除“${p.name}”及其保存的密钥？当前 Codex 配置不会因此更改。`)){setState(await call('deleteProvider',{id}));for(const key of cache.keys())if(key.startsWith(id+':'))cache.delete(key);toast('中转已删除。');if(detailId)await openDetails(detailId);}break;}}catch(error){toast((b.dataset.action==='refresh'?`${p?.name||'中转'} 刷新失败：`:'')+error.message,true);}finally{b.disabled=false;}
}
$('providerList').addEventListener('click',handleProviderAction);
$('detailActions').addEventListener('click',handleProviderAction);
function syncRangeControls(){const source=$('period');source.querySelectorAll('[data-custom-range]').forEach(o=>o.remove());if(rangePeriod.startsWith('range:')){const option=new Option(rangeLabel(rangePeriod),rangePeriod);option.dataset.customRange='true';source.insertBefore(option,source.querySelector('[value=custom]'));}source.value=rangePeriod;filterPickers.get('period')?.sync();}
async function applyRange(period){RelayAdapters.range(period);rangePeriod=period;syncRangeControls();rememberDetailView();detailEpoch++;renderProviders();if(page==='details'){renderDetail(selectedCache()||null);await refreshDetails();}else await refreshAll();}
function cancelDateSelection(){$('dateDialog').close();syncRangeControls();}
function onRangeChange(event){if(event.target.value==='custom'){const current=RelayAdapters.range(rangePeriod),today=RelayAdapters.day(Date.now()/1000);$('dateStart').max=$('dateEnd').max=today;$('dateStart').value=current.start;$('dateEnd').value=current.end;$('dateError').textContent='';$('dateDialog').showModal();}else applyRange(event.target.value).catch(e=>toast(e.message,true));}
$('dateForm').addEventListener('submit',async event=>{event.preventDefault();try{const period='range:'+$('dateStart').value+':'+$('dateEnd').value;RelayAdapters.range(period);$('dateDialog').close();await applyRange(period);}catch(e){$('dateError').textContent=e.message;}});
$('closeDate').onclick=$('cancelDate').onclick=cancelDateSelection;$('dateDialog').addEventListener('cancel',e=>{e.preventDefault();cancelDateSelection();});
$('dateStart').addEventListener('change',()=>{if($('dateEnd').value<$('dateStart').value)$('dateEnd').value=$('dateStart').value;});
$('detailBody').addEventListener('click',event=>{const series=event.target.closest('[data-series]'),grain=event.target.closest('[data-grain]');if(series){const key=series.dataset.series;if(hiddenSeries.has(key))hiddenSeries.delete(key);else hiddenSeries.add(key);rememberDetailView();renderCharts();}if(grain){chartGranularity=grain.dataset.grain;rememberDetailView();renderCharts();}});
let sortFeedbackTimer;
function showSortFeedback(){clearTimeout(sortFeedbackTimer);$('sortFeedback').textContent='顺序已保存';$('sortFeedback').classList.add('visible');sortFeedbackTimer=setTimeout(()=>$('sortFeedback').classList.remove('visible'),1900);}
async function persistOrder(ids){
  if(reorderBusy)return;const previous=state.providers.map(p=>p.id);if(ids.every((id,i)=>id===previous[i]))return;
  reorderBusy=true;state.providers=ids.map(id=>state.providers.find(p=>p.id===id));
  try{setState(await call('reorderProviders',{ids}));showSortFeedback();}
  catch(e){setState(await call('state'));toast(e.message,true);throw e;}
  finally{reorderBusy=false;renderProviders();}
}
const providerSorter=new AnimatedProviderSort($('providerList'),{
  disabled:()=>reorderBusy,
  onBusy:id=>{draggedId=id;if(id){clearTimeout(sortFeedbackTimer);$('sortFeedback').classList.remove('visible');}else renderProviders();},
  onCommit:persistOrder,
  onError:e=>toast(e.message,true),
  onSettled:(id,cancelled,keyboard)=>{
    const card=$('providerList').querySelector('[data-id="'+CSS.escape(id)+'"]');if(!card)return;
    card.querySelector('.drag-handle')?.focus({preventScroll:true});
    if(!cancelled&&!matchMedia('(prefers-reduced-motion: reduce)').matches)card.animate([{boxShadow:'0 0 0 2px #ed703f22'},{boxShadow:'0 0 0 0px #ed703f00'}],{duration:360,easing:'ease-out'});
  }
});
chartHover=new RelayChartHover($('detailBody'),{getUsage:()=>currentDetail?.usage,getRows:trendRows,disabled:()=>Boolean(draggedId)});
const appTooltips=new RelayTooltips();
const startupCard=document.createElement('div');
startupCard.className='settings-card startup-settings';
startupCard.innerHTML='<h2>启动方式</h2><p>控制 Codex Relay Lite 是否随 Windows 登录启动，以及开机后是否直接收起到托盘。</p><div class="preference-list"><label class="preference-row"><span><strong>开机自动启动</strong><small>当前 Windows 用户登录时启动程序。</small></span><input id="launchAtLogin" type="checkbox" class="preference-switch"></label><label class="preference-row" id="silentStartupRow"><span><strong>静默启动</strong><small>开机后直接在托盘运行，不显示主窗口。</small></span><input id="silentStartup" type="checkbox" class="preference-switch"></label></div>';
$('settingsPage').prepend(startupCard);
let startupSettingsBusy=false;
function syncStartupSettings(){const settings=state.appSettings||{};$('launchAtLogin').checked=Boolean(settings.launchAtLogin);$('launchAtLogin').disabled=startupSettingsBusy;$('silentStartup').checked=Boolean(settings.silentStartup);$('silentStartup').disabled=startupSettingsBusy||!settings.launchAtLogin;$('silentStartupRow').classList.toggle('preference-disabled',!settings.launchAtLogin);startupCard.setAttribute('aria-busy',String(startupSettingsBusy));}
async function setStartupSetting(key,value){if(startupSettingsBusy)return;startupSettingsBusy=true;$('launchAtLogin').disabled=true;$('silentStartup').disabled=true;startupCard.setAttribute('aria-busy','true');try{setState(await call('setAppSetting',{key,value}));toast(key==='launchAtLogin'?(value?'已启用开机自动启动。':'已关闭开机自动启动。'):(value?'已启用静默启动。':'已关闭静默启动。'));}catch(error){toast(error.message,true);}finally{startupSettingsBusy=false;syncStartupSettings();}}
$('launchAtLogin').addEventListener('change',event=>setStartupSetting('launchAtLogin',event.target.checked));
$('silentStartup').addEventListener('change',event=>setStartupSetting('silentStartup',event.target.checked));
for(const id of ['adapter','reasoning']){
  const source=$(id),label=source.closest('label').querySelector('.field-label'),root=document.createElement('div');
  label.id=id+'Label';root.id=id+'Picker';root.className='filter-picker editor-select';source.before(root);source.classList.add('filter-source');source.tabIndex=-1;source.setAttribute('aria-hidden','true');
  enhanceFilter(id,'');source.addEventListener('change',()=>filterPickers.get(id).sync());
}
const modelPicker=new RelayModelPicker({fetch:input=>call('listModels',input),getCredentials:()=>({baseUrl:$('baseUrl').value.trim(),key:$('apiKey').value.trim()}),isOpen:()=>$('providerDialog').open});
$('chooseConfigDir').onclick=async()=>{try{setState(await call('chooseConfigDir'));}catch(e){toast(e.message,true);}};$('openDataDir').onclick=async()=>{try{await call('openDataDir');}catch(e){toast(e.message,true);}};
$('refreshDetail').onclick=()=>refreshDetails();$('period').onchange=onRangeChange;$('modelFilter').onchange=()=>{rememberDetailView();refreshDetails();};
const dateIcon='<svg class="filter-icon" viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="5" width="18" height="16" rx="3"/><path d="M7 3v4M17 3v4M3 10h18M8 14h2M14 14h2M8 17h2"/></svg>';
enhanceFilter('period',dateIcon);
enhanceFilter('modelFilter','<svg class="filter-icon" viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m12 3 9 5-9 5-9-5 9-5ZM3 12l9 5 9-5M3 16l9 5 9-5"/></svg>');
$('pauseAuto').onclick=()=>{auto=!auto;$('pauseAuto').textContent=auto?'暂停自动刷新':'继续自动刷新';$('pauseAuto').setAttribute('aria-pressed',String(!auto));$('pauseAuto').dataset.tooltip=(auto?'暂停':'继续')+'使用中中转的自动刷新';nextRefresh=Date.now()+60000;updateCountdown();};
function syncRefreshControls(){
  updateRefreshTime();
  const editing=(($('providerDialog').open||$('confirmDialog').open||$('dateDialog').open)&&document.visibilityState!=='hidden')||draggedId||reorderBusy;
  $('refreshDetail').disabled=!detailId||[...pending].some(key=>key.startsWith(detailId+':'));
  return editing;
}
function updateCountdown(){const editing=syncRefreshControls();if(auto&&!editing&&Date.now()>=nextRefresh&&!pending.size&&!allBusy){nextRefresh=Date.now()+60000;if(page==='details'&&state.providers.find(p=>p.id===detailId)?.active&&rangePeriod==='today')refreshDetails(false);else refreshAll();}}
setInterval(updateCountdown,1000);
window.appReady=(async()=>{try{if(window.relay.detailViewState)for(const [id,value] of Object.entries(await call('detailViewState')))detailViews.set(id,value);setState(await call('state'));window.relayUiReady=true;if(detailId){await openDetails(detailId);if(!currentDetail?.usage||!cacheFresh(currentDetail))await refreshDetails(false);}}catch(error){$('providerList').innerHTML=`<div class="notice error">${esc(error.message)}</div>`;toast(error.message,true);}})();
