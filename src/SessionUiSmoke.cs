using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace RelayLite {
internal static class SessionUiSmoke {
    public static async Task Run(CoreWebView2 core,string dir){
        await core.ExecuteScriptAsync(File.ReadAllText(Path.Combine(Program.BaseDir,"sessions-smoke.js")));
        string last="";
        for(int n=0;n<1800;n++){
            string stage=J.Str(J.Decode(await core.ExecuteScriptAsync("({stage:window.sessionSmokeStage||''})")),"stage");
            if(stage!=""&&stage!=last){
                last=stage;await Task.Delay(100);
                using(var output=File.Create(Path.Combine(dir,"sessions-"+stage+".png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,output);
                await core.ExecuteScriptAsync("window.sessionSmokeContinue()");
            }
            string result=await core.ExecuteScriptAsync("window.sessionSmokeResult||null");
            if(result!="null"){
                J.Atomic(Path.Combine(dir,"sessions-result.json"),result);
                if(!J.True(J.Get(J.Decode(result),"ok")))throw J.Error(result);
                return;
            }
            await Task.Delay(50);
        }
        throw J.Error("Session UI smoke test timed out.");
    }
    public static async Task CheckCompact(CoreWebView2 core,string dir){
        await core.ExecuteScriptAsync("navigate('sessions')");await Task.Delay(120);
        string result=await core.ExecuteScriptAsync("(()=>({fits:document.querySelector('main').scrollWidth<=document.querySelector('main').clientWidth,panels:[...document.querySelectorAll('.session-list-panel,.session-preview-panel')].every(el=>el.scrollWidth<=el.clientWidth)}))()");
        J.Atomic(Path.Combine(dir,"sessions-compact-result.json"),result);
        using(var output=File.Create(Path.Combine(dir,"sessions-compact.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,output);
        if(!J.True(J.Get(J.Decode(result),"fits"))||!J.True(J.Get(J.Decode(result),"panels")))throw J.Error("Compact sessions layout: "+result);
        await core.ExecuteScriptAsync("navigate('providers')");
    }
}
}
