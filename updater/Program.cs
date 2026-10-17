using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace RelayLite {
internal static class UpdaterProgram {
    [STAThread] static int Main(string[] args){
        Application.EnableVisualStyles();
        if(args.Length!=2||args[0]!="--apply")return 2;
        using(var updating=new Mutex(false,"Local\\CodexRelayLiteUpdating")){
            bool owned=false;try{try{owned=updating.WaitOne(0);}catch(AbandonedMutexException){owned=true;}if(!owned)throw J.Error("另一个更新任务正在运行。");Apply(Path.GetFullPath(args[1]));return 0;}
            catch(Exception ex){MessageBox.Show(ex.Message,"Codex Relay Lite 更新失败",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
            finally{if(owned)updating.ReleaseMutex();}
        }
    }
    internal static void Apply(string planPath,string appMutexName="Local\\CodexRelayLite"){
        var plan=J.Read(planPath,null);string work=Path.GetDirectoryName(planPath),target=Path.GetFullPath(J.Str(plan,"target"));
        string archive=Path.GetFullPath(J.Str(plan,"archive"));
        if(Path.GetDirectoryName(archive)!=work||target.TrimEnd('\\').Equals(work.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)||work.StartsWith(target.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw J.Error("更新工作目录无效。");
        string stage=Path.Combine(work,"extracted"),backup=Path.Combine(work,"backup"),health=Path.Combine(work,"healthy.json");
        string log=Path.Combine(work,"result.json");UpdateTransaction transaction=null;Process restarted=null;bool parentExited=false;
        try{
            int pid=Convert.ToInt32(J.Get(plan,"pid"));long ticks=Convert.ToInt64(J.Get(plan,"startTicks"));
            try{using(var parent=Process.GetProcessById(pid)){if(parent.StartTime.ToUniversalTime().Ticks==ticks&&!parent.WaitForExit(60000))throw J.Error("程序未完全退出，更新已取消。");}}catch(ArgumentException){}
            parentExited=true;
            var files=UpdatePackage.Extract(archive,stage,J.Str(plan,"sha256"),J.Str(plan,"version"));
            using(var appLock=new Mutex(false,appMutexName)){
                bool locked=false;try{try{locked=appLock.WaitOne(0);}catch(AbandonedMutexException){locked=true;}if(!locked)throw J.Error("程序仍在运行，更新已取消。");
                    transaction=new UpdateTransaction(target,stage,backup);transaction.Apply(files);
                }finally{if(locked)appLock.ReleaseMutex();}
            }
            restarted=Process.Start(new ProcessStartInfo(Path.Combine(target,"Codex Relay Lite.exe"),"--update-health \""+health+"\""){UseShellExecute=false,WorkingDirectory=target});
            var watch=Stopwatch.StartNew();
            while(watch.ElapsedMilliseconds<45000&&!File.Exists(health)&&!restarted.HasExited)Thread.Sleep(200);
            if(!File.Exists(health)||J.Str(J.Read(health,null),"version")!=J.Str(plan,"version"))throw J.Error("新版未能正常启动。");
            J.Atomic(log,J.Encode(J.Map("ok",true,"version",J.Str(plan,"version"),"at",J.Now())));
            // Backups stay available for manual recovery; downloaded archives are no longer needed.
            try{File.Delete(archive);}catch(IOException){}catch(UnauthorizedAccessException){}
        }catch(Exception ex){
            if(restarted!=null)try{if(!restarted.HasExited){restarted.Kill();restarted.WaitForExit(10000);}}catch(InvalidOperationException){}
            string message=ex.Message;bool restored=false;
            if(transaction!=null)try{transaction.Rollback();restored=true;message+="\n已恢复旧版本。";}catch(Exception rollback){message+="\n"+rollback.Message;}
            J.Atomic(log,J.Encode(J.Map("ok",false,"error",message,"restored",restored,"backup",backup,"at",J.Now())));
            if(restored||transaction==null&&parentExited)try{Process.Start(new ProcessStartInfo(Path.Combine(target,"Codex Relay Lite.exe"),"--update-health \""+health+"\""){UseShellExecute=true,WorkingDirectory=target});}catch(Exception restart){message+="\n请手动启动程序："+restart.Message;}
            throw J.Error(message+"\n更新记录："+log);
        }finally{if(restarted!=null)restarted.Dispose();}
    }
}
}
