(async()=>{
  const check=(value,label)=>{if(!value)throw Error('Updates: '+label);};
  const waitFor=async(fn)=>{for(let n=0;n<100;n++){if(fn())return;await new Promise(resolve=>setTimeout(resolve,30));}throw Error('Updates: timed out');};
  await waitFor(()=>!relayUpdates.pending);
  const original=window.relay,initial=state.appSettings.autoCheckUpdates;
  navigate('settings');
  check($('checkUpdates')&&$('autoCheckUpdates').checked===(initial!==false),'update settings loaded');
  check(relayUpdates.state.status==='unpublished','startup update result is recovered after the webview core becomes ready');
  await relayUpdates.run('check');check(relayUpdates.state.status==='unpublished','native no-release response is handled');
  let finishDownload,installs=0;
  const fixture={currentVersion:'1.1.4',version:'9.9.0',notes:'修复与改进\n<script>不会执行</script>',progress:0,status:'available'};
  const emit=value=>window.dispatchEvent(new CustomEvent('relay-updates-state',{detail:value}));
  window.relay={...original,updates:async input=>{
    if(input.action==='check'||input.action==='status')return {ok:true,data:{...fixture}};
    if(input.action==='download'){emit({...fixture,status:'downloading',progress:35});return new Promise(resolve=>{finishDownload=resolve;});}
    if(input.action==='cancel'){const value={...fixture,status:'available'};finishDownload({ok:true,data:value});return {ok:true,data:value};}
    if(input.action==='install'){installs++;return {ok:false,error:'当前操作尚未结束，请稍后再安装更新。'};}
  }};
  try{
    $('checkUpdates').click();await waitFor(()=>!relayUpdates.pending);
    check(!$('updateNotice').classList.contains('hidden')&&!$('downloadUpdate').classList.contains('hidden'),'new version notice and download action');
    check($('updateNotes').textContent===fixture.notes&&!$('updateNotes').querySelector('script'),'release notes rendered as plain text');
    $('dismissUpdate').click();check($('updateNotice').classList.contains('hidden'),'new version notice can be dismissed');
    $('downloadUpdate').click();await waitFor(()=>Boolean(finishDownload));
    check($('updateProgress').value===35&&$('checkUpdates').disabled&&!$('cancelUpdate').classList.contains('hidden'),'download progress and disabled duplicate actions');
    $('cancelUpdate').click();await waitFor(()=>!relayUpdates.pending);check(relayUpdates.state.status==='available'&&!$('downloadUpdate').disabled,'cancel permits retry');
    finishDownload=null;$('downloadUpdate').click();await waitFor(()=>Boolean(finishDownload));finishDownload({ok:true,data:{...fixture,status:'error',error:'更新包校验失败'}});
    await waitFor(()=>!relayUpdates.pending);check($('updateStatus').textContent==='更新包校验失败'&&!$('downloadUpdate').disabled,'download failure permits retry');
    finishDownload=null;$('downloadUpdate').click();await waitFor(()=>Boolean(finishDownload));finishDownload({ok:true,data:{...fixture,status:'ready',progress:100}});
    await waitFor(()=>!relayUpdates.pending);check(!$('installUpdate').classList.contains('hidden')&&$('downloadUpdate').classList.contains('hidden'),'verified update waits for restart');
    const cancelInstall=relayUpdates.install();$('confirmCancel').click();await cancelInstall;check(installs===0,'cancel restart leaves program running');
    const install=relayUpdates.install();$('confirmOkay').click();await install;check(installs===1&&!$('installUpdate').disabled&&$('toast').textContent.includes('当前操作'),'busy operation keeps update ready and program open');
    $('autoCheckUpdates').click();await waitFor(()=>!$('autoCheckUpdates').disabled);check(state.appSettings.autoCheckUpdates===false,'automatic checks can be disabled');
    $('autoCheckUpdates').click();await waitFor(()=>!$('autoCheckUpdates').disabled&&!relayUpdates.pending);check(state.appSettings.autoCheckUpdates===true,'automatic checks can be enabled again');
    const panel=document.querySelector('.update-settings');check(panel.scrollWidth<=panel.clientWidth,'update panel fits');
  }finally{
    window.relay=original;setState(await call('setAppSetting',{key:'autoCheckUpdates',value:initial!==false}));await relayUpdates.run('status');navigate('details');
  }
  window.updateSmokeResult={ok:true,newVersion:true,downloadProgress:true,cancelAndRetry:true,checksumFailure:true,restartConfirmation:true,busyInstallGuard:true,autoCheckPreference:true};
})().catch(error=>{window.updateSmokeResult={ok:false,error:String(error.stack||error)};});
