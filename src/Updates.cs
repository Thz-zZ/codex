using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RelayLite {
internal sealed class UpdateRequestException:Exception {
    internal readonly HttpStatusCode StatusCode;
    internal UpdateRequestException(HttpStatusCode status):base(status==HttpStatusCode.Forbidden||status==(HttpStatusCode)429?"GitHub 请求暂时受限，请稍后重试。":"检查更新失败：HTTP "+(int)status){StatusCode=status;}
}
internal sealed class UpdateService {
    internal const string Repository="Thz-zZ/codex",ReleaseUrl="https://github.com/"+Repository+"/releases",ApiUrl="https://api.github.com/repos/"+Repository+"/releases/latest";
    readonly Func<string,CancellationToken,Task<object>> readJson;
    readonly Func<CancellationToken,Task<Uri>> resolveLatest;
    readonly Func<string,string,IProgress<int>,CancellationToken,Task> download;
    readonly string directory;
    readonly Action<object> changed;
    readonly string currentVersion;
    CancellationTokenSource cancellation;
    Dictionary<string,object> release;
    string status="idle",error="",checkedAt="",archive="";
    int progress;
    internal bool Busy {get{return status=="checking"||status=="downloading";}}
    internal UpdateService(string directory,Action<object> changed,Func<string,CancellationToken,Task<object>> readJson=null,Func<string,string,IProgress<int>,CancellationToken,Task> download=null,string currentVersion=null,Func<CancellationToken,Task<Uri>> resolveLatest=null){this.directory=directory;this.changed=changed;this.readJson=readJson??ReadJson;this.download=download??Download;this.currentVersion=currentVersion??AppVersion.Number;this.resolveLatest=resolveLatest??ResolveLatest;}
    internal object Snapshot(){return J.Map("status",status,"currentVersion",currentVersion,"version",J.Str(release,"version"),"notes",J.Str(release,"notes"),"releaseUrl",ReleaseUrl,"checkedAt",checkedAt,"progress",progress,"error",error);}
    void Notify(){if(changed!=null)changed(Snapshot());}
    internal static string AssetUrl(string url,string tag){
        Uri uri;string prefix=ReleaseUrl+"/download/"+Uri.EscapeDataString(tag)+"/";
        if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!=""||!url.StartsWith(prefix,StringComparison.Ordinal)||uri.AbsolutePath.Substring(new Uri(prefix).AbsolutePath.Length).Contains('/'))throw J.Error("更新下载地址不属于本项目 Release。");
        return url;
    }
    internal static Dictionary<string,object> ParseManifest(object manifest,string tag,object release){
        string version=J.Str(manifest,"version");if(UpdatePackage.VersionOf(version)!=UpdatePackage.VersionOf(tag)||J.Str(manifest,"platform")!="windows-x64"||!UpdatePackage.ValidHash(J.Str(manifest,"sha256")))throw J.Error("新版发布清单无效。");
        long size=(long)(J.Count(J.Get(manifest,"size"))??0);if(size<=0||size>UpdatePackage.MaxArchive)throw J.Error("更新包大小无效。");
        string notes=J.Str(release,"body");if(notes=="")notes=J.Str(manifest,"notes");
        return J.Map("version",version,"url",AssetUrl(J.Str(manifest,"url"),tag),"sha256",J.Str(manifest,"sha256").ToLowerInvariant(),"size",size,"notes",notes.Substring(0,Math.Min(12000,notes.Length)));
    }
    internal static string ReleaseTag(Uri uri){
        string prefix=new Uri(ReleaseUrl).AbsolutePath+"/tag/";
        if(uri==null||!uri.IsAbsoluteUri||uri.Scheme!="https"||uri.Host!="github.com"||!uri.IsDefaultPort||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!=""||!uri.AbsolutePath.StartsWith(prefix,StringComparison.Ordinal))throw J.Error("更新发布页地址不属于本项目 Release。");
        string tag=Uri.UnescapeDataString(uri.AbsolutePath.Substring(prefix.Length));UpdatePackage.VersionOf(tag);return tag;
    }
    static bool CanUseReleasePage(Exception error,CancellationToken token){
        if(token.IsCancellationRequested)return false;
        var response=error as UpdateRequestException;
        return error is HttpRequestException||error is OperationCanceledException||response!=null&&(response.StatusCode==HttpStatusCode.Forbidden||response.StatusCode==(HttpStatusCode)429||(int)response.StatusCode>=500);
    }
    async Task<object> Latest(CancellationToken token){
        try{return await readJson(ApiUrl,token);}
        catch(Exception ex) when(CanUseReleasePage(ex,token)){
            token.ThrowIfCancellationRequested();var uri=await resolveLatest(token);if(uri==null)return null;
            string tag=ReleaseTag(uri);
            return J.Map("tag_name",tag,"assets",new[]{J.Map("name","update.json","browser_download_url",ReleaseUrl+"/download/"+Uri.EscapeDataString(tag)+"/update.json")});
        }
    }
    internal async Task<object> Check(){
        if(Busy||status=="ready")return Snapshot();
        status="checking";error="";Notify();cancellation=new CancellationTokenSource();cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        try{
            object latest=await Latest(cancellation.Token);string tag=J.Str(latest,"tag_name");
            if(latest==null){release=null;status="unpublished";}
            else if(J.True(J.Get(latest,"draft"))||J.True(J.Get(latest,"prerelease")))throw J.Error("更新源未返回正式版本。");
            else if(UpdatePackage.VersionOf(tag)<=UpdatePackage.VersionOf(currentVersion)){release=null;status="current";}
            else{
                object asset=J.Arr(J.Get(latest,"assets")).FirstOrDefault(x=>J.Str(x,"name")=="update.json");
                if(asset==null)throw J.Error("发现新版 "+tag+"，但尚未上传完整更新包。");
                string url=AssetUrl(J.Str(asset,"browser_download_url"),tag);
                object manifest;
                try{manifest=await readJson(url,cancellation.Token);}catch(UpdateRequestException ex) when(ex.StatusCode==HttpStatusCode.NotFound){throw J.Error("发现新版 "+tag+"，但尚未上传完整更新包。");}
                release=ParseManifest(manifest,tag,latest);status="available";
            }
            checkedAt=J.Now();
        }catch(OperationCanceledException){error="检查已取消或连接超时，请重试。";status="error";}catch(Exception ex){error=ex.Message;status="error";}
        finally{cancellation.Dispose();cancellation=null;Notify();}
        return Snapshot();
    }
    internal async Task<object> DownloadUpdate(){
        if(Busy)return Snapshot();if(release==null)throw J.Error("请先检查可用更新。");if(status=="ready")return Snapshot();
        status="downloading";error="";progress=0;Notify();cancellation=new CancellationTokenSource();cancellation.CancelAfter(TimeSpan.FromMinutes(20));
        string work=Path.Combine(directory,Guid.NewGuid().ToString("N"));archive=Path.Combine(work,"package.zip");
        try{
            Directory.CreateDirectory(work);
            var reporter=new Progress<int>(value=>{if(status=="downloading"){progress=Math.Max(0,Math.Min(99,value));Notify();}});
            await download(J.Str(release,"url"),archive,reporter,cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if(new FileInfo(archive).Length!=(long)J.Num(release,"size"))throw J.Error("更新包大小不符，请重试下载。");
            await Task.Run(()=>UpdatePackage.Extract(archive,Path.Combine(work,"validated"),J.Str(release,"sha256"),J.Str(release,"version")));
            cancellation.Token.ThrowIfCancellationRequested();progress=100;status="ready";
        }catch(OperationCanceledException){status="available";DeletePartial();}catch(Exception ex){error=ex.Message;status="error";DeletePartial();}
        finally{cancellation.Dispose();cancellation=null;Notify();}
        return Snapshot();
    }
    void DeletePartial(){if(File.Exists(archive))File.Delete(archive);archive="";}
    internal void Cancel(){if(cancellation!=null)cancellation.Cancel();}
    internal object InstallPlan(string target,int pid,long startTicks){
        if(status!="ready"||!File.Exists(archive))throw J.Error("请先下载更新包。");
        return J.Map("target",Path.GetFullPath(target),"archive",archive,"sha256",J.Str(release,"sha256"),"version",J.Str(release,"version"),"pid",pid,"startTicks",startTicks);
    }
    internal static HttpClient Client(TimeSpan timeout){
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        var http=new HttpClient(new HttpClientHandler {AllowAutoRedirect=false}){Timeout=timeout};http.DefaultRequestHeaders.UserAgent.ParseAdd("CodexRelayLite/"+AppVersion.Number);return http;
    }
    static bool TrustedRedirect(Uri uri){return uri.Scheme=="https"&&uri.UserInfo==""&&(uri.Host=="github.com"||uri.Host=="api.github.com"||uri.Host=="release-assets.githubusercontent.com"||uri.Host=="objects.githubusercontent.com");}
    static async Task<HttpResponseMessage> Get(HttpClient http,string url,CancellationToken token){
        var uri=new Uri(url);
        for(int i=0;i<6;i++){
            var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token);
            if((int)response.StatusCode>=300&&(int)response.StatusCode<400&&response.Headers.Location!=null){var next=new Uri(uri,response.Headers.Location);response.Dispose();if(!TrustedRedirect(next))throw J.Error("更新服务器重定向地址无效。");uri=next;continue;}
            return response;
        }
        throw J.Error("更新服务器重定向次数过多。");
    }
    static async Task<object> ReadJson(string url,CancellationToken token){
        using(var http=Client(TimeSpan.FromSeconds(20)))using(var response=await Get(http,url,token)){
            if(response.StatusCode==HttpStatusCode.NotFound&&url==ApiUrl)return null;
            if(!response.IsSuccessStatusCode)throw new UpdateRequestException(response.StatusCode);
            using(var input=await response.Content.ReadAsStreamAsync())using(var output=new MemoryStream()){
                var buffer=new byte[8192];int count;while((count=await input.ReadAsync(buffer,0,buffer.Length,token))>0){if(output.Length+count>1024*1024)throw J.Error("更新清单过大。");output.Write(buffer,0,count);}
                return J.Decode(System.Text.Encoding.UTF8.GetString(output.ToArray()));
            }
        }
    }
    static async Task<Uri> ResolveLatest(CancellationToken token){
        using(var http=Client(TimeSpan.FromSeconds(20)))using(var response=await Get(http,ReleaseUrl+"/latest",token)){
            if(response.StatusCode==HttpStatusCode.NotFound)return null;
            if(!response.IsSuccessStatusCode)throw new UpdateRequestException(response.StatusCode);
            return response.RequestMessage.RequestUri;
        }
    }
    static async Task Download(string url,string path,IProgress<int> progress,CancellationToken token){
        using(var http=Client(TimeSpan.FromMinutes(20)))using(var response=await Get(http,url,token)){
            if(!response.IsSuccessStatusCode)throw J.Error("下载更新失败：HTTP "+(int)response.StatusCode);
            long total=response.Content.Headers.ContentLength??0,received=0;if(total>UpdatePackage.MaxArchive)throw J.Error("更新包大小超限。");
            using(var input=await response.Content.ReadAsStreamAsync())using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)){
                var buffer=new byte[65536];int count,last=-1;while((count=await input.ReadAsync(buffer,0,buffer.Length,token))>0){received+=count;if(received>UpdatePackage.MaxArchive)throw J.Error("更新包大小超限。");await output.WriteAsync(buffer,0,count,token);int value=total>0?(int)(received*100/total):0;if(value!=last){last=value;progress.Report(value);}}
            }
        }
    }
}
}
