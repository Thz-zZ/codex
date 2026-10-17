using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RelayLite {
internal sealed partial class MainForm {
    SessionBridge sessionBridge;
    string sessionBridgeHome,sessionBridgeExe;
    bool sessionOperation;

    object SessionSettings(){
        return J.Read(Path.Combine(dataDir,"session-settings.json"),J.Map("home","","executable","","readOnly",false));
    }
    string SessionHome(object settings){
        return smoke?Path.Combine(qaDir,"session-fixture"):J.Str(settings,"home")!=""?J.Str(settings,"home"):store.ConfigDir;
    }
    async Task<object> SessionStatus(){
        var settings=SessionSettings();var status=J.Obj(await SessionCall("status",J.Map()));
        status["readOnly"]=J.True(J.Get(settings,"readOnly"));
        status["followsConfig"]=J.Str(settings,"home")=="";
        return status;
    }
    async Task<object> SessionCall(string action,object input){
        var settings=SessionSettings();string home=Path.GetFullPath(SessionHome(settings)),exe=J.Str(settings,"executable");
        if(J.True(J.Get(settings,"readOnly"))&&(action=="prepareDelete"||action=="executeDelete"||action=="prepareImport"||action=="executeImport"))throw J.Error("当前为只读模式，请在“读取设置”中关闭只读模式后重试。");
        if(sessionBridge==null||sessionBridgeHome!=home||sessionBridgeExe!=exe){
            if(sessionBridge!=null)sessionBridge.Dispose();
            sessionBridge=new SessionBridge(home,exe);sessionBridgeHome=home;sessionBridgeExe=exe;
        }
        sessionOperation=true;
        try{return await sessionBridge.Call(action,input,progress=>{
            try{if(view!=null)view.CoreWebView2.PostWebMessageAsJson(J.Encode(J.Map("event","sessions-progress","data",progress)));}catch(InvalidOperationException){}
        });}finally{sessionOperation=false;}
    }
    async Task<object> HandleSessions(object input){
        string action=J.Str(input,"action");var args=J.Obj(J.Clone(J.Get(input,"args",J.Map())));
        if(action=="settings")return await SessionStatus();
        if(action=="configure"){
            string home=J.Str(args,"home").Trim(),exe=J.Str(args,"executable").Trim();
            if(home!=""&&!Directory.Exists(home))throw J.Error("会话数据目录不存在。");
            if(exe!=""&&(!File.Exists(exe)||!exe.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)))throw J.Error("请选择有效的 codex.exe。");
            if(sessionBridge!=null){sessionBridge.Dispose();sessionBridge=null;}
            J.Atomic(Path.Combine(dataDir,"session-settings.json"),J.Encode(J.Map("home",home!=""?Path.GetFullPath(home):"","executable",exe,"readOnly",J.True(J.Get(args,"readOnly")))));
            return await SessionStatus();
        }
        if(action=="browseHome"){
            using(var dialog=new FolderBrowserDialog()){dialog.Description="选择 Codex 会话数据目录";dialog.SelectedPath=SessionHome(SessionSettings());return dialog.ShowDialog(this)==DialogResult.OK?dialog.SelectedPath:null;}
        }
        if(action=="browseCodex"){
            using(var dialog=new OpenFileDialog()){dialog.Title="选择 codex.exe";dialog.Filter="Codex 程序 (codex.exe)|codex.exe|可执行程序 (*.exe)|*.exe";return dialog.ShowDialog(this)==DialogResult.OK?dialog.FileName:null;}
        }
        if(action=="export"){
            if(J.Arr(J.Get(args,"ids")).Count==0)throw J.Error("请先勾选会话。");
            using(var dialog=new SaveFileDialog()){
                dialog.Title="导出已选会话";dialog.Filter="会话导出包 (*.zip)|*.zip";dialog.DefaultExt="zip";dialog.FileName="Codex会话-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip";
                if(dialog.ShowDialog(this)!=DialogResult.OK)return null;args["path"]=dialog.FileName;
            }
        }
        if(action=="prepareImport"){
            if(J.True(J.Get(SessionSettings(),"readOnly")))throw J.Error("当前为只读模式。");
            using(var dialog=new OpenFileDialog()){dialog.Title="选择会话导出包";dialog.Filter="会话导出包 (*.zip)|*.zip";if(dialog.ShowDialog(this)!=DialogResult.OK)return null;args["path"]=dialog.FileName;}
        }
        return await SessionCall(action,args);
    }
}
}
