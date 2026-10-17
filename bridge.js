'use strict';
(() => {
  const pending = new Map(); let sequence = 0;
  window.chrome.webview.addEventListener('message', event => {
    if(event.data.event==='sessions-progress'){window.dispatchEvent(new CustomEvent('relay-sessions-progress',{detail:event.data.data}));return;}
    if(event.data.event==='updates-state'){window.dispatchEvent(new CustomEvent('relay-updates-state',{detail:event.data.data}));return;}
    const {id, result} = event.data; const resolve = pending.get(id);
    if (resolve) { pending.delete(id); resolve(result); }
  });
  window.relay = Object.freeze(Object.fromEntries(['updates','sessions','state','setAppSetting','providerDetails','saveProvider','deleteProvider','reorderProviders','switchProvider','importCurrent','inspect','detectAdapter','testConnection','listModels','snapshots','chooseConfigDir','openDataDir'].map(method => [method, input => new Promise(resolve => {
    const id = ++sequence; pending.set(id, resolve);
    window.chrome.webview.postMessage({id, method, input:input ?? null});
  })])));
})();
