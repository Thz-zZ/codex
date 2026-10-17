'use strict';
class RelayUpdates {
  constructor(){
    this.state={status:'idle'};this.pending=false;this.dismissed='';
    const panel=document.createElement('section');panel.className='settings-card update-settings';
    panel.innerHTML='<div class="update-heading"><div><h2>软件更新</h2><span id="updateVersion"></span></div><button id="checkUpdates" type="button" class="button">检查更新</button></div><label class="preference-row"><span><strong>自动检查更新</strong><small>启动时及运行期间检查新版本。</small></span><input id="autoCheckUpdates" type="checkbox" class="preference-switch"></label><div id="updateStatus" role="status" aria-live="polite"></div><progress id="updateProgress" class="hidden" max="100" value="0" aria-label="更新下载进度"></progress><details id="updateReleaseNotes" class="hidden"><summary>更新说明</summary><p id="updateNotes"></p></details><div class="update-actions"><button id="downloadUpdate" type="button" class="button primary hidden">下载更新</button><button id="installUpdate" type="button" class="button primary hidden">更新并重启</button><button id="cancelUpdate" type="button" class="button hidden">取消下载</button></div>';
    startupCard.after(panel);
    const notice=document.createElement('div');notice.id='updateNotice';notice.className='update-notice hidden';notice.setAttribute('role','status');
    notice.innerHTML='<span id="updateNoticeText"></span><button id="showUpdate" type="button" class="button small">查看更新</button><button id="dismissUpdate" type="button" class="icon-button" aria-label="稍后查看更新">×</button>';
    document.querySelector('main header').after(notice);
    $('checkUpdates').onclick=()=>this.run('check');$('downloadUpdate').onclick=()=>this.run('download');$('cancelUpdate').onclick=()=>this.run('cancel');
    $('installUpdate').onclick=()=>this.install();$('showUpdate').onclick=()=>{navigate('settings');panel.scrollIntoView({block:'nearest',behavior:'smooth'});};
    $('dismissUpdate').onclick=()=>{this.dismissed=this.state.version;this.render();};
    $('autoCheckUpdates').onchange=async()=>{
      const value=$('autoCheckUpdates').checked;$('autoCheckUpdates').disabled=true;
      try{setState(await call('setAppSetting',{key:'autoCheckUpdates',value}));if(value)this.run('check');}catch(error){toast(error.message,true);}finally{$('autoCheckUpdates').disabled=false;this.render();}
    };
    window.addEventListener('relay-updates-state',event=>{this.state=event.detail;this.render();});
  }
  async run(action){
    if(this.pending&&action!=='cancel')return;
    if(action!=='cancel')this.pending=true;this.render();
    try{this.state=await call('updates',{action});}catch(error){this.state={...this.state,status:'error',error:error.message};}
    finally{if(action!=='cancel')this.pending=false;this.render();}
  }
  async install(){
    if(this.pending)return;
    if(!await confirmAction('更新并重启','安装 v'+this.state.version+' 后将重新打开程序。'))return;
    this.pending=true;this.render();
    try{await call('updates',{action:'install'});$('updateStatus').textContent='正在退出并安装更新…';}catch(error){toast(error.message,true);this.pending=false;this.render();}
  }
  render(){
    const value=this.state,busy=['checking','downloading'].includes(value.status),available=Boolean(value.version);
    $('autoCheckUpdates').checked=state.appSettings?.autoCheckUpdates!==false;
    $('updateVersion').textContent='当前版本 v'+(value.currentVersion||document.querySelector('[data-app-version]').textContent.replace(/^v/,''));
    const messages={idle:'尚未检查更新',checking:'正在检查更新…',current:'已是最新版本',unpublished:'暂无可用的正式版本',available:'发现新版本 v'+value.version,downloading:'正在下载 v'+value.version+' · '+(value.progress||0)+'%',ready:'v'+value.version+' 已下载，等待重启安装',error:value.error||'更新失败，请重试'};
    $('updateStatus').textContent=messages[value.status]||messages.idle;$('updateStatus').classList.toggle('update-error',value.status==='error');
    $('checkUpdates').disabled=busy||this.pending||value.status==='ready';
    $('downloadUpdate').classList.toggle('hidden',!available||!['available','error'].includes(value.status));$('downloadUpdate').disabled=this.pending;
    $('installUpdate').classList.toggle('hidden',value.status!=='ready');$('installUpdate').disabled=this.pending;
    $('cancelUpdate').classList.toggle('hidden',value.status!=='downloading');
    $('updateProgress').classList.toggle('hidden',value.status!=='downloading');$('updateProgress').value=value.progress||0;
    $('updateReleaseNotes').classList.toggle('hidden',!value.notes);$('updateNotes').textContent=value.notes||'';
    const show=available&&['available','downloading','ready'].includes(value.status)&&this.dismissed!==value.version;
    $('updateNotice').classList.toggle('hidden',!show);$('updateNoticeText').textContent=value.status==='ready'?'v'+value.version+' 已准备好安装':'发现新版本 v'+value.version;
  }
}
const relayUpdates=new RelayUpdates();
window.appReady.then(()=>relayUpdates.run('status'));
