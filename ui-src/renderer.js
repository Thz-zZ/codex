'use strict';
const $=id=>document.getElementById(id);
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fmt=v=>Number.isFinite(v)?Number(v).toLocaleString('en-US'):'—';
const compactFormat=new Intl.NumberFormat('en-US',{notation:'compact',maximumFractionDigits:2});
const compact=v=>Number.isFinite(v)?compactFormat.format(v):'—';
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
  const latest=syncResults.get('local'),at=latest?.at||currentDetail?.usageAt,age=refreshAge(at);
  const label=age?'上次同步：'+age:'尚未同步';
  if($('detailRefreshTime').textContent!==label)$('detailRefreshTime').textContent=label;
  $('detailRefreshTime').dataset.tooltip=at?dateTime(at):'';
  const localBusy=[...pending].some(key=>key.startsWith('local:')&&!key.endsWith(':balance')),status=$('detailSyncStatus');
  const message=localBusy?'正在同步本地记录…':latest?.message||'同步本地记录';
  if(status.textContent!==message)status.textContent=message;
  status.dataset.state=localBusy?'busy':latest?(latest.error?'error':'success'):'idle';
  status.dataset.tooltip=latest?.details||'';
}
const balanceEnabled=p=>['newapi','sub2api'].includes(p?.adapter);
const balanceText=b=>Number.isFinite(b?.value)?b.value.toLocaleString('en-US',{minimumFractionDigits:2,maximumFractionDigits:4}):'—';
const BALANCE_SYNC_INTERVAL=5*60*1000;
const LOCAL_SYNC_INTERVAL=60*1000;
let state={providers:[],history:[],config:{}},page='providers',detailId=null,detailEpoch=0,balanceRefreshTimer=null,localRefreshTimer=null,stateRefreshPromise=null,allBusy=false,toastTimer,editorEpoch=0,siteFollowsBase=true,rangePeriod='today',draggedId=null,reorderBusy=false,currentDetail=null,detectEpoch=0;
const cache=new RelayDetailCache(),pending=new Set(),requests=new Map(),detailViews=new Map(),balanceSummaries=new Map();
const syncResults=new Map(),balanceSyncResults=new Map();
let officialBusy=false,generationBusy=false,generationEpoch=0;
function recordSyncResult(id,result,mode){
  const provider=state.providers.find(p=>p.id===id);if(mode==='balance'&&!provider)return;
  (mode==='balance'?balanceSyncResults:syncResults).set(mode==='balance'?id:'local',{...RelaySyncStatus.describe(result,mode,provider?.adapter!=='none'),at:result.syncAt||result.at,revision:mode==='balance'?provider.revision:state.localUsageScope});
}
function showSyncResult(id,mode='local'){
  const result=(mode==='balance'?balanceSyncResults:syncResults).get(mode==='balance'?id:'local');if(!result)return;
  toast(result.message+(result.details?'；'+result.details:''),result.error);$('toast').classList.add('sync-result');
}
let detailViewSave=Promise.resolve();
const detailCacheKey=(period,model='all')=>'local:'+period+(model==='all'?'':':model:'+model);
function rememberDetailView(){
  detailViews.set('local',{period:rangePeriod,model:$('modelFilter').value||'all'});
  if(window.relay.detailViewState)detailViewSave=call('detailViewState',Object.fromEntries(detailViews)).catch(error=>toast(error.message,true));
}
function syncModelFilter(models=[],selection=$('modelFilter').value||'all'){
  const names=[...new Set([...models,...(selection==='all'?[]:[selection])])];
  $('modelFilter').innerHTML='<option value="all">全部模型</option>'+names.map(m=>`<option value="${esc(m)}">${esc(m)}</option>`).join('');$('modelFilter').value=selection;
  filterPickers.get('modelFilter')?.sync();
}
function restoreDetailView(){
  const saved=detailViews.get('local');
  rangePeriod=saved?.period||'today';try{RelayAdapters.range(rangePeriod);}catch{rangePeriod='today';}
  syncRangeControls();syncModelFilter([],saved?.model||'all');
}
const detailStorageKey='relay-detail-cache-local-v4';
let detailCacheHydrated=false;
function validCache(value){try{return value.id==='local'&&value.revision===state.localUsageScope&&value.range?.end===RelayAdapters.range(value.period).end;}catch{return false;}}
function rememberBalance(value){
  const summary=value.summary||value,at=summary.checkedAt||summary.at||value.at,previous=balanceSummaries.get(value.id);
  if(!previous||previous.revision!==value.revision||Date.parse(at)>=Date.parse(previous.at)){
    const hasBalance=Number.isFinite(summary.balance?.value),saved=previous?.revision===value.revision?previous:null;
    balanceSummaries.set(value.id,{id:value.id,revision:value.revision,at,balance:hasBalance?summary.balance:saved?.balance||summary.balance,balanceAt:hasBalance?(summary.balanceAt||value.balanceAt||at):saved?.balanceAt,balanceError:summary.balanceError||value.balanceError||(!hasBalance?summary.balance?.message:'')});
  }
}
function hydrateDetails(force=false){
  if(detailCacheHydrated&&!force)return;detailCacheHydrated=true;
  try{for(const r of JSON.parse(localStorage.getItem(detailStorageKey)||'[]'))if(validCache(r)&&['today','7'].includes(r.period)&&r.usage){
    delete r.history;
    const key=detailCacheKey(r.period,r.model||'all'),previous=cache.peek(key);
    if(!previous||Date.parse(r.at)>=Date.parse(previous.summary?.checkedAt||previous.checkedAt||previous.at))cache.set(key,RelayDetailCache.merge(previous,r));
    else if(!previous.usage)cache.set(key,RelayDetailCache.merge(r,previous));
  }}catch{}
  persistDetails();
}
function persistDetails(){try{const values=[...cache].filter(([,r])=>['today','7'].includes(r.period)&&r.usage&&validCache(r)).sort(([,a],[,b])=>Date.parse(b.at)-Date.parse(a.at)).slice(0,6),saved=[];let size=0;for(const [key,r] of values){const length=cache.byteSize(key)/2;if(length>512000||size+length>2000000)continue;saved.push(r);size+=length;}localStorage.setItem(detailStorageKey,JSON.stringify(saved));}catch{}}
const rangeLabel=period=>({today:'今天',yesterday:'昨天','7':'最近 7 天','30':'最近 30 天',all:'全部记录 / 近 90 天'})[period]||(period.startsWith('range:')?period.slice(6).replace(':',' ～ '):period);
function matchingCache(period=rangePeriod,model='all',touch=true){const key=detailCacheKey(period,model),value=cache.peek(key);if(value?.period!==period||!validCache(value))return null;return touch?cache.get(key):value;}
function selectedCache(){return matchingCache(rangePeriod,$('modelFilter').value||'all');}
const filterPickers=new Map();
function enhanceFilter(id,icon){
  const source=$(id),root=$(id+'Picker'),menuId=id+'Menu',valueId=id+'Value';
  root.innerHTML=`<button type="button" class="filter-trigger" role="combobox" aria-haspopup="listbox" aria-expanded="false" aria-controls="${menuId}" aria-labelledby="${id}Label ${valueId}">${icon}<span id="${valueId}" class="filter-value"></span><svg class="filter-chevron" viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.5" aria-hidden="true"><path d="m4 6 4 4 4-4"/></svg></button><div id="${menuId}" class="filter-menu hidden" role="listbox" aria-labelledby="${id}Label"></div>`;
  const trigger=root.querySelector('button'),menu=$(menuId),hints=['period','listPeriod'].includes(id)?{all:'本地会话记录 · 最多近 90 天',today:'北京时间 00:00 起',yesterday:'前一天的用量','7':'过去 7 天的用量','30':'过去 30 天的用量',custom:'选择某一天或起止日期'}:{};
  let active=0,search='',searchAt=0;
  const options=()=>Array.from(source.options);
  const enabledIndexes=()=>options().flatMap((option,index)=>option.disabled?[]:[index]);
  const close=()=>{menu.classList.add('hidden');trigger.setAttribute('aria-expanded','false');trigger.removeAttribute('aria-activedescendant');root.classList.remove('is-open');};
  const highlight=()=>{const items=menu.querySelectorAll('[role=option]');items.forEach((item,i)=>item.classList.toggle('is-focused',i===active));const item=items[active];if(item){trigger.setAttribute('aria-activedescendant',item.id);item.scrollIntoView({block:'nearest'});}};
  const sync=()=>{const opts=options();active=Math.max(0,source.selectedIndex);$(valueId).textContent=opts[active]?.textContent||'全部模型';trigger.dataset.tooltip=$(valueId).textContent;trigger.disabled=source.disabled;trigger.setAttribute('aria-required',String(source.required));root.classList.toggle('is-required-empty',source.required&&!source.value);if(source.validity.valid)trigger.removeAttribute('aria-invalid');menu.innerHTML=opts.map((o,i)=>`<div id="${id}Option${i}" role="option" aria-selected="${o.selected}" aria-disabled="${o.disabled}" class="filter-option${o.selected?' is-selected':''}" data-index="${i}"><span class="filter-option-copy"><span>${esc(o.textContent)}</span>${hints[o.value]?`<small>${esc(hints[o.value])}</small>`:''}</span><svg class="filter-check" viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><path d="m3 8 3 3 7-7"/></svg></div>`).join('');close();};
  const open=()=>{for(const picker of filterPickers.values())picker.close();document.dispatchEvent(new CustomEvent('relay-dropdown-open',{detail:id}));menu.classList.remove('hidden');root.classList.add('is-open');trigger.setAttribute('aria-expanded','true');const box=root.getBoundingClientRect(),container=root.closest('.dialog-body')?.getBoundingClientRect(),top=Math.max(0,container?.top||0),bottom=Math.min(innerHeight,container?.bottom||innerHeight),below=bottom-box.bottom-12,above=box.top-top-12,up=below<140&&above>below;menu.style.top=up?'auto':'calc(100% + 6px)';menu.style.bottom=up?'calc(100% + 6px)':'auto';menu.style.maxHeight=Math.max(40,Math.min(248,up?above:below))+'px';active=source.selectedOptions[0]?.disabled?(enabledIndexes()[0]??-1):source.selectedIndex;highlight();};
  const choose=index=>{if(!source.options[index]||source.options[index].disabled)return;const changed=source.selectedIndex!==index;source.selectedIndex=index;sync();trigger.focus();if(changed)source.dispatchEvent(new Event('change',{bubbles:true}));};
  trigger.addEventListener('click',()=>menu.classList.contains('hidden')?open():close());
  menu.addEventListener('pointerdown',event=>event.preventDefault());
  // Prevent the enclosing label from clicking the trigger again after selection.
  menu.addEventListener('click',event=>{const option=event.target.closest('[data-index]');if(option){event.preventDefault();choose(Number(option.dataset.index));}});
  trigger.addEventListener('keydown',event=>{const isOpen=!menu.classList.contains('hidden'),enabled=enabledIndexes(),position=enabled.indexOf(active);if(event.key==='Tab'){close();return;}if(event.key==='Escape'){if(isOpen){event.preventDefault();event.stopPropagation();close();}return;}if(['ArrowDown','ArrowUp','Home','End'].includes(event.key)){event.preventDefault();if(!isOpen)open();else if(enabled.length&&event.key==='ArrowDown')active=enabled[(position+1)%enabled.length];else if(enabled.length&&event.key==='ArrowUp')active=enabled[(position+enabled.length-1)%enabled.length];if(event.key==='Home')active=enabled[0]??-1;if(event.key==='End')active=enabled[enabled.length-1]??-1;highlight();}else if(['Enter',' '].includes(event.key)){event.preventDefault();if(isOpen)choose(active);else open();}else if(event.key.length===1&&!event.ctrlKey&&!event.metaKey&&!event.altKey){event.preventDefault();if(!isOpen)open();search=Date.now()-searchAt>700?event.key:search+event.key;searchAt=Date.now();const index=options().findIndex(o=>!o.disabled&&o.textContent.toLowerCase().startsWith(search.toLowerCase()));if(index>=0){active=index;highlight();}}});
  root.addEventListener('focusout',event=>{if(!root.contains(event.relatedTarget))close();});
  document.addEventListener('pointerdown',event=>{if(!root.contains(event.target))close();});
  document.addEventListener('relay-dropdown-open',event=>{if(event.detail!==id)close();});
  filterPickers.set(id,{sync,close,open});sync();
}
async function call(name,input){const result=await window.relay[name](input);if(!result.ok)throw new Error(result.error);return result.data;}
function toast(message,error=false){clearTimeout(toastTimer);$('toast').textContent=message;$('toast').className='toast'+(error?' error':'');toastTimer=setTimeout(()=>$('toast').classList.add('hidden'),error?9000:6500);}
function confirmAction(title,text){return new Promise(resolve=>{$('confirmTitle').textContent=title;$('confirmText').textContent=text;$('confirmDialog').showModal();let done=false;const finish=value=>{if(done)return;done=true;$('confirmDialog').close();$('confirmDialog').removeEventListener('cancel',cancel);resolve(value);};const cancel=e=>{e.preventDefault();finish(false);};$('confirmCancel').onclick=()=>finish(false);$('confirmOkay').onclick=()=>finish(true);$('confirmDialog').addEventListener('cancel',cancel);});}
function setState(value){
  const scopeChanged=state.localUsageScope!==value.localUsageScope;state=value;
  if(scopeChanged){cache.clear();syncResults.clear();currentDetail=null;detailEpoch++;detailCacheHydrated=false;}
  for(const [id,entry] of balanceSyncResults)if(!state.providers.some(p=>p.id===id&&p.revision===entry.revision))balanceSyncResults.delete(id);
  for(const [id,entry] of balanceSummaries)if(!state.providers.some(p=>p.id===id&&p.revision===entry.revision))balanceSummaries.delete(id);
  for(const [key,entry] of cache)if(!validCache(entry))cache.delete(key);
  for(const summary of state.usageCache||[])rememberBalance(summary);
  hydrateDetails();renderState();
}
function cacheFresh(value){const record=value?.summary||value,at=Date.parse(record?.balanceAt||record?.checkedAt||record?.at);return Number.isFinite(at)&&Date.now()-at>=0&&Date.now()-at<BALANCE_SYNC_INTERVAL;}
function navigate(target){
  const previousPage=page;
  page=target==='providers'?'details':target;
  closeProviderMenu();
  for(const picker of filterPickers.values())picker.close();
  for(const name of ['providers','details','settings','sessions'])$(name+'Page').classList.toggle('hidden',name!==page);
  const main=document.querySelector('main');main.classList.toggle('sessions-layout',page==='sessions');if(page==='sessions')main.scrollTop=0;
  document.querySelectorAll('[data-nav]').forEach(b=>b.classList.toggle('selected',b.dataset.nav===page));
  renderSelectedHeader();renderProviders();
  window.RelaySessions?.onNavigate(page);
  if(page==='details'&&previousPage!==page&&!matchMedia('(prefers-reduced-motion: reduce)').matches)$('detailsPage').animate([{opacity:.72,transform:'translateY(3px)'},{opacity:1,transform:'translateY(0)'}],{duration:160,easing:'ease-out'});
}
function renderSelectedHeader(){
  $('pageHeader').classList.toggle('hidden',page==='details');
  $('pageTitle').textContent=page==='settings'?'设置':page==='sessions'?'会话管理':'中转工作台';
  $('detailActions').classList.toggle('hidden',page!=='sessions');
  if(page!=='sessions')$('detailActions').replaceChildren();
}
function renderState(){
  $('providerCount').textContent=state.providers.length;
  const changed=!state.providers.some(p=>p.id===detailId);
  if(changed){detailId=(state.providers.find(p=>p.active)||state.providers[0])?.id||null;}
  $('configPath').textContent=state.config.dir||'';$('dataPath').textContent=state.dataDir||'';
  syncStartupSettings();
  navigate(['settings','sessions'].includes(page)?page:'details');
  if(page==='details'&&!currentDetail)renderDetail(selectedCache()||null);
}
function renderProviders(){
  const official=Boolean(state.config.official),officialButton=$('switchOfficial');
  officialButton.classList.toggle('active',official);officialButton.disabled=officialBusy;officialButton.setAttribute('aria-pressed',String(official));
  $('officialStatus').textContent=officialBusy?'切换中…':official?'使用中':'';
  if(draggedId)return;
  if(!state.providers.length){$('providerList').innerHTML='<div class="sidebar-empty">还没有中转<br>点击左上角“＋”添加，或导入当前配置</div>';return;}
  $('providerList').innerHTML=state.providers.map((p,i)=>{
    const busy=[...pending].some(k=>k.startsWith(p.id+':')),entry=balanceSummaries.get(p.id)||(state.usageCache||[]).find(r=>r.id===p.id&&r.revision===p.revision);
    const snapshot=entry?.summary||entry,balance=snapshot?.balance;
    const balanceLabel=p.adapter==='none'?'模型调用 · 未启用余额':Number.isFinite(balance?.value)?`余额 ${balanceText(balance)} USD`:busy&&!entry?'正在查询余额':balance?.message?'余额暂不可用':'余额未查询';
    const balanceClass=Number.isFinite(balance?.value)?(balance.value<0?' negative':' positive'):'';
    return `<article class="provider-card${p.active?' active':''}" data-id="${esc(p.id)}"><button class="drag-handle" data-tooltip="拖动排序；Alt + ↑ / ↓ 也可移动" aria-label="拖动排序 ${esc(p.name)}"${reorderBusy?' disabled':''}><svg viewBox="0 0 16 24" width="11" height="18" fill="currentColor" aria-hidden="true"><circle cx="5" cy="6" r="1.3"/><circle cx="11" cy="6" r="1.3"/><circle cx="5" cy="12" r="1.3"/><circle cx="11" cy="12" r="1.3"/><circle cx="5" cy="18" r="1.3"/><circle cx="11" cy="18" r="1.3"/></svg></button><button class="provider-select" data-action="details" aria-current="${page==='details'&&p.id===detailId?'true':'false'}" aria-label="查看 ${esc(p.name)} 的余额与用量"><span class="avatar tone-${i%4}">${esc(p.name.slice(0,1))}</span><span class="provider-info"><span class="provider-title"><strong data-tooltip="${esc(p.name)}">${esc(p.name)}</strong>${p.active?'<span class="badge live">使用中</span>':''}</span><span class="provider-subtitle${balanceClass}" data-tooltip="${esc(balanceSyncResults.get(p.id)?.error?balanceSyncResults.get(p.id).details:entry?.balanceError||balance?.message||balance?.source||balanceLabel)}">${esc(balanceLabel)}</span></span></button><button class="icon-button refresh-provider" data-action="refresh" data-tooltip="同步${esc(p.name)}的账户余额" aria-label="同步${esc(p.name)}"${busy||!balanceEnabled(p)?' disabled':''}${busy?' aria-busy="true"':''}>↻</button></article>`;
  }).join('');
}
function requiresCodexSetup(provider){
  return Boolean(provider&&(provider.requiresCodexSetup||(provider.modelFamily&&provider.modelFamily!=='codex')||(provider.protocol&&provider.protocol!=='responses')));
}
function providerSetupNotice(provider){
  if(requiresCodexSetup(provider))return '<section class="notice error model-usage"><strong>旧中转需重新配置</strong><div>本应用现在仅支持 Codex Responses 直连。原记录和密钥仍保留；请编辑并确认上游支持 Responses，可使用内置模型或获取中转列表后保存、应用。</div></section>';
  return '';
}
function updateModelForm(){
  $('modelHelp').textContent='不获取时使用 Codex 内置模型列表和默认模型；获取后请选择默认模型，不锁定其他模型。应用后需重启 Codex。';
  modelPicker.syncMode();syncModelRequirement();
  for(const option of $('reasoning').options)option.disabled=!option.value;
  if(!$('reasoning').value)$('reasoning').value='default';
  $('reasoningHelp').textContent='默认强度可选，不指定时跟随 Codex；指定后仅作为默认，仍可在 Codex 内选择 low、medium、high、xhigh、max、ultra，实际支持情况由中转和模型决定。';
  filterPickers.get('reasoning')?.sync();
}
function updateAccountForm(){
  const enabled=$('balanceEnabled').checked,type=$('adapter').value,selected=enabled&&['newapi','sub2api'].includes(type);
  $('balanceConfig').classList.toggle('hidden',!enabled);$('balanceFields').classList.toggle('hidden',!selected);
  $('adapter').disabled=!enabled;$('adapter').required=enabled;
  for(const id of ['accountKey','clearAccount','siteUrl'])$(id).disabled=!selected;
  $('newapiFields').classList.toggle('hidden',type!=='newapi');
  const needsId=selected&&type==='newapi'&&Boolean($('accountKey').value.trim())&&!$('clearAccount').checked;
  $('accountUserId').required=needsId;$('accountUserId').disabled=!selected||type!=='newapi';$('quotaPerUnit').disabled=!selected||type!=='newapi';
  $('userIdRequirement').textContent=needsId?'必填':'按需填写';$('userIdRequirement').className='field-tag '+(needsId?'required':'optional');
  $('accountHelp').textContent=type==='newapi'?'使用 API 调用密钥查询余额；若站点需要账户访问令牌，请同时填写纯数字用户 ID。':'使用 API 调用密钥查询余额；填写账户访问令牌后优先读取账户余额。';
  filterPickers.get('adapter')?.sync();
}
function draftQuery(){return {baseUrl:$('baseUrl').value.trim(),siteUrl:siteFollowsBase?'':$('siteUrl').value.trim(),key:$('apiKey').value.trim(),modelFamily:'codex',protocol:'responses',adapter:$('balanceEnabled').checked?$('adapter').value:'none',accountKey:$('clearAccount').checked?'':$('accountKey').value.trim(),accountUserId:$('accountUserId').value.trim(),quotaPerUnit:$('quotaPerUnit').value,includeAccount:$('balanceEnabled').checked};}
function invalidateDetection(){detectEpoch++;const results=$('connectionResults');if(results)results.innerHTML='<span class="connection-idle">点击测试连接与权限</span>';for(const id of ['baseUrl','apiKey','siteUrl','accountKey','accountUserId','adapter'])$(id).removeAttribute('aria-invalid');$('adapterPicker')?.querySelector('button').removeAttribute('aria-invalid');const button=$('testConnection');if(button){button.disabled=false;button.textContent='连通性测试';}}
async function testConnectionFromForm(){
  if(!$('providerDialog').open||!$('balanceEnabled').checked||!$('adapter').value)return;
  const epoch=++detectEpoch,input=draftQuery(),signature=JSON.stringify(input),button=$('testConnection');
  button.disabled=true;button.textContent='正在检测…';$('connectionResults').innerHTML='<span class="connection-inline running"><i></i>正在检查连接与权限…</span>';
  for(const id of ['baseUrl','apiKey','siteUrl','accountKey','accountUserId'])$(id).removeAttribute('aria-invalid');
  const current=()=>epoch===detectEpoch&&$('providerDialog').open&&$('balanceEnabled').checked&&signature===JSON.stringify(draftQuery());
  try{const result=await call('testConnection',input);if(!current())return;
    const errors=(result.checks||[]).filter(item=>item.status==='error'),warnings=(result.checks||[]).filter(item=>item.status==='warning'),issue=errors[0]||warnings[0],status=errors.length?'error':warnings.length?'warning':'success';
    const details=(result.checks||[]).map(item=>item.label+'：'+item.message).join('\n'),full=issue?issue.label+'：'+issue.message:'连接正常'+(result.label?' · '+result.label:'');
    const summary=full.length>110?full.slice(0,107)+'…':full;
    $('connectionResults').innerHTML='<span class="connection-inline '+status+'" data-tooltip="'+esc(details)+'"><i></i>'+esc(summary)+(errors.length>1?'（另有 '+(errors.length-1)+' 项问题）':'')+'</span>';
    for(const item of result.checks||[])if(item.status==='error'&&$(item.field))$(item.field).setAttribute('aria-invalid','true');
  }catch(error){if(current())$('connectionResults').innerHTML='<span class="connection-inline error"><i></i>'+esc(error.message)+'</span>';}
  finally{if(current()){button.disabled=false;button.textContent='重新测试';}}
}
function generationInput(){return {...draftQuery(),id:$('providerId').value,...modelPicker.selection(),reasoning:$('reasoning').value};}
function invalidateGeneration(){generationEpoch++;$('generationResult').replaceChildren();$('testGeneration').disabled=generationBusy;$('testGeneration').textContent=generationBusy?'测试中…':'测试生成';}
async function testGenerationFromForm(){
  if(!$('providerDialog').open||generationBusy)return;
  const button=$('testGeneration'),result=$('generationResult'),epoch=++generationEpoch;
  let input;try{modelPicker.validate();input=generationInput();}catch(error){result.dataset.state='error';result.textContent=error.message;return;}
  const signature=JSON.stringify(input),current=()=>epoch===generationEpoch&&$('providerDialog').open&&signature===JSON.stringify(generationInput());
  generationBusy=true;button.disabled=true;button.textContent='测试中…';result.dataset.state='running';result.textContent='正在发送短文本请求…';
  try{
    const response=await call('testGeneration',input);if(!current())return;
    result.dataset.state=['success','warning','error'].includes(response.status)?response.status:'error';
    const meta=[response.model,response.httpStatus?'HTTP '+response.httpStatus:'',Number.isFinite(response.elapsedMs)?(response.elapsedMs/1000).toFixed(2)+' 秒':''].filter(Boolean).join(' · ');
    result.textContent=response.message+(meta?'\n'+meta:'');
  }catch(error){if(current()){result.dataset.state='error';result.textContent=error.message;}}
  finally{generationBusy=false;button.disabled=false;button.textContent=current()?'重新测试生成':'测试生成';}
}
async function openEditor(id){
  invalidateGeneration();
  invalidateDetection();modelPicker.reset();const epoch=++editorEpoch,p=id?await call('providerDetails',{id}):null;if(epoch!==editorEpoch)return;$('providerForm').reset();$('providerId').value=p?.id||'';$('formTitle').textContent=p?'编辑中转':'添加中转';$('providerName').value=p?.name||'';$('baseUrl').value=p?.baseUrl||'';$('balanceEnabled').checked=balanceEnabled(p);$('adapter').value=balanceEnabled(p)?p.adapter:'';$('modelName').value=requiresCodexSetup(p)?'':p?.model||'';$('reasoning').value=requiresCodexSetup(p)?'default':p?.reasoning||'default';for(const field of ['reasoning'])$(field+'Picker').querySelector('button').removeAttribute('aria-invalid');$('modelName').removeAttribute('aria-invalid');$('accountUserId').value=p?.accountUserId||'';$('quotaPerUnit').value=p?.quotaPerUnit||500000;
  $('legacyProviderNotice').classList.toggle('hidden',!requiresCodexSetup(p));$('legacyProviderNotice').textContent=requiresCodexSetup(p)?'该记录来自旧版非 Codex 配置，原地址和密钥已保留。请确认中转支持 Responses，可直接使用内置默认，或重新获取模型并选择默认项后保存，默认强度可选；取消不会修改原记录。':'';
  const derived=(p?.baseUrl||'').replace(/\/+$/,'').replace(/\/v1$/i,'');siteFollowsBase=!p||!p.siteUrl||p.siteUrl===derived;$('siteUrl').value=p?.siteUrl||derived;$('notes').value=p?.notes||'';$('apiKey').value=p?.key||'';$('accountKey').value=p?.accountKey||'';setSecretVisibility('apiKey',false);setSecretVisibility('accountKey',false);
  $('apiKey').required=!p;$('apiKeyRequirement').textContent=p?'已保存，可留空':'必填';$('apiKeyRequirement').className='field-tag '+(p?'optional':'required');$('apiKey').placeholder='sk-…';$('accountKey').placeholder='仅账户余额接口需要时填写';$('keyHint').textContent=p?'已填入保存的密钥，默认隐藏。点击小眼睛查看，修改后保存即可替换；清空后保存会保留原密钥。':'用于 Codex 调用和记录查询，默认隐藏，可点小眼睛查看。';$('formError').textContent='';updateAccountForm();updateModelForm();if(!requiresCodexSetup(p))modelPicker.restore(p);filterPickers.get('reasoning')?.sync();$('providerDialog').showModal();$('providerName').focus();$('providerDialog').querySelector('.dialog-body').scrollTop=0;
}
function tableCard(title,headers,rows,empty='没有可显示的数据',className=''){return `<section class="table-card ${esc(className)}"><h2>${esc(title)}</h2><div class="table-scroll">${rows.length?`<table><thead><tr>${headers.map(h=>`<th scope="col">${esc(h)}</th>`).join('')}</tr></thead><tbody>${rows.map(row=>'<tr>'+row.map(cell=>`<td>${cell}</td>`).join('')+'</tr>').join('')}</tbody></table>`:`<div class="table-empty">${esc(empty)}</div>`}</div></section>`;}
function renderDetail(r,loading=false){
  currentDetail=r;
  const provider=state.providers.find(item=>item.id===detailId);
  const setupNotice=providerSetupNotice(provider);
  updateRefreshTime();
  syncModelFilter(r?.usage?.modelNames||[]);
  if(!r){$('detailBody').innerHTML=setupNotice+'<div class="empty-state"><div class="empty-icon">↻</div><h2>'+(loading?'正在读取本地记录':'尚未读取本地用量')+'</h2><p>'+(loading?'正在读取 Codex 会话记录。':'点击上方“同步”，读取本地 Token 与缓存用量。')+'</p></div>';return;}
  const u=r.usage,t=u?.totals;
  const metric=(label,value,note,tooltip=note)=>`<div class="metric-card"><div class="metric-label">${esc(label)}</div><div class="metric-value">${value}</div><div class="metric-note" data-tooltip="${esc(tooltip)}">${esc(note)}</div></div>`;
  const cost=t?.cost,missingPrices=u?.pricing?.missingModels||[];
  const costValue=Number.isFinite(cost)?(cost>0&&cost<0.0001?'&lt;0.0001':cost.toLocaleString('en-US',{minimumFractionDigits:2,maximumFractionDigits:4}))+'<small>USD</small>':'—';
  const costNote=missingPrices.length?`${missingPrices.length} 个模型缺少单价`:u?.pricing?'本地估算 · 全部会话':'同步后计算本地成本';
  const costTooltip=`${rangeLabel(rangePeriod)} · 按模型输入、输出和缓存单价估算，覆盖全部本地会话。${u?.pricing?.source?'价格来源：'+u.pricing.source+'。':''}${missingPrices.length?'缺少单价：'+missingPrices.join('、')+'。':''}不代表中转实际扣费。`;
  let html=setupNotice+`<div class="metrics-grid">${metric('总成本',costValue,costNote,costTooltip)}${metric('Token 缓存命中率',rate(t?.tokenRate),'缓存读取 Token ÷ 输入 Token')}${metric('缓存读取 Token',compact(t?.read),`总输入 ${compact(t?.input)}`)}${metric('本地用量记录',fmt(t?.records??t?.requests),'Codex 会话 Token 记录数，不等同于 HTTP 请求数')}</div>`;
  if(r.localUsageError)html+=`<div class="notice error">本地会话记录读取失败：${esc(r.localUsageError)}</div>`;
  if(r.usageStale&&u)html+=`<div class="notice">已保留上次读取的本地用量，数据时间：${esc(dateTime(r.usageAt||r.at))}。</div>`;
  for(const warning of u?.warnings||[])html+=`<div class="notice error">${esc(warning)}</div>`;
  if(u){
    if(u.mode==='local')html+=tableCard('本地 Token 记录',['时间','模型','输入 Token','输出 Token','缓存读取','缓存率'],u.logs.map(l=>[l.timestamp?esc(new Date(l.timestamp*1000).toLocaleString('zh-CN',{hour12:false})):'—',esc(l.model),fmt(l.input),fmt(l.output),fmt(l.read),rate(l.input?l.read/l.input:null)]),'此范围暂无保留的逐条记录；汇总包含全部有效记录。','recent-requests');
  }
  if(!u)html+=`<div class="detail-placeholder"><span class="secure-dot"></span>暂无本地 Token 数据。请确认此设置指向正在使用的 Codex 会话目录。</div>`;
  $('detailBody').innerHTML=html;syncRefreshControls();
}
async function fetchProvider(id,period=rangePeriod,model='all',mode='local'){
  const local=mode==='local',scope=state.localUsageScope,token=(local?'local':id)+':'+period+':'+model+':'+mode;if(requests.has(token))return requests.get(token);pending.add(token);renderProviders();syncRefreshControls();
  const task=(async()=>{try{
    const response=local?await call('localUsage',{period,model}):await call('inspect',{id,period,model,refreshBalance:true,readLocal:false}),p=state.providers.find(p=>p.id===id);
    if(local?(scope!==state.localUsageScope||response.revision!==scope):(!p||p.revision!==response.revision))return null;
    recordSyncResult(id,response,mode);
    if(!local){
      rememberBalance(response);
      if(response.summary)state.usageCache=[...(state.usageCache||[]).filter(r=>r.id!==id),response.summary];
      return response;
    }
    const result=RelayDetailCache.merge(matchingCache(period,model,false),response);
    cache.set(detailCacheKey(period,model),result);persistDetails();return result;
  }catch(error){const p=state.providers.find(p=>p.id===id);if(local?scope===state.localUsageScope:Boolean(p))(local?syncResults:balanceSyncResults).set(local?'local':id,{message:local?'本地用量同步失败':'余额同步失败',details:error.message,error:true,at:new Date().toISOString(),revision:local?scope:p.revision});throw error;}finally{pending.delete(token);requests.delete(token);renderProviders();syncRefreshControls();}})();
  requests.set(token,task);return task;
}
async function refreshAll(){
  if(allBusy)return;allBusy=true;const ids=state.providers.filter(p=>p.active&&balanceEnabled(p)).map(p=>p.id);
  try{for(const id of ids)try{await fetchProvider(id,'today','all','balance');}catch{}}finally{allBusy=false;resetBalanceRefreshTimer();syncRefreshControls();}

}
async function openDetails(id){
  if(id!==null&&!state.providers.some(p=>p.id===id))return;
  detailId=id;const hadLocalDetail=Boolean(currentDetail);navigate('details');
  if(!hadLocalDetail)renderDetail(selectedCache()||null);
  syncRefreshControls();
  if(!hadLocalDetail&&(!currentDetail?.usage||Date.now()-Date.parse(currentDetail.usageAt||currentDetail.at)>=LOCAL_SYNC_INTERVAL))await refreshDetails(false,'local');
}
async function refreshDetails(notifyFailure=true,mode='local'){
  if(!detailId&&mode!=='local')return;const id=detailId,epoch=++detailEpoch,model=$('modelFilter').value,period=$('period').value;$('refreshDetail').disabled=true;if(!currentDetail)renderDetail(null,true);
  try{const result=await fetchProvider(id,period,model,mode);if(result&&notifyFailure)showSyncResult(id,mode);if(result&&mode==='local'&&epoch===detailEpoch&&period===rangePeriod&&model===$('modelFilter').value){renderSelectedHeader();renderDetail(result);}}catch(e){if(notifyFailure)showSyncResult(id,mode);if(epoch===detailEpoch){const saved=selectedCache();if(saved)renderDetail(saved);else $('detailBody').innerHTML=`<div class="notice error">${esc(e.message)}</div>`;}}finally{syncRefreshControls();if(mode!=='local')resetBalanceRefreshTimer();if(mode!=='balance')resetLocalRefreshTimer();}
}
window.openDetails=openDetails;window.refreshDetails=refreshDetails;
document.querySelectorAll('[data-nav]').forEach(b=>b.addEventListener('click',()=>{detailEpoch++;navigate(b.dataset.nav);}));
$('addProvider').onclick=()=>openEditor().catch(e=>toast(e.message,true));$('closeDialog').onclick=$('cancelDialog').onclick=closeEditor;
function setSecretVisibility(id,visible){const input=$(id),button=$(id+'Eye'),label=id==='apiKey'?'API 密钥':'账户访问令牌';input.type=visible?'text':'password';button.setAttribute('aria-pressed',String(visible));button.setAttribute('aria-label',(visible?'隐藏':'显示')+label);button.dataset.tooltip=(visible?'隐藏':'显示')+label;button.classList.toggle('revealed',visible);}
function clearEditorSecrets(){for(const id of ['apiKey','accountKey']){$(id).value='';setSecretVisibility(id,false);}}
function closeEditor(){invalidateGeneration();invalidateDetection();modelPicker.reset();for(const id of ['reasoning','adapter'])filterPickers.get(id)?.close();editorEpoch++;clearEditorSecrets();$('providerDialog').close();}
for(const id of ['apiKey','accountKey'])$(id+'Eye').addEventListener('click',()=>setSecretVisibility(id,$(id).type==='password'));
function derivedSite(value){if(!value.trim())return '';try{const u=new URL(/^[a-z][a-z\d+.-]*:\/\//i.test(value)?value:'https://'+value);return u.origin+u.pathname.replace(/\/+$/,'').replace(/\/v1$/i,'');}catch{return '';}}
$('baseUrl').addEventListener('input',()=>{if(siteFollowsBase)$('siteUrl').value=derivedSite($('baseUrl').value);});$('siteUrl').addEventListener('input',()=>{siteFollowsBase=!$('siteUrl').value.trim();});
for(const id of ['baseUrl','apiKey','siteUrl','accountKey','accountUserId','quotaPerUnit','clearAccount'])$(id).addEventListener('input',()=>{invalidateDetection();updateAccountForm();});
for(const id of ['balanceEnabled','adapter'])$(id).addEventListener('change',()=>{invalidateDetection();updateAccountForm();});
$('testConnection').addEventListener('click',testConnectionFromForm);
$('testGeneration').addEventListener('click',testGenerationFromForm);
for(const id of ['baseUrl','apiKey','modelName','reasoning'])for(const event of ['input','change'])$(id).addEventListener(event,invalidateGeneration);
async function switchOfficialFromMenu(){
  if(officialBusy)return;officialBusy=true;closeProviderMenu(true);renderProviders();
  try{setState(await call('switchOfficial'));toast('已切回 OpenAI 官方。请重启 Codex；尚未登录时请在 Codex 中登录。');}
  catch(error){toast(error.message,true);}
  finally{officialBusy=false;renderProviders();}
}
$('switchOfficial').addEventListener('contextmenu',event=>{event.preventDefault();openProviderMenu(null,event.clientX,event.clientY);});
$('switchOfficial').addEventListener('click',()=>openDetails(null).catch(error=>toast(error.message,true)));
$('switchOfficial').addEventListener('keydown',event=>{
  if(event.key!=='ContextMenu'&&!(event.shiftKey&&event.key==='F10'))return;
  event.preventDefault();const rect=event.currentTarget.getBoundingClientRect();openProviderMenu(null,rect.left+20,rect.bottom);
});
async function importCurrent(){try{const draft=await call('importCurrent');await openEditor();for(const [field,key] of Object.entries({providerName:'name',baseUrl:'baseUrl',apiKey:'key',reasoning:'reasoning'}))$(field).value=draft[key]||(field==='reasoning'?'default':'');updateModelForm();filterPickers.get('reasoning')?.sync();toast('已读取当前 Codex 配置，可直接保存并使用内置默认，也可获取中转模型列表。');}catch(e){toast(e.message,true);}}
$('importCurrent').onclick=importCurrent;
$('providerForm').addEventListener('submit',async e=>{
  e.preventDefault();$('formError').textContent='';$('saveProvider').disabled=true;
  const input={...draftQuery(),id:$('providerId').value||undefined,name:$('providerName').value,...modelPicker.selection(),reasoning:$('reasoning').value,discoveryToken:modelPicker.discoveryToken,notes:$('notes').value,clearAccount:$('clearAccount').checked};
  try{modelPicker.validate();setState(await call('saveProvider',input));closeEditor();toast('中转已保存。右键左侧中转，选择“应用到 Codex”使配置生效。');}catch(error){$('formError').textContent=error.message;}finally{$('saveProvider').disabled=false;}
});
$('providerDialog').addEventListener('cancel',e=>{e.preventDefault();closeEditor();});
$('providerDialog').addEventListener('close',()=>{if(!$('providerDialog').open)clearEditorSecrets();});
async function handleProviderAction(e){
  const b=e.target.closest('[data-action]');if(!b||b.disabled)return;
  if($('providerMenu').contains(b)&&$('providerMenu').dataset.kind==='official'){if(b.dataset.action==='switch')await switchOfficialFromMenu();return;}
  const id=b.closest('[data-id]')?.dataset.id,p=state.providers.find(p=>p.id===id);if(!p)return;
  if($('providerMenu').contains(b))closeProviderMenu(true);
  try{switch(b.dataset.action){case'refresh':b.disabled=true;{const result=await fetchProvider(id,'today','all','balance');if(result)showSyncResult(id,'balance');}break;case'edit':await openEditor(id);break;case'details':if(!draggedId)await openDetails(id);break;case'switch':b.disabled=true;setState(await call('switchProvider',{id}));toast(`已应用 ${p.name}。重新启动 Codex 客户端以加载模型配置，已有会话不会被重置。`);await openDetails(id);break;case'delete':if(await confirmAction('删除中转',`删除“${p.name}”及其保存的密钥？当前 Codex 配置不会因此更改。`)){b.disabled=true;const updated=await call('deleteProvider',{id});if($('providerDialog').open&&$('providerId').value===id)closeEditor();setState(updated);toast('中转已删除。');if(detailId)await openDetails(detailId);}break;}}catch(error){toast((b.dataset.action==='refresh'?`${p?.name||'中转'} 同步失败：`:'')+error.message,true);}finally{if(b.dataset.action==='refresh')resetBalanceRefreshTimer();b.disabled=b.dataset.action==='refresh'&&!balanceEnabled(p);}
}
$('providerList').addEventListener('click',handleProviderAction);
$('providerMenu').addEventListener('click',handleProviderAction);
function closeProviderMenu(restoreFocus=false){
  const menu=$('providerMenu');if(!menu.matches(':popover-open'))return;
  const id=menu.dataset.id;for(const animation of menu.getAnimations())animation.cancel();menu.hidePopover();
  if(restoreFocus)(menu.dataset.kind==='official'?$('switchOfficial'):$('providerList').querySelector(`[data-id="${CSS.escape(id)}"] .provider-select`))?.focus({preventScroll:true});
}
function openProviderMenu(id,x,y){
  const official=id===null,p=official?{name:'OpenAI 官方',active:Boolean(state.config.official)}:state.providers.find(p=>p.id===id);if(!p||draggedId||reorderBusy||official&&officialBusy)return;
  closeProviderMenu();for(const picker of filterPickers.values())picker.close();
  const menu=$('providerMenu'),apply=menu.querySelector('[data-action=switch]'),edit=menu.querySelector('[data-action=edit]'),remove=menu.querySelector('[data-action=delete]');menu.dataset.id=id??'';menu.dataset.kind=official?'official':'relay';menu.setAttribute('aria-label',p.name+'的操作');
  edit.classList.toggle('hidden',official);edit.disabled=official;remove.classList.toggle('hidden',official);
  apply.querySelector('span').textContent=p.active?'重新应用':'应用到 Codex';apply.disabled=requiresCodexSetup(p);
  if(apply.disabled)apply.dataset.tooltip='请先编辑为 Codex';else delete apply.dataset.tooltip;
  remove.disabled=official||Boolean(p.active);
  if(remove.disabled)remove.dataset.tooltip='使用中的中转不能删除，请先切换到其他中转。';else delete remove.dataset.tooltip;
  menu.showPopover();const gap=8,left=Math.max(gap,Math.min(x,innerWidth-menu.offsetWidth-gap)),top=Math.max(gap,Math.min(y,innerHeight-menu.offsetHeight-gap));
  menu.style.left=left+'px';menu.style.top=top+'px';menu.style.transformOrigin=`${Math.max(0,Math.min(x-left,menu.offsetWidth))}px ${Math.max(0,Math.min(y-top,menu.offsetHeight))}px`;
  if(!matchMedia('(prefers-reduced-motion: reduce)').matches){for(const animation of menu.getAnimations())animation.cancel();menu.animate([{opacity:0,transform:'translateY(-4px) scale(.96)'},{opacity:1,transform:'translateY(0) scale(1)'}],{duration:180,easing:'cubic-bezier(.2,.8,.2,1)'});}
  menu.querySelector('button:not(:disabled)').focus({preventScroll:true});
}
$('providerList').addEventListener('contextmenu',event=>{
  const card=event.target.closest('.provider-card');if(!card)return;event.preventDefault();openProviderMenu(card.dataset.id,event.clientX,event.clientY);
});
$('providerList').addEventListener('keydown',event=>{
  if(event.key!=='ContextMenu'&&!(event.shiftKey&&event.key==='F10'))return;
  const card=event.target.closest('.provider-card');if(!card)return;event.preventDefault();const rect=card.getBoundingClientRect();openProviderMenu(card.dataset.id,rect.left+20,rect.bottom);
});
$('providerMenu').addEventListener('keydown',event=>{
  const buttons=[...event.currentTarget.querySelectorAll('button:not(:disabled)')],index=buttons.indexOf(document.activeElement);
  if(event.key==='Escape'){event.preventDefault();closeProviderMenu(true);}
  else if(event.key==='Tab')closeProviderMenu(true);
  else if(['ArrowDown','ArrowUp','Home','End'].includes(event.key)){
    event.preventDefault();buttons[event.key==='Home'?0:event.key==='End'?buttons.length-1:(index+(event.key==='ArrowUp'?buttons.length-1:1))%buttons.length]?.focus();
  }
});
document.addEventListener('pointerdown',event=>{if(!$('providerMenu').contains(event.target))closeProviderMenu();});
window.addEventListener('resize',()=>closeProviderMenu());window.addEventListener('blur',()=>closeProviderMenu());
document.addEventListener('scroll',event=>{if(!$('providerMenu').contains(event.target))closeProviderMenu();},true);
document.addEventListener('visibilitychange',()=>{if(document.hidden)closeProviderMenu();});

function syncRangeControls(){const source=$('period');source.querySelectorAll('[data-custom-range]').forEach(o=>o.remove());if(rangePeriod.startsWith('range:')){const option=new Option(rangeLabel(rangePeriod),rangePeriod);option.dataset.customRange='true';source.insertBefore(option,source.querySelector('[value=custom]'));}source.value=rangePeriod;filterPickers.get('period')?.sync();}
async function applyRange(period){RelayAdapters.range(period);rangePeriod=period;syncRangeControls();rememberDetailView();detailEpoch++;renderProviders();if(page==='details'){renderDetail(selectedCache()||null);await refreshDetails(false,'local');}}
function cancelDateSelection(){$('dateDialog').close();syncRangeControls();}
function onRangeChange(event){if(event.target.value==='custom'){const current=RelayAdapters.range(rangePeriod),today=RelayAdapters.day(Date.now()/1000);$('dateStart').max=$('dateEnd').max=today;$('dateStart').value=current.start;$('dateEnd').value=current.end;$('dateError').textContent='';$('dateDialog').showModal();}else applyRange(event.target.value).catch(e=>toast(e.message,true));}
$('dateForm').addEventListener('submit',async event=>{event.preventDefault();try{const period='range:'+$('dateStart').value+':'+$('dateEnd').value;RelayAdapters.range(period);$('dateDialog').close();await applyRange(period);}catch(e){$('dateError').textContent=e.message;}});
$('closeDate').onclick=$('cancelDate').onclick=cancelDateSelection;$('dateDialog').addEventListener('cancel',e=>{e.preventDefault();cancelDateSelection();});
$('dateStart').addEventListener('change',()=>{if($('dateEnd').value<$('dateStart').value)$('dateEnd').value=$('dateStart').value;});
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
const appTooltips=new RelayTooltips();
const startupCard=document.createElement('div');
startupCard.className='settings-card startup-settings';
startupCard.innerHTML='<h2>启动方式</h2><p>控制 Codex 中转台 是否随 Windows 登录启动，以及开机后是否直接收起到托盘。</p><div class="preference-list"><label class="preference-row"><span><strong>开机自动启动</strong><small>当前 Windows 用户登录时启动程序。</small></span><input id="launchAtLogin" type="checkbox" class="preference-switch"></label><label class="preference-row" id="silentStartupRow"><span><strong>静默启动</strong><small>开机后直接在托盘运行，不显示主窗口。</small></span><input id="silentStartup" type="checkbox" class="preference-switch"></label></div>';
$('settingsPage').prepend(startupCard);
let startupSettingsBusy=false;
function syncStartupSettings(){const settings=state.appSettings||{};$('launchAtLogin').checked=Boolean(settings.launchAtLogin);$('launchAtLogin').disabled=startupSettingsBusy;$('silentStartup').checked=Boolean(settings.silentStartup);$('silentStartup').disabled=startupSettingsBusy||!settings.launchAtLogin;$('silentStartupRow').classList.toggle('preference-disabled',!settings.launchAtLogin);startupCard.setAttribute('aria-busy',String(startupSettingsBusy));}
async function setStartupSetting(key,value){if(startupSettingsBusy)return;startupSettingsBusy=true;$('launchAtLogin').disabled=true;$('silentStartup').disabled=true;startupCard.setAttribute('aria-busy','true');try{setState(await call('setAppSetting',{key,value}));toast(key==='launchAtLogin'?(value?'已启用开机自动启动。':'已关闭开机自动启动。'):(value?'已启用静默启动。':'已关闭静默启动。'));}catch(error){toast(error.message,true);}finally{startupSettingsBusy=false;syncStartupSettings();}}
$('launchAtLogin').addEventListener('change',event=>setStartupSetting('launchAtLogin',event.target.checked));
$('silentStartup').addEventListener('change',event=>setStartupSetting('silentStartup',event.target.checked));
for(const id of ['reasoning','adapter']){
  const source=$(id),label=source.closest('label').querySelector('.field-label'),root=document.createElement('div');
  label.id=id+'Label';root.id=id+'Picker';root.className='filter-picker editor-select';source.before(root);source.classList.add('filter-source');source.tabIndex=-1;source.setAttribute('aria-hidden','true');
  enhanceFilter(id,'');source.addEventListener('change',()=>filterPickers.get(id).sync());
}
function syncModelRequirement(){const input=$('modelName');input.classList.toggle('is-required-empty',input.required&&!input.value.trim());if(input.validity.valid)input.removeAttribute('aria-invalid');}
for(const event of ['input','change'])$('modelName').addEventListener(event,syncModelRequirement);
for(const [id,message] of Object.entries({modelName:'请先获取模型列表，再明确选择默认模型。',adapter:'请选择 NewAPI 或 Sub2API 中转类型。'})){
  $(id).addEventListener('invalid',event=>{event.preventDefault();const target=id==='modelName'?$(id):$(id+'Picker').querySelector('button');target.setAttribute('aria-invalid','true');if($('providerForm').querySelector(':invalid')===$(id)){$('formError').textContent=message;target.focus();target.scrollIntoView({block:'nearest'});}});
}
const modelPicker=new RelayModelPicker({fetch:input=>call('listModels',input),getCredentials:()=>({baseUrl:$('baseUrl').value.trim(),key:$('apiKey').value.trim(),modelFamily:'codex',protocol:'responses'}),isOpen:()=>$('providerDialog').open});
$('chooseConfigDir').onclick=async()=>{try{setState(await call('chooseConfigDir'));}catch(e){toast(e.message,true);}};$('openDataDir').onclick=async()=>{try{await call('openDataDir');}catch(e){toast(e.message,true);}};
$('refreshDetail').onclick=()=>refreshDetails(true,'local');$('period').onchange=onRangeChange;$('modelFilter').onchange=()=>{rememberDetailView();refreshDetails(false,'local');};
const dateIcon='<svg class="filter-icon" viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="5" width="18" height="16" rx="3"/><path d="M7 3v4M17 3v4M3 10h18M8 14h2M14 14h2M8 17h2"/></svg>';
enhanceFilter('period',dateIcon);
enhanceFilter('modelFilter','<svg class="filter-icon" viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m12 3 9 5-9 5-9-5 9-5ZM3 12l9 5 9-5M3 16l9 5 9-5"/></svg>');
function syncRefreshControls(){
  updateRefreshTime();
  const editing=(($('providerDialog').open||$('confirmDialog').open||$('dateDialog').open)&&document.visibilityState!=='hidden')||draggedId||reorderBusy;
  const busy=[...pending].some(key=>key.startsWith('local:')),button=$('refreshDetail');button.disabled=!window.relayUiReady||busy;button.setAttribute('aria-busy',String(busy));button.querySelector('span').textContent=busy?'同步中…':'同步';
  return editing;
}
function runScheduledRefresh(){if(document.hidden||allBusy||pending.size||syncRefreshControls())return;return refreshAll();}
function resetBalanceRefreshTimer(){clearInterval(balanceRefreshTimer);balanceRefreshTimer=setInterval(runScheduledRefresh,BALANCE_SYNC_INTERVAL);}
function runScheduledLocalRefresh(background=false){if(!window.relayUiReady||document.hidden&&!background||[...pending].some(key=>!key.endsWith(':balance'))||syncRefreshControls())return;return refreshDetails(false,'local');}
function resetLocalRefreshTimer(){clearInterval(localRefreshTimer);localRefreshTimer=setInterval(runScheduledLocalRefresh,LOCAL_SYNC_INTERVAL);}
window.runScheduledLocalRefresh=runScheduledLocalRefresh;
resetBalanceRefreshTimer();
resetLocalRefreshTimer();
async function refreshVisibleState(){
  if(!window.relayUiReady)return;
  if(stateRefreshPromise)return stateRefreshPromise;
  stateRefreshPromise=(async()=>{try{setState(await call('state'));if(page==='details')renderDetail(selectedCache()||currentDetail);}catch{}finally{resetBalanceRefreshTimer();resetLocalRefreshTimer();stateRefreshPromise=null;}})();
  return stateRefreshPromise;
}
window.refreshVisibleState=refreshVisibleState;
document.addEventListener('visibilitychange',()=>{if(!document.hidden)refreshVisibleState();});
window.appReady=(async()=>{try{if(window.relay.detailViewState)for(const [id,value] of Object.entries(await call('detailViewState')))detailViews.set(id,value);restoreDetailView();setState(await call('state'));window.relayUiReady=true;if(state.catalogUpgraded)toast('已升级模型目录并备份配置；请重新启动 Codex，加载原生模型设置。');await openDetails(detailId);if(detailId&&!cacheFresh(balanceSummaries.get(detailId)))await refreshAll();}catch(error){$('providerList').innerHTML=`<div class="notice error">${esc(error.message)}</div>`;toast(error.message,true);}})();
