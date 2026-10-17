using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RelayLite {
internal sealed partial class MainForm {
    UpdateService updates;
    bool updateExitPending;
    readonly Timer updateTimer=new Timer {Interval=6*60*60*1000};
    UpdateService Updates {get{
        if(updates==null)updates=new UpdateService(smoke?Path.Combine(qaDir,"updates"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Codex Relay Lite","updates"),PostUpdateState,smoke?(Func<string,System.Threading.CancellationToken,Task<object>>)((url,token)=>Task.FromResult<object>(null)):null);
        return updates;
    }}
    void PostUpdateState(object value){
        var current=view;if(current==null||current.IsDisposed||current.Disposing)return;
        try{
            // Startup checks can finish before WebView2 initialization; the page reads the saved snapshot when ready.
            var core=current.CoreWebView2;if(core==null)return;
            core.PostWebMessageAsJson(J.Encode(J.Map("event","updates-state","data",value)));
        }catch(InvalidOperationException){}catch(System.Runtime.InteropServices.COMException){}
    }
    bool AutoCheckUpdates {get{return !J.Has(J.Get(store.Data,"appSettings"),"autoCheckUpdates")||J.True(J.Get(J.Get(store.Data,"appSettings"),"autoCheckUpdates"));}}
    void StartUpdateChecks(){
        if(smoke)return;
        updateTimer.Tick+=async(s,e)=>{if(AutoCheckUpdates&&!updateExitPending)await Updates.Check();};updateTimer.Start();
        if(AutoCheckUpdates)BeginInvoke(new Action(async()=>await Updates.Check()));
    }
    async Task<object> HandleUpdates(object input){
        switch(J.Str(input,"action","status")){
            case "status":return Updates.Snapshot();
            case "check":return await Updates.Check();
            case "download":return await Updates.DownloadUpdate();
            case "cancel":Updates.Cancel();return Updates.Snapshot();
            case "install":return await InstallUpdate();
            default:throw J.Error("未知更新操作。");
        }
    }
    async Task<object> InstallUpdate(){
        if(smoke)throw J.Error("测试模式不会替换或重启程序。");
        if(sessionOperation||!await gate.WaitAsync(0))throw J.Error("当前操作尚未结束，请稍后再安装更新。");
        try{
            if(updateExitPending)throw J.Error("更新正在启动。");
            string helper=Path.Combine(Program.BaseDir,"Updater.exe");if(!File.Exists(helper))throw J.Error("更新器文件缺失，请重新解压完整程序包。");
            string probe=Path.Combine(Program.BaseDir,".update-write-"+Guid.NewGuid().ToString("N"));
            try{using(File.Create(probe)){}File.Delete(probe);}catch(UnauthorizedAccessException){throw J.Error("程序目录不可写，请将完整程序文件夹移到当前用户可写的目录后更新。");}
            using(var process=Process.GetCurrentProcess()){
                var plan=Updates.InstallPlan(Program.BaseDir,process.Id,process.StartTime.ToUniversalTime().Ticks);
                string work=Path.GetDirectoryName(J.Str(plan,"archive")),worker=Path.Combine(work,"Updater.exe"),path=Path.Combine(work,"plan.json");
                File.Copy(helper,worker,true);J.Atomic(path,J.Encode(plan));
                using(var child=Process.Start(new ProcessStartInfo(worker,"--apply \""+path+"\""){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=work}))if(child==null)throw J.Error("无法启动更新器。");
            }
            updateExitPending=true;updateTimer.Stop();refresh.Stop();
            var exit=new Timer {Interval=500};exit.Tick+=(s,e)=>{exit.Stop();exit.Dispose();quitting=true;Close();};exit.Start();
            return true;
        }finally{gate.Release();}
    }
    async Task ConfirmUpdateStartup(string[] args){
        int index=Array.IndexOf(args,"--update-health");if(index<0||index+1>=args.Length)return;
        string file=Path.GetFullPath(args[index+1]),root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Codex Relay Lite","updates")+Path.DirectorySeparatorChar;
        if(!file.StartsWith(root,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(file)!="healthy.json")return;
        for(int n=0;n<150;n++){
            if(view!=null&&await view.CoreWebView2.ExecuteScriptAsync("Boolean(window.relayUiReady&&document.querySelector('#checkUpdates'))")=="true"){
                J.Atomic(file,J.Encode(J.Map("version",AppVersion.Number,"at",J.Now())));return;
            }
            await Task.Delay(100);
        }
    }
}
}
