'use strict';
window.RelaySessions=(()=>{
  const root=$('sessionsPage');
  let rows=[],filtered=[],selected=new Set(),current=null,cursor=null,epoch=0,limit=150;
  let busy=false,previewBusy=false,previewJobs=0,loaded=false,config={},inventory=null,plan=null,filter='all',messageCount=0;
  const api=(action,args={})=>call('sessions',{action,args});
  const time=value=>value?new Date(value*1000).toLocaleString('zh-CN',{hour12:false,year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit'}):'';
  const icons={refresh:'<path d="M20 7v5h-5M4 17v-5h5M6 7a7 7 0 0 1 12-1l2 6M4 12l2 6a7 7 0 0 0 12-1"/>',import:'<path d="M12 3v12m-4-4 4 4 4-4M5 16v5h14v-5"/>',search:'<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 4 4"/>'};
  const icon=name=>'<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'+icons[name]+'</svg>';
  root.innerHTML=`<div class="session-overview"><div class="session-counts" id="sessionCounts">本地会话</div><div class="session-data-actions"><button id="sessionReadDetails" class="button subtle small">读取详情</button><button id="sessionSettings" class="button small">读取设置</button></div><div id="sessionHome" class="session-home"></div></div>
    <div class="session-searchbar"><label class="session-search">${icon('search')}<input id="sessionSearch" type="search" placeholder="搜索会话、项目路径或 ID" aria-label="搜索会话"></label><div class="session-filters" role="group" aria-label="会话类型"><button data-session-filter="all" aria-pressed="true">全部</button><button data-session-filter="ordinary" aria-pressed="false">普通</button><button data-session-filter="archived" aria-pressed="false">已归档</button></div></div>
    <div class="session-workspace"><section class="session-list-panel" aria-label="会话列表"><div class="session-selection"><label><input id="sessionSelectAll" type="checkbox">全选筛选结果</label><span id="sessionSelectionCount">已选 0</span><button id="sessionExport" class="button small" disabled>导出</button><button id="sessionDeleteSelected" class="button small session-danger" disabled>删除</button></div><div id="sessionList" class="session-list"><div class="session-empty">正在准备本地会话…</div></div><div class="session-list-footer"><span id="sessionListCount">0 个会话</span><button id="sessionMoreRows" class="button subtle small hidden">加载更多</button></div></section>
    <section class="session-preview-panel" aria-label="会话预览"><div class="session-preview-heading"><div><h2 id="sessionPreviewTitle">选择一个会话</h2><p id="sessionPreviewMeta">点击左侧会话，查看用户与 Codex 的对话。</p><code id="sessionPreviewId"></code></div><button id="sessionDeleteOne" class="button small session-danger hidden">删除此会话</button></div><div class="session-preview-tools"><span id="sessionMessageCount">对话记录</span><label><input id="sessionShowTools" type="checkbox">工具详情</label></div><div id="sessionMessages" class="session-messages"><div class="session-preview-empty"><span>▤</span><strong>浏览你的本地对话</strong><p>对话按时间顺序展示，支持继续加载。</p></div></div><div class="session-preview-footer"><span id="sessionPreviewNote"></span><button id="sessionMoreMessages" class="button small hidden">加载后续内容</button></div></section></div>
    <div class="session-status"><span id="sessionStatus" role="status" aria-live="polite">准备读取会话</span><span class="session-write-hint">导入或删除前，请关闭其他 Codex 客户端</span></div>`;
  document.body.insertAdjacentHTML('beforeend',`<dialog id="sessionSettingsDialog" class="session-dialog"><form id="sessionSettingsForm"><div class="dialog-header"><div><div class="eyebrow">LOCAL SESSIONS</div><h2>会话读取设置</h2></div><button type="button" class="icon-button" data-session-close="sessionSettingsDialog" aria-label="关闭">×</button></div><div class="dialog-body"><label>Codex 数据目录<div class="session-path-input"><input id="sessionHomeInput" placeholder="留空沿用当前 Codex 配置目录"><button id="sessionBrowseHome" type="button" class="button">浏览</button></div><small>包含 sessions、state_*.sqlite 等会话数据的目录。</small></label><label>Codex 程序<div class="session-path-input"><input id="sessionExeInput" placeholder="留空自动检测 codex.exe"><button id="sessionBrowseExe" type="button" class="button">浏览</button></div><small>预览优先使用官方接口，程序不可用时仍可读取本地记录。</small></label><label class="session-readonly"><input id="sessionReadOnly" type="checkbox"><span>只读模式<small>允许浏览和导出，禁用导入及永久删除。</small></span></label><p id="sessionSettingsError" class="form-error" role="alert"></p></div><div class="dialog-footer"><button type="button" class="button" data-session-close="sessionSettingsDialog">取消</button><button id="sessionSaveSettings" class="button primary" type="submit">保存设置</button></div></form></dialog>
    <dialog id="sessionPlanDialog" class="session-dialog"><div class="dialog-header"><div><div class="eyebrow">SESSION MANAGEMENT</div><h2 id="sessionPlanTitle"></h2></div></div><div class="dialog-body"><p id="sessionPlanNote" class="session-plan-note"></p><div id="sessionPlanList" class="session-plan-list"></div><label id="sessionAckLabel" class="session-ack"><input id="sessionPlanAck" type="checkbox">我已核对以上列表，确认永久删除且不备份</label></div><div class="dialog-footer"><button id="sessionPlanCancel" class="button">取消</button><button id="sessionPlanConfirm" class="button primary" disabled>确认</button></div></dialog>
    <dialog id="sessionInfoDialog" class="session-dialog"><div class="dialog-header"><h2 id="sessionInfoTitle"></h2></div><div class="dialog-body"><pre id="sessionInfoText" class="session-info-text"></pre></div><div class="dialog-footer"><button class="button" data-session-close="sessionInfoDialog">关闭</button></div></dialog>`);

  function status(text,error=false){$('sessionStatus').textContent=text;$('sessionStatus').classList.toggle('error',error);}
  function info(title,text){$('sessionInfoTitle').textContent=title;$('sessionInfoText').textContent=text;if(!$('sessionInfoDialog').open)$('sessionInfoDialog').showModal();}
  function controls(){
    const n=selected.size,writeBlocked=busy||previewBusy||config.readOnly;
    $('sessionSelectionCount').textContent='已选 '+n;
    $('sessionExport').disabled=busy||!n;
    $('sessionDeleteSelected').disabled=writeBlocked||!config.available||!n;
    $('sessionDeleteOne').disabled=writeBlocked||!config.available||!current;
    $('sessionSettings').disabled=busy||previewBusy;
    $('sessionSelectAll').disabled=busy||!filtered.length;
    $('sessionSelectAll').checked=Boolean(filtered.length)&&filtered.every(s=>selected.has(s.id));
    $('sessionSelectAll').indeterminate=filtered.some(s=>selected.has(s.id))&&!$('sessionSelectAll').checked;
    for(const id of ['sessionImport','sessionRefresh'])if($(id))$(id).disabled=busy||(id==='sessionImport'&&(previewBusy||config.readOnly));
    root.setAttribute('aria-busy',String(busy));
  }
  async function run(label,work){
    if(busy)return null;busy=true;status(label);controls();
    try{return await work();}catch(error){status('操作未完成：'+error.message,true);info('操作未完成',error.message);return null;}
    finally{busy=false;controls();}
  }
  function clearPreview(){
    epoch++;current=null;cursor=null;messageCount=0;
    $('sessionPreviewTitle').textContent='选择一个会话';$('sessionPreviewMeta').textContent='点击左侧会话，查看用户与 Codex 的对话。';$('sessionPreviewId').textContent='';
    $('sessionDeleteOne').classList.add('hidden');$('sessionMoreMessages').classList.add('hidden');$('sessionPreviewNote').textContent='';$('sessionMessageCount').textContent='对话记录';
    $('sessionMessages').innerHTML='<div class="session-preview-empty"><span>▤</span><strong>浏览你的本地对话</strong><p>对话按时间顺序展示，支持继续加载。</p></div>';
    controls();
  }
  function renderRows(){
    const container=$('sessionList'),scroll=container.scrollTop;
    container.innerHTML=filtered.length?filtered.slice(0,limit).map(s=>`<article class="session-row${s.id===current?.id?' is-current':''}"><input type="checkbox" data-session-check="${esc(s.id)}" aria-label="选择 ${esc(s.title)}"${selected.has(s.id)?' checked':''}${busy?' disabled':''}><button class="session-open" data-session-open="${esc(s.id)}" aria-current="${s.id===current?.id}"><strong data-tooltip="${esc(s.title)}">${s.is_child?'<span class="session-child">↳</span> ':''}${esc(s.title||'未命名会话')}</strong><span class="session-row-project" data-tooltip="${esc(s.cwd||'无项目目录')}">${esc(s.cwd||'无项目目录')}</span><span class="session-row-meta"><span>${esc(time(s.updated))}</span>${s.archived?'<span class="session-row-badge">已归档</span>':''}${s.status?`<span class="session-row-badge${s.status==='内容缺失'?' missing':''}">${esc(s.status)}</span>`:''}</span></button></article>`).join(''):'<div class="session-empty">'+(rows.length?'没有匹配的会话':'此目录暂无本地会话')+'</div>';
    container.scrollTop=scroll;
    $('sessionListCount').textContent=`显示 ${Math.min(filtered.length,limit)} / ${filtered.length} 个会话`;
    $('sessionMoreRows').classList.toggle('hidden',limit>=filtered.length);controls();
  }
  function applyFilter(){
    selected.clear();limit=150;clearPreview();
    const query=$('sessionSearch').value.trim().normalize('NFKC').toLowerCase();
    filtered=rows.filter(s=>(filter==='all'||(filter==='archived'?s.archived:!s.archived))&&(!query||(s.title+' '+s.cwd+' '+s.id).normalize('NFKC').toLowerCase().includes(query)));
    root.querySelectorAll('[data-session-filter]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.sessionFilter===filter)));
    renderRows();$('sessionList').scrollTop=0;
  }
  async function load(){
    config=await api('settings');
    const result=await api('list');inventory=result;rows=result.sessions;loaded=true;
    const c=result.counts;
    $('sessionCounts').innerHTML=`<strong>${fmt(c.total)}</strong> 个会话 <span>普通 ${fmt(c.ordinary)}</span><span>已归档 ${fmt(c.archived)}</span>${config.readOnly?'<span class="session-readonly-tag">只读模式</span>':''}`;
    $('sessionHome').textContent=result.home;$('sessionHome').dataset.tooltip=result.home;applyFilter();
    status(`已读取 ${c.total} 个会话`+(result.warnings.length?` · ${result.warnings.length} 项读取提示`:' · 所有本地数据源读取正常'));
  }
  async function refresh(){await run('正在读取本地会话…',load);}
  async function preview(session,reset=true){
    const generation=reset?++epoch:epoch;
    if(reset){current=session;cursor=null;messageCount=0;$('sessionMessages').innerHTML='';$('sessionMessageCount').textContent='对话记录';
      $('sessionPreviewTitle').textContent=session.title;$('sessionPreviewMeta').textContent=(session.cwd||'无项目目录')+(time(session.updated)?' · '+time(session.updated):'');$('sessionPreviewId').textContent=session.id;
      $('sessionDeleteOne').classList.remove('hidden');renderRows();$('sessionMessages').scrollTop=0;}
    previewJobs++;previewBusy=true;controls();$('sessionMoreMessages').disabled=true;$('sessionPreviewNote').textContent='正在载入对话…';
    try{
      const result=await api('preview',{id:session.id,tools:$('sessionShowTools').checked,cursor});
      if(generation!==epoch||current?.id!==session.id)return;
      const markup=result.messages.map(m=>{
        if(m.role!=='tool')messageCount++;
        const heading=`<span>${m.role==='user'?'用户':m.role==='assistant'?'Codex':esc(m.kind||'工具活动')}</span>${m.time?'<time>'+esc(time(m.time))+'</time>':''}`;
        return m.role==='tool'?`<details class="session-message tool"><summary>${heading}</summary><pre>${esc(m.text)}</pre></details>`:`<article class="session-message ${m.role==='user'?'user':'assistant'}"><div class="session-message-heading">${heading}</div><pre>${esc(m.text)}</pre></article>`;
      }).join('');
      $('sessionMessages').insertAdjacentHTML('beforeend',markup);cursor=result.cursor;
      $('sessionMessageCount').textContent='对话记录 · '+messageCount+' 条';
      $('sessionMoreMessages').classList.toggle('hidden',!cursor);
      $('sessionPreviewNote').textContent=result.notice||(cursor?'按时间顺序展示 · 还有后续内容':'已显示全部内容');
      if(!$('sessionMessages').children.length)$('sessionMessages').innerHTML='<div class="session-empty">暂无可显示的对话内容</div>';
    }catch(error){if(generation===epoch)$('sessionPreviewNote').textContent='读取失败：'+error.message;}
    finally{previewJobs--;previewBusy=previewJobs>0;$('sessionMoreMessages').disabled=previewBusy;controls();}
  }
  function openPlan(kind,data){
    plan={kind,...data};const deleting=kind==='delete';
    $('sessionPlanTitle').textContent=deleting?`永久删除 ${data.affected} 个会话？`:`导入 ${data.importing} 个会话`;
    $('sessionPlanNote').textContent=deleting?`已选 ${data.selected} 个，另有 ${data.affected-data.selected} 个派生子会话将一并删除。\n不保留备份，删除后无法恢复。项目文件和工作目录不在删除范围内。`:`包内 ${data.total} 个会话，将导入 ${data.importing} 个，跳过已有 ${data.existing} 个。保留标题、对话与归档状态，不覆盖已有会话。`;
    $('sessionPlanList').innerHTML=data.sessions.map(s=>`<div><span class="session-plan-tag">${deleting?(s.selected?'已选':'连带删除'):(s.exists?'跳过已有':'导入')}</span><strong>${esc(s.title)}</strong><code>${esc(s.id)}</code></div>`).join('');
    $('sessionAckLabel').classList.toggle('hidden',!deleting);$('sessionPlanAck').checked=false;
    $('sessionPlanConfirm').textContent=deleting?'永久删除 '+data.affected+' 个会话':'导入 '+data.importing+' 个会话';
    $('sessionPlanConfirm').classList.toggle('session-danger-primary',deleting);$('sessionPlanConfirm').disabled=deleting||!data.importing;
    $('sessionPlanDialog').showModal();
  }
  function closePlan(cancel=true){const old=plan;plan=null;$('sessionPlanDialog').close();if(cancel&&old)api('cancelPlan',{token:old.token}).catch(()=>{});}
  async function prepareDelete(ids){if(!ids.length||busy||previewBusy)return;const data=await run('正在核对占用情况和删除范围…',()=>api('prepareDelete',{ids}));if(data)openPlan('delete',data);}
  async function prepareImport(){const data=await run('正在校验会话导出包…',()=>api('prepareImport'));if(data)openPlan('import',data);else if(!busy&&$('sessionStatus').textContent==='正在校验会话导出包…')status('已取消导入。');}
  async function executePlan(){
    if(!plan||$('sessionPlanConfirm').disabled)return;const action=plan;closePlan(false);
    const deleting=action.kind==='delete';const result=await run(deleting?'正在永久删除会话…':'正在导入会话…',()=>api(deleting?'executeDelete':'executeImport',{token:action.token,confirmed:true}));
    if(!result)return;
    const failures=Object.entries(result.failures||{});
    const summary=deleting?`已确认删除 ${result.succeeded.length} 个会话，${failures.length} 个未完成。`:`已导入 ${result.succeeded.length} 个，跳过 ${result.skipped.length} 个，${failures.length} 个未完成。`;
    info(deleting?'删除结果':'导入结果',summary+(result.stopped?'\n\n'+result.stopped:'')+(failures.length?'\n\n'+failures.map(([id,reason])=>id+'\n'+reason).join('\n\n'):''));
    await refresh();status(summary,Boolean(failures.length));
  }
  async function showSettings(){
    if(busy)return;
    const settings=await run('正在读取会话设置…',()=>api('settings'));if(!settings)return;
    $('sessionHomeInput').value=settings.followsConfig?'':settings.home;$('sessionExeInput').value=settings.executable||'';$('sessionReadOnly').checked=Boolean(settings.readOnly);$('sessionSettingsError').textContent='';$('sessionSettingsDialog').showModal();status('会话读取设置');
  }
  function showDetails(){
    if(!inventory){info('读取详情','还没有读取会话，请先刷新列表。');return;}
    const c=inventory.counts;
    info('读取详情',`数据目录：${inventory.home}\n数据库索引：${c.database} 条\n会话文件：${c.files} 个\n官方列表：${c.official} 条\n合并去重：${c.total} 个会话\n\n不同来源按会话 ID 合并；↳ 表示派生子会话。\n\n`+(inventory.warnings.join('\n\n')||'所有本地数据源读取正常。'));
  }
  function onNavigate(page){
    if(page!=='sessions')return;
    $('detailActions').innerHTML=`<button id="sessionImport" class="button">${icon('import')}导入会话</button><button id="sessionRefresh" class="button primary">${icon('refresh')}刷新列表</button>`;
    $('sessionImport').onclick=prepareImport;$('sessionRefresh').onclick=refresh;controls();if(!loaded&&!busy)refresh();
  }
  $('sessionSearch').addEventListener('input',applyFilter);
  root.querySelectorAll('[data-session-filter]').forEach(button=>button.addEventListener('click',()=>{filter=button.dataset.sessionFilter;applyFilter();}));
  $('sessionSelectAll').onchange=event=>{selected=event.target.checked?new Set(filtered.map(s=>s.id)):new Set();renderRows();};
  $('sessionList').addEventListener('change',event=>{const id=event.target.dataset.sessionCheck;if(!id)return;if(event.target.checked)selected.add(id);else selected.delete(id);controls();});
  $('sessionList').addEventListener('click',event=>{const button=event.target.closest('[data-session-open]');if(button&&!busy){const session=rows.find(s=>s.id===button.dataset.sessionOpen);if(session)preview(session);}});
  $('sessionMoreRows').onclick=()=>{limit+=150;renderRows();};
  $('sessionMoreMessages').onclick=()=>{if(current&&!previewBusy)preview(current,false);};
  $('sessionShowTools').onchange=()=>{if(current)preview(current);};
  $('sessionDeleteSelected').onclick=()=>prepareDelete([...selected]);$('sessionDeleteOne').onclick=()=>current&&prepareDelete([current.id]);
  $('sessionExport').onclick=async()=>{const result=await run('正在导出所选会话…',()=>api('export',{ids:[...selected]}));if(result){status(`已导出 ${result.count} 个会话`);info('导出完成',`已导出 ${result.count} 个会话：\n${result.path}`);}};
  $('sessionReadDetails').onclick=showDetails;$('sessionSettings').onclick=showSettings;
  document.querySelectorAll('[data-session-close]').forEach(button=>button.onclick=()=>$(button.dataset.sessionClose).close());
  $('sessionBrowseHome').onclick=async()=>{const value=await run('请选择会话数据目录…',()=>api('browseHome'));if(value)$('sessionHomeInput').value=value;};
  $('sessionBrowseExe').onclick=async()=>{const value=await run('请选择 codex.exe…',()=>api('browseCodex'));if(value)$('sessionExeInput').value=value;};
  $('sessionSettingsForm').onsubmit=async event=>{
    event.preventDefault();$('sessionSaveSettings').disabled=true;$('sessionSettingsError').textContent='';
    try{config=await api('configure',{home:$('sessionHomeInput').value.trim(),executable:$('sessionExeInput').value.trim(),readOnly:$('sessionReadOnly').checked});$('sessionSettingsDialog').close();loaded=false;await refresh();}
    catch(error){$('sessionSettingsError').textContent=error.message;}finally{$('sessionSaveSettings').disabled=false;}
  };
  $('sessionPlanAck').onchange=()=>$('sessionPlanConfirm').disabled=!$('sessionPlanAck').checked;
  $('sessionPlanCancel').onclick=()=>closePlan();$('sessionPlanConfirm').onclick=executePlan;
  $('sessionPlanDialog').addEventListener('cancel',event=>{event.preventDefault();closePlan();});
  window.addEventListener('relay-sessions-progress',event=>{if(busy){const p=event.detail;status(`正在处理 ${p.done}/${p.total}`+(p.label?' · '+p.label:''));}});
  return {onNavigate,refresh,isBusy:()=>busy||Boolean(document.querySelector('.session-dialog[open]'))};
})();
