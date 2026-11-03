using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RelayLite {
internal static class NativeModelCatalog {
    internal static Func<object> ReadOverride;
    static readonly object gate=new object();
    static string cachedStamp;
    static List<object> cached=new List<object>();
    internal static List<object> Read(string configDir=null){
        if(ReadOverride!=null)return J.Arr(J.Get(ReadOverride(),"models"));
        lock(gate){
            string executable=FindExecutable();
            string stamp=executable==null?"":executable+"|"+File.GetLastWriteTimeUtc(executable).Ticks;
            if(cachedStamp!=stamp){cachedStamp=stamp;cached=Bundled(executable);}
            if(cached.Count>0)return cached;
            // Older clients may not have `debug models --bundled` yet.
            string file=Path.Combine(configDir??Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex"),"models_cache.json");
            try{if(File.Exists(file)&&new FileInfo(file).Length<=16*1024*1024)return J.Arr(J.Get(J.Read(file,null),"models"));}catch{}
            return new List<object>();
        }
    }
    static string FindExecutable(){
        foreach(string folder in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))try{string directory=folder.Trim('"');if(!Path.IsPathRooted(directory))continue;string file=Path.Combine(directory,"codex.exe");if(File.Exists(file))return Path.GetFullPath(file);}catch{}
        foreach(var process in Process.GetProcessesByName("codex"))using(process)try{string file=process.MainModule.FileName;if(File.Exists(file))return file;}catch{}
        return null;
    }
    static List<object> Bundled(string executable){
        if(executable==null)return new List<object>();
        try{
            var info=new ProcessStartInfo(executable,"debug models --bundled"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8};
            using(var process=Process.Start(info)){
                Task<string> output=process.StandardOutput.ReadToEndAsync(),errors=process.StandardError.ReadToEndAsync();
                if(!process.WaitForExit(4000)){try{process.Kill();}catch{}return new List<object>();}
                if(process.ExitCode!=0||!output.Wait(1000)||output.Result.Length>16*1024*1024)return new List<object>();
                return J.Arr(J.Get(J.Decode(output.Result),"models"));
            }
        }catch{return new List<object>();}
    }
}
}
