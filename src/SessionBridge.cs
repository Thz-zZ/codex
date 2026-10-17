using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RelayLite {
internal sealed class SessionBridge : IDisposable {
    readonly string home, executable;
    readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
    Process process;
    int sequence;

    public SessionBridge(string home,string executable){this.home=home;this.executable=executable;}

    static string Quote(string value){
        var output=new StringBuilder("\"");int slashes=0;
        foreach(char c in value){
            if(c=='\\'){slashes++;continue;}
            if(c=='"'){output.Append('\\',slashes*2+1).Append(c);slashes=0;continue;}
            output.Append('\\',slashes).Append(c);slashes=0;
        }
        return output.Append('\\',slashes*2).Append('"').ToString();
    }

    void Start(){
        if(process!=null&&!process.HasExited)return;
        if(process!=null){process.Dispose();process=null;}
        string backend=Path.Combine(Program.BaseDir,"SessionBackend.exe");
        if(!File.Exists(backend))throw J.Error("会话服务文件缺失，请重新解压完整应用文件夹。");
        var info=new ProcessStartInfo(backend,"--home "+Quote(home)+(executable!=""?" --codex "+Quote(executable):"")){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
            RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false),
            WorkingDirectory=Program.BaseDir
        };
        info.EnvironmentVariables["PYTHONIOENCODING"]="utf-8";
        process=new Process {StartInfo=info};
        process.Start();process.BeginErrorReadLine();
    }

    public async Task<object> Call(string action,object input,Action<object> progress){
        await gate.WaitAsync();
        try{
            Start();int id=++sequence;
            // Write UTF-8 directly: .NET Framework's redirected stdin otherwise uses the system code page.
            byte[] request=Encoding.UTF8.GetBytes(J.Encode(J.Map("id",id,"action",action,"args",input))+"\n");
            await process.StandardInput.BaseStream.WriteAsync(request,0,request.Length);
            await process.StandardInput.BaseStream.FlushAsync();
            while(true){
                string line=await process.StandardOutput.ReadLineAsync();
                if(line==null)throw J.Error("会话服务已退出，请刷新后核对操作结果。");
                var response=J.Decode(line);
                if(J.Str(response,"event")=="progress"){if(progress!=null)progress(response);continue;}
                if(J.Has(response,"id")&&J.Num(response,"id")!=id)throw J.Error("会话服务响应不匹配，请重新打开会话页面。");
                if(!J.True(J.Get(response,"ok")))throw J.Error(J.Str(response,"error","会话操作失败。"));
                return J.Get(response,"data");
            }
        }finally{gate.Release();}
    }

    public void Dispose(){
        if(process==null)return;
        try{
            if(!process.HasExited){
                process.StandardInput.WriteLine("{\"action\":\"shutdown\"}");
                process.StandardInput.Close();
                // The backend closes only its own private Codex app-server before exiting.
                if(!process.WaitForExit(6000))process.Kill();
            }
        }catch(InvalidOperationException){}catch(IOException){}
        finally{process.Dispose();process=null;}
    }
}
}
