using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RelayLite {
internal sealed partial class MainForm {
    List<Process> smokeSessionProcesses;

    List<Process> SmokeSessionProcesses(){
        var result=new List<Process>();
        foreach(var process in Process.GetProcessesByName("SessionBackend")){
            bool owned=false;
            try{owned=string.Equals(process.MainModule.FileName,Path.Combine(Program.BaseDir,"SessionBackend.exe"),StringComparison.OrdinalIgnoreCase);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
            if(owned)result.Add(process);else process.Dispose();
        }
        return result;
    }
    void CaptureSessionMemorySmoke(){
        smokeSessionProcesses=SmokeSessionProcesses();
        if(smokeSessionProcesses.Count==0)throw J.Error("Session memory smoke needs a running isolated backend.");
    }
    async Task CheckSessionMemorySmoke(){
        var original=smokeSessionProcesses;
        await sessionRelease;
        for(int n=0;n<100&&original.Any(p=>!p.HasExited);n++)await Task.Delay(50);
        if(original.Any(p=>!p.HasExited))throw J.Error("Tray hide left the isolated session backend running.");
        var oldIds=original.Select(p=>p.Id).ToArray();
        var restarted=new List<Process>();
        try{
            // Immediate reuse also exercises waiting for an asynchronous release.
            Task<object> status=SessionCall("status",J.Map());
            if(await status==null)throw J.Error("Session service did not restart.");
            restarted=SmokeSessionProcesses();
            if(restarted.Count==0||restarted.Any(p=>oldIds.Contains(p.Id)))throw J.Error("Session service reused a released process.");
            ReleaseIdleSessionBridge();
            Task<object> immediate=SessionCall("status",J.Map());
            if(await immediate==null)throw J.Error("Immediate session reuse failed.");
            ReleaseIdleSessionBridge();await sessionRelease;
            for(int n=0;n<100&&restarted.Any(p=>!p.HasExited);n++)await Task.Delay(50);
            if(restarted.Any(p=>!p.HasExited))throw J.Error("Restarted session service was not released.");
            J.Atomic(Path.Combine(qaDir,"memory-result.json"),J.Encode(J.Map("ok",true,"initialSessionProcesses",original.Count,"traySessionProcessesExited",true,"lazySessionRestart",true,"reuseDuringRelease",true,"cacheEntriesLimit",24,"cacheSerializedBytesLimit",4*1024*1024)));
        }finally{foreach(var process in original.Concat(restarted))process.Dispose();}
    }
}
}
