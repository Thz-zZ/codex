using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace RelayLite {
internal sealed partial class MainForm {
    async Task CheckTrayWindowSmoke(){
        var originalView=view;
        var originalCore=view.CoreWebView2;
        var originalBridge=modelBridge;
        int navigationCount=0,backgroundFetches=0;
        EventHandler<CoreWebView2NavigationStartingEventArgs> navigationHandler=(sender,args)=>navigationCount++;
        originalCore.NavigationStarting+=navigationHandler;
        const string snapshot="JSON.stringify({page,scroll:document.querySelector('main').scrollTop,dialog:$('providerDialog').open,name:$('providerName').value,url:$('baseUrl').value,dialogScroll:document.querySelector('#providerDialog .dialog-body').scrollTop})";
        await originalCore.ExecuteScriptAsync("window.trayKeepAliveDocument=document;window.trayKeepAliveNode=$('settingsPage');window.traySavedApi=window.relay;window.trayHiddenAutoFetches=0;window.relay={...window.relay,inspect:input=>{window.trayHiddenAutoFetches++;return traySavedApi.inspect(input);}};");
        try{
            for(int cycle=0;cycle<3;cycle++){
                await originalCore.ExecuteScriptAsync(cycle==0?"navigate('settings');document.querySelector('main').scrollTop=80;":cycle==1?"openEditor();$('providerName').value='未保存的托盘草稿';$('baseUrl').value='https://tray-fixture.example/v1';document.querySelector('#providerDialog .dialog-body').scrollTop=60;":"closeEditor();navigate('details');document.querySelector('main').scrollTop=120;");
                await Task.Delay(100);
                string before=await originalCore.ExecuteScriptAsync(snapshot);
                Close();
                await Task.Delay(100);
                if(Visible||ShowInTaskbar||quitting||!ReferenceEquals(view,originalView)||!ReferenceEquals(view.CoreWebView2,originalCore)||!ReferenceEquals(modelBridge,originalBridge))throw J.Error("Closing the window did not retain the background application and WebView.");
                string hidden=await originalCore.ExecuteScriptAsync("(()=>{if(!document.hidden)return false;window.trayHiddenAutoFetches=0;nextRefresh=Date.now()-100;updateCountdown();nextRefresh=Date.now()+60000;return trayHiddenAutoFetches===0;})()");
                if(hidden!="true")throw J.Error("Hidden WebView duplicated automatic refresh or remained visible.");
                if(cycle==0){
                    var originalFetch=Usage.FetchOverride;
                    try{Usage.FetchOverride=(url,key,user)=>{backgroundFetches++;return originalFetch(url,key,user);};await BackgroundRefresh();}
                    finally{Usage.FetchOverride=originalFetch;}
                    if(backgroundFetches==0)throw J.Error("Native tray refresh stopped after closing the window.");
                }
                await Open();
                await Task.Delay(100);
                if(!Visible||!ShowInTaskbar||!ReferenceEquals(view,originalView)||navigationCount!=0)throw J.Error("Reopening recreated or reloaded the window.");
                string after=await originalCore.ExecuteScriptAsync(snapshot);
                if(before!=after||await originalCore.ExecuteScriptAsync("document===trayKeepAliveDocument&&$('settingsPage')===trayKeepAliveNode")!="true")throw J.Error("Reopening lost the page, scroll position or unsaved editor draft.");
            }
            J.Atomic(Path.Combine(qaDir,"window-lifecycle-result.json"),J.Encode(J.Map("ok",true,"closeReopenCycles",3,"sameWebView",true,"documentNavigations",navigationCount,"settingsAndScrollPreserved",true,"unsavedEditorPreserved",true,"backgroundRefreshContinues",backgroundFetches>0,"hiddenUiRefreshSuppressed",true,"bridgeRetained",true)));
        }finally{
            originalCore.NavigationStarting-=navigationHandler;
            await originalCore.ExecuteScriptAsync("window.relay=traySavedApi;closeEditor();navigate('providers');nextRefresh=Date.now()+60000;delete window.trayKeepAliveDocument;delete window.trayKeepAliveNode;delete window.traySavedApi;delete window.trayHiddenAutoFetches;");
        }
    }
}
}
