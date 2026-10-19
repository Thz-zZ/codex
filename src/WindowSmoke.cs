using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace RelayLite {
internal sealed partial class MainForm {
    async Task CheckSavedWindowSmoke(){
        var originalBounds=Bounds;var originalState=WindowState;var originalView=view;
        string file=Path.Combine(dataDir,"window-state.json");
        try{
            WindowState=FormWindowState.Normal;
            Bounds=WindowPlacement.Fit(new Rectangle(67,83,1080,740),Screen.FromControl(this).WorkingArea,MinimumSize);
            await Task.Delay(80);var normal=Bounds;windowPlacement.Save();
            var saved=WindowPlacement.Read(file);
            if(saved==null||saved.Bounds!=normal||saved.Maximized)throw J.Error("Real app did not save normal window bounds.");
            WindowState=FormWindowState.Maximized;await Task.Delay(80);windowPlacement.Save();saved=WindowPlacement.Read(file);
            if(saved==null||!saved.Maximized||saved.Bounds!=normal)throw J.Error("Real app did not retain normal bounds while maximized.");
            WindowState=FormWindowState.Minimized;await Task.Delay(80);HideToTray();saved=WindowPlacement.Read(file);
            if(saved==null||!saved.Maximized||saved.Bounds!=normal)throw J.Error("Minimizing to tray corrupted the saved window geometry.");
            await Open();await Task.Delay(80);
            if(WindowState!=FormWindowState.Maximized||!ReferenceEquals(view,originalView))throw J.Error("Reopening from tray lost maximization or recreated WebView.");
            WindowState=FormWindowState.Normal;await Task.Delay(80);
            if(Bounds!=normal)throw J.Error("Restoring maximization lost the last normal window size.");
            J.Atomic(Path.Combine(qaDir,"window-placement-result.json"),J.Encode(J.Map("ok",true,"normalBoundsSaved",true,"maximizedSaved",true,"minimizedDoesNotOverwrite",true,"trayMaximizedRestored",true,"normalRestoreBoundsPreserved",true,"sameWebView",true)));
        }finally{
            WindowState=FormWindowState.Normal;Bounds=originalBounds;
            if(originalState==FormWindowState.Maximized)WindowState=originalState;
            windowPlacement.Save();
        }
    }
    async Task CheckTrayWindowSmoke(){
        var originalView=view;
        var originalCore=view.CoreWebView2;
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
                if(Visible||ShowInTaskbar||quitting||!ReferenceEquals(view,originalView)||!ReferenceEquals(view.CoreWebView2,originalCore))throw J.Error("Closing the window did not retain the background application and WebView.");
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
