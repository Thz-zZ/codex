using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
namespace RelayLite {
internal static class UpdateUiSmoke {
    internal static async Task BeforeWebViewReady(WebView2 view,UpdateService updates,string dir){
        if(view.CoreWebView2!=null)throw J.Error("Startup regression must run before the webview core is ready.");
        var result=await updates.Check();
        if(J.Str(result,"status")!="unpublished")throw J.Error("Startup update check did not finish before the webview was ready.");
        J.Atomic(Path.Combine(dir,"updates-startup-result.json"),J.Encode(J.Map("ok",true,"coreNotReady",view.CoreWebView2==null,"checkCompleted",true)));
    }
    internal static async Task Run(CoreWebView2 core,string dir){
        await core.ExecuteScriptAsync(File.ReadAllText(Path.Combine(Program.BaseDir,"updates-smoke.js")));
        for(int n=0;n<400;n++){
            string result=await core.ExecuteScriptAsync("window.updateSmokeResult||null");
            if(result!="null"){
                J.Atomic(Path.Combine(dir,"updates-result.json"),result);
                if(!J.True(J.Get(J.Decode(result),"ok")))throw J.Error(result);
                await core.ExecuteScriptAsync("navigate('settings');$('toast').classList.add('hidden')");
                using(var preview=File.Create(Path.Combine(dir,"updates-settings.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,preview);
                await core.ExecuteScriptAsync("navigate('details')");return;
            }
            await Task.Delay(50);
        }
        throw J.Error("Update UI checks timed out.");
    }
    internal static async Task CheckCompact(CoreWebView2 core,string dir){
        await core.ExecuteScriptAsync("navigate('settings');document.querySelector('.update-settings').scrollIntoView({block:'center'})");
        string result=await core.ExecuteScriptAsync("(()=>{const panel=document.querySelector('.update-settings');return panel.scrollWidth<=panel.clientWidth&&[...panel.querySelectorAll('button,input')].every(el=>{const r=el.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth;});})()");
        using(var preview=File.Create(Path.Combine(dir,"updates-compact.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,preview);
        if(result!="true")throw J.Error("Update settings overflow in compact layout.");
        await core.ExecuteScriptAsync("navigate('details')");
    }
}
}
