using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
namespace RelayLite {
internal static class DraftUiSmoke {
    public static async Task Run(CoreWebView2 core,string dir){
        await UpdateUiSmoke.Run(core,dir);
        await core.ExecuteScriptAsync(File.ReadAllText(Path.Combine(Program.BaseDir,"draft-editor-smoke.js")));
        for(int n=0;n<500;n++){
            string json=await core.ExecuteScriptAsync("window.draftSmokeResult||null");
            if(json!="null"){
                J.Atomic(Path.Combine(dir,"draft-editor-result.json"),json);
                if(!J.True(J.Get(J.Decode(json),"ok")))throw J.Error(json);
                await core.ExecuteScriptAsync("$('toast').classList.add('hidden');$('sortFeedback').classList.remove('visible')");
                await core.ExecuteScriptAsync("openEditor(state.providers.find(p=>p.adapter==='newapi').id)");
                await core.ExecuteScriptAsync("modelPicker.load()");
                await core.ExecuteScriptAsync("$('modelName').scrollIntoView({block:'center'});");
                await Task.Delay(150);
                using(var modelPreview=File.Create(Path.Combine(dir,"model-picker.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,modelPreview);
                await core.ExecuteScriptAsync("modelPicker.close()");
                await core.ExecuteScriptAsync("filterPickers.get('reasoning').open()");
                await Task.Delay(150);
                using(var effortPreview=File.Create(Path.Combine(dir,"reasoning-picker.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,effortPreview);
                await core.ExecuteScriptAsync("filterPickers.get('reasoning').close()");
                await core.ExecuteScriptAsync("$('accountOptions').open=true;$('accountKey').value='smoke-account-fixture';$('accountUserId').value='bad';updateAccountForm();");
                await Task.Delay(100);
                await core.ExecuteScriptAsync("testConnectionFromForm()");
                await core.ExecuteScriptAsync("$('testConnection').scrollIntoView({block:'center'});");
                using(var preview=File.Create(Path.Combine(dir,"connection-test.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,preview);
                await core.ExecuteScriptAsync("closeEditor()");
                await core.ExecuteScriptAsync("navigate('settings');");
                using(var settingsPreview=File.Create(Path.Combine(dir,"settings.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,settingsPreview);
                await core.ExecuteScriptAsync("navigate('providers')");
                await core.ExecuteScriptAsync("openDetails(state.providers.find(p=>!p.active).id)");
                await Task.Delay(180);
                using(var inactivePreview=File.Create(Path.Combine(dir,"workspace-inactive.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,inactivePreview);
                return;
            }
            await Task.Delay(50);
        }
        throw J.Error("Draft editor smoke test timed out.");
    }
    public static async Task CheckCompact(CoreWebView2 core,string dir){
        await UpdateUiSmoke.CheckCompact(core,dir);
        string report=await core.ExecuteScriptAsync("(()=>{const toolbar=document.querySelector('.detail-toolbar').getBoundingClientRect(),controls=document.querySelector('.detail-refresh-controls'),box=controls.getBoundingClientRect(),model=document.querySelector('.model-field').getBoundingClientRect(),time=$('detailRefreshTime'),stamp=time.getBoundingClientRect();return {noHorizontalOverflow:document.querySelector('main').scrollWidth<=document.querySelector('main').clientWidth,sidebarFits:document.querySelector('.sidebar').scrollHeight<=document.querySelector('.sidebar').clientHeight,metricsFit:[...document.querySelectorAll('.metric-value')].every(el=>el.scrollWidth<=el.clientWidth),refreshTimeFits:stamp.top>=toolbar.top&&stamp.bottom<=model.top&&stamp.right<=toolbar.right&&time.scrollWidth<=time.clientWidth,refreshControlsFit:box.left>=model.right&&box.right<=toolbar.right&&controls.scrollWidth<=controls.clientWidth&&[...controls.querySelectorAll('button')].every(el=>el.getBoundingClientRect().bottom<=toolbar.bottom)};})()");
        var checks=J.Decode(report);
        if(!J.True(J.Get(checks,"noHorizontalOverflow"))||!J.True(J.Get(checks,"sidebarFits"))||!J.True(J.Get(checks,"metricsFit"))||!J.True(J.Get(checks,"refreshControlsFit"))||!J.True(J.Get(checks,"refreshTimeFits")))throw J.Error("Compact workspace layout: "+report);
        await core.ExecuteScriptAsync("openEditor()");
        await Task.Delay(100);
        string editor=await core.ExecuteScriptAsync("(()=>{const dialog=$('providerDialog').getBoundingClientRect(),footer=document.querySelector('#providerDialog .dialog-footer').getBoundingClientRect();return footer.bottom<=dialog.bottom&&footer.bottom<=innerHeight&&footer.top>=dialog.top;})()");
        using(var preview=File.Create(Path.Combine(dir,"compact-editor.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,preview);
        await core.ExecuteScriptAsync("closeEditor()");
        if(editor!="true")throw J.Error("Compact editor footer is clipped.");
        J.Atomic(Path.Combine(dir,"compact-layout-result.json"),report);
    }
}
}
