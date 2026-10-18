using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
namespace RelayLite {
internal static class DraftUiSmoke {
    static async Task PointerClick(CoreWebView2 core,string id){
        await core.ExecuteScriptAsync("document.querySelector('main').scrollTop=0;document.getElementById("+J.Encode(id)+").scrollIntoView({block:'nearest'});");
        var point=J.Decode(await core.ExecuteScriptAsync("(()=>{const button=document.getElementById("+J.Encode(id)+"),box=button.getBoundingClientRect(),x=box.left+box.width/2,y=box.top+box.height/2;return {x,y,reachable:button.contains(document.elementFromPoint(x,y)),disabled:button.disabled};})()"));
        if(!J.True(J.Get(point,"reachable"))||J.True(J.Get(point,"disabled")))throw J.Error("Refresh pointer target blocked: "+id+" "+J.Encode(point));
        foreach(string type in new[]{"mouseMoved","mousePressed","mouseReleased"})await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",J.Encode(J.Map("type",type,"x",J.Get(point,"x"),"y",J.Get(point,"y"),"button",type=="mouseMoved"?"none":"left","clickCount",type=="mouseMoved"?0:1)));
    }
    static async Task CheckRefreshPointer(CoreWebView2 core,string dir){
        await core.ExecuteScriptAsync("window.refreshPointerSaved={api:window.relay,auto};window.refreshPointerCalls=0;window.relay={...window.relay,inspect:input=>{window.refreshPointerCalls++;return refreshPointerSaved.api.inspect(input);}};openDetails(state.providers.find(p=>!p.active).id);filterPickers.get('period').open();");
        try{
            await PointerClick(core,"pauseAuto");
            for(int attempt=0;attempt<100;attempt++){if(await core.ExecuteScriptAsync("!autoSavePending")=="true")break;await Task.Delay(50);}
            if(await core.ExecuteScriptAsync("auto!==refreshPointerSaved.auto")!="true")throw J.Error("Pause button did not respond to pointer input.");
            await PointerClick(core,"refreshDetail");
            for(int attempt=0;attempt<200;attempt++){
                if(await core.ExecuteScriptAsync("refreshPointerCalls===1&&!pending.size&&!$('refreshDetail').disabled")=="true"){
                    await PointerClick(core,"pauseAuto");
                    for(int saveAttempt=0;saveAttempt<100;saveAttempt++){if(await core.ExecuteScriptAsync("!autoSavePending")=="true")break;await Task.Delay(50);}
                    J.Atomic(Path.Combine(dir,"refresh-pointer-result.json"),J.Encode(J.Map("ok",true,"pausePointer",true,"manualRefreshPointer",true,"inactiveRelay",true)));return;
                }
                await Task.Delay(50);
            }
            throw J.Error("Manual refresh did not complete after pointer input.");
        }finally{await core.ExecuteScriptAsync("window.relay=refreshPointerSaved.api;auto=refreshPointerSaved.auto;");}
    }
    const string ViewSnapshot="({id:detailId,period:rangePeriod,model:$('modelFilter').value,granularity:chartGranularity,hiddenSeries:[...hiddenSeries],matchingData:currentDetail?.period===rangePeriod&&currentDetail?.model===$('modelFilter').value})";
    static async Task CheckRequestTable(CoreWebView2 core,string dir,bool compact){
        if(!compact)await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",J.Encode(J.Map("width",1440,"height",900,"deviceScaleFactor",1,"mobile",false)));
        await core.ExecuteScriptAsync("(()=>{window.requestTableSavedScroll=document.querySelector('main').scrollTop;const fixture=document.createElement('div');fixture.id='requestTableLayoutFixture';fixture.innerHTML=tableCard('近期请求',['时间','模型','输入 Token','输出 Token','缓存读取','缓存写入','缓存率'],Array.from({length:24},(_,index)=>['2026/10/18 18:20:33',index===1?'claude-'+('long-relay-model-'.repeat(6)):'claude-opus-5-5','54,308','1,530','40,182','14,124','73.99%']),'暂无近期请求。','recent-requests');$('detailBody').append(fixture);fixture.scrollIntoView({block:'start'});})()");
        try{
            await Task.Delay(100);
            string report=await core.ExecuteScriptAsync("(()=>{const card=$('requestTableLayoutFixture').querySelector('.recent-requests'),scroll=card.querySelector('.table-scroll'),table=card.querySelector('table'),headers=[...table.querySelectorAll('th')],cells=[...table.querySelector('tbody tr').cells],widths=headers.map(cell=>cell.getBoundingClientRect().width),main=document.querySelector('main'),long=table.querySelectorAll('tbody tr')[1].cells[1];scroll.scrollLeft=scroll.scrollWidth;scroll.scrollTop=100;const box=scroll.getBoundingClientRect(),last=cells.at(-1).getBoundingClientRect();return {columns:headers.length,viewportWidth:innerWidth,availableWidth:scroll.clientWidth,tableWidth:table.getBoundingClientRect().width,equalColumns:Math.max(...widths)-Math.min(...widths)<1,leftAligned:[...headers,...cells].every(cell=>getComputedStyle(cell).textAlign==='left'),headerAlignment:headers.every((header,index)=>Math.abs(header.getBoundingClientRect().left-cells[index].getBoundingClientRect().left)<1),scrollbarGap:box.left+scroll.clientLeft+scroll.clientWidth-last.right,lastColumnReachable:last.left>=box.left&&last.right<=box.left+scroll.clientWidth-8,verticalScroll:scroll.scrollHeight>scroll.clientHeight,horizontalScroll:scroll.scrollWidth>scroll.clientWidth,headerSticky:Math.abs(headers[0].getBoundingClientRect().top-box.top)<1,longModelWrapped:long.scrollWidth<=long.clientWidth&&getComputedStyle(long).whiteSpace==='normal'&&getComputedStyle(long).textOverflow!=='ellipsis',noPageOverflow:main.scrollWidth<=main.clientWidth,scrollbarButtonsHidden:getComputedStyle(scroll,'::-webkit-scrollbar-button').display==='none'};})()");
            var checks=J.Decode(report);foreach(string key in new[]{"equalColumns","leftAligned","headerAlignment","lastColumnReachable","verticalScroll","headerSticky","longModelWrapped","noPageOverflow","scrollbarButtonsHidden"})if(!J.True(J.Get(checks,key)))throw J.Error("Request table layout ("+key+"): "+report);
            if(!compact&&J.True(J.Get(checks,"horizontalScroll")))throw J.Error("Wide request table should fit without horizontal scrolling: "+report);
            await core.ExecuteScriptAsync("$('requestTableLayoutFixture').querySelector('.table-scroll').scrollTop=0;");
            string name=compact?"request-table-compact":"request-table-wide";J.Atomic(Path.Combine(dir,name+".json"),report);
            using(var preview=File.Create(Path.Combine(dir,name+".png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,preview);
        }finally{await core.ExecuteScriptAsync("$('requestTableLayoutFixture').remove();document.querySelector('main').scrollTop=window.requestTableSavedScroll;");if(!compact)await core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride","{}");}
    }
    static string trayView;
    internal static async Task PrepareTrayView(CoreWebView2 core){
        await core.ExecuteScriptAsync("window.detailViewTrayPrepared=null;(async()=>{await openDetails(state.providers.find(p=>p.active).id);await applyRange('7');$('modelFilter').value='gpt-codex';rememberDetailView();await refreshDetails(false);document.querySelector('[data-grain=day]')?.click();document.querySelector('[data-series=plain]').click();await detailViewSave;window.detailViewTrayPrepared=true;})().catch(error=>window.detailViewTrayPrepared=String(error));");
        for(int n=0;n<200;n++){
            string result=await core.ExecuteScriptAsync("window.detailViewTrayPrepared");
            if(result=="true"){trayView=await core.ExecuteScriptAsync(ViewSnapshot);return;}
            if(result!="null")throw J.Error("Preparing tray view: "+result);
            await Task.Delay(50);
        }
        throw J.Error("Preparing tray view timed out.");
    }
    internal static async Task CheckTrayView(CoreWebView2 core,string dir){
        string restored=await core.ExecuteScriptAsync(ViewSnapshot);
        if(restored!=trayView||!J.True(J.Get(J.Decode(restored),"matchingData")))throw J.Error("Tray reopen lost the relay filters or matching cache: "+restored+"; expected "+trayView);
        J.Atomic(Path.Combine(dir,"detail-view-result.json"),J.Encode(J.Map("ok",true,"perRelayFilters",true,"customDates",true,"modelCache",true,"trayRestore",true,"freshProcessDefaults",true)));
    }
    static async Task CheckRequiredSelections(CoreWebView2 core,string dir){
        await core.ExecuteScriptAsync("openEditor()");
        try{
            string report=await core.ExecuteScriptAsync("(()=>{const ids=['providerPreset','protocol','modelName','reasoning'];return {empty:ids.every(id=>$(id).value===''),required:ids.every(id=>$(id).required),visiblePlaceholders:['providerPreset','protocol','reasoning'].every(id=>$(id+'Value').textContent.startsWith('请选择')&&$(id+'Picker').classList.contains('is-required-empty')),modelPlaceholder:$('modelName').classList.contains('is-required-empty')&&$('modelName').placeholder.includes('选择默认模型'),badges:document.querySelectorAll('#providerDialog label .field-tag.required').length>=4};})()");
            var checks=J.Decode(report);foreach(string key in new[]{"empty","required","visiblePlaceholders","modelPlaceholder","badges"})if(!J.True(J.Get(checks,key)))throw J.Error("Required editor selections ("+key+"): "+report);
            J.Atomic(Path.Combine(dir,"required-selections.json"),report);
            await core.ExecuteScriptAsync("$('providerDialog').querySelector('.dialog-body').scrollTop=0");
            await Task.Delay(100);
            using(var preview=File.Create(Path.Combine(dir,"required-selections-top.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,preview);
            await core.ExecuteScriptAsync("$('modelName').scrollIntoView({block:'center'})");
            await Task.Delay(100);
            using(var preview=File.Create(Path.Combine(dir,"required-selections-model.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,preview);
        }finally{await core.ExecuteScriptAsync("closeEditor()");}
    }
    public static async Task Run(CoreWebView2 core,string dir){
        await UpdateUiSmoke.Run(core,dir);
        await CheckRequiredSelections(core,dir);
        await core.ExecuteScriptAsync(File.ReadAllText(Path.Combine(Program.BaseDir,"draft-editor-smoke.js")));
        for(int n=0;n<500;n++){
            string json=await core.ExecuteScriptAsync("window.draftSmokeResult||null");
            if(json!="null"){
                J.Atomic(Path.Combine(dir,"draft-editor-result.json"),json);
                if(!J.True(J.Get(J.Decode(json),"ok")))throw J.Error(json);
                await CheckRefreshPointer(core,dir);
                await core.ExecuteScriptAsync("$('toast').classList.add('hidden');$('sortFeedback').classList.remove('visible')");
                await CheckRequestTable(core,dir,false);
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
                await core.ExecuteScriptAsync("window.summaryPreviewPrevious=detailId;openDetails(state.providers.find(p=>p.adapter==='sub2api').id);document.querySelector('main').scrollTop=0;");
                await Task.Delay(100);
                using(var summaryPreview=File.Create(Path.Combine(dir,"sub2api-summary.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,summaryPreview);
                await core.ExecuteScriptAsync("openDetails(window.summaryPreviewPrevious);");
                return;
            }
            await Task.Delay(50);
        }
        throw J.Error("Draft editor smoke test timed out.");
    }
    public static async Task CheckCompact(CoreWebView2 core,string dir){
        await UpdateUiSmoke.CheckCompact(core,dir);
        await CheckRequestTable(core,dir,true);
        foreach(string id in new[]{"period","modelFilter"}){
            await core.ExecuteScriptAsync("document.querySelector('main').scrollTop=0;filterPickers.get("+J.Encode(id)+").open();");
            await Task.Delay(150);
            string fits=await core.ExecuteScriptAsync("(()=>{const menu=document.getElementById("+J.Encode(id+"Menu")+").getBoundingClientRect(),main=document.querySelector('main').getBoundingClientRect();return menu.left>=main.left&&menu.right<=main.right&&menu.top>=0&&menu.bottom<=innerHeight;})()");
            await core.ExecuteScriptAsync("filterPickers.get("+J.Encode(id)+").close();");
            if(fits!="true")throw J.Error("Compact dropdown is clipped: "+id);
        }
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
