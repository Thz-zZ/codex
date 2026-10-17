using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RelayLite {
internal static class UpdateChecks {
    static void Check(bool value,string label){if(!value)throw J.Error("Update checks: "+label);}
    static void Reject(Action action,string label){bool failed=false;try{action();}catch{failed=true;}Check(failed,label);}
    static void Write(string root,string name,string value){string path=Path.Combine(root,name.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,value);}
    internal static async Task Run(string root,List<string> checks){
        string dir=Path.Combine(root,"updates"),payload=Path.Combine(dir,"payload");Directory.CreateDirectory(payload);
        Check(UpdatePackage.VersionOf("v1.10.0")>UpdatePackage.VersionOf("1.9.9")&&UpdatePackage.VersionOf("1.1.4")==UpdatePackage.VersionOf("1.1.4.0"),"numeric stable version comparison");
        foreach(string path in new[]{"../providers.json","ui/../renderer.js","ui\\renderer.js","ui/a.js:stream","ui/CON.js","ui/a. ","/ui/renderer.js","providers.json","config.toml"})Reject(()=>UpdatePackage.SafeName(path),"reject unsafe paths: "+path);
        Reject(()=>UpdateService.AssetUrl("https://evil.example/package.zip","v1.1.4"),"reject foreign package host");
        Reject(()=>UpdateService.AssetUrl(UpdateService.ReleaseUrl+"/download/v1.1.4/a.zip?redirect=x","v1.1.4"),"reject asset query injection");
        foreach(string name in UpdatePackage.Required)if(name=="Codex Relay Lite.exe")File.Copy(Path.Combine(Program.BaseDir,name),Path.Combine(payload,name));else Write(payload,name,name=="VERSION.txt"?AppVersion.Number:"fixture");
        J.Atomic(Path.Combine(payload,"package-files.json"),J.Encode(J.Map("files",UpdatePackage.Required)));
        string archive=Path.Combine(dir,"valid.zip");ZipFile.CreateFromDirectory(payload,archive);string hash=UpdatePackage.Hash(archive);
        var files=UpdatePackage.Extract(archive,Path.Combine(dir,"valid-extracted"),hash,AppVersion.Number);Check(files.Count==UpdatePackage.Required.Length,"complete package extracts");
        Reject(()=>UpdatePackage.Extract(archive,Path.Combine(dir,"bad-hash"),new string('0',64),AppVersion.Number),"hash mismatch rejected");
        Reject(()=>UpdatePackage.Extract(archive,Path.Combine(dir,"bad-version"),hash,"9.9.9"),"binary version mismatch rejected");
        foreach(string entry in new[]{"../outside.txt","providers.json","ui/CON.js","ui/index.html"}){
            string bad=Path.Combine(dir,Guid.NewGuid().ToString("N")+".zip");File.Copy(archive,bad);
            using(var zip=ZipFile.Open(bad,ZipArchiveMode.Update))using(var writer=new StreamWriter(zip.CreateEntry(entry).Open()))writer.Write("untrusted");
            Reject(()=>UpdatePackage.Extract(bad,Path.Combine(dir,Guid.NewGuid().ToString("N")),UpdatePackage.Hash(bad),AppVersion.Number),"zip traversal, user data and duplicate protection");
        }
        var tag="v"+AppVersion.Number;object manifest=J.Map("version",AppVersion.Number,"platform","windows-x64","url",UpdateService.ReleaseUrl+"/download/"+tag+"/test.zip","size",new FileInfo(archive).Length,"sha256",hash,"notes","manifest notes");
        object release=J.Map("tag_name",tag,"body","fixture notes","assets",new[]{J.Map("name","update.json","browser_download_url",UpdateService.ReleaseUrl+"/download/"+tag+"/update.json")});
        Func<string,CancellationToken,Task<object>> reader=(url,token)=>Task.FromResult(url==UpdateService.ApiUrl?release:manifest);
        var service=new UpdateService(Path.Combine(dir,"downloads"),null,reader,(url,path,report,token)=>{File.Copy(archive,path);report.Report(100);return Task.CompletedTask;},"1.0.0");
        var discovered=await service.Check();Check(J.Str(discovered,"status")=="available"&&J.Str(discovered,"notes")=="fixture notes","new release discovered with release notes");
        Check(J.Str(await service.DownloadUpdate(),"status")=="ready","full package downloads and validates");
        var plan=service.InstallPlan(payload,123,456);Check(J.Str(plan,"sha256")==hash&&J.Str(plan,"version")==AppVersion.Number,"install plan bound to verified release");
        var same=new UpdateService(dir,null,reader);Check(J.Str(await same.Check(),"status")=="current","same version is not offered");
        var newer=new UpdateService(dir,null,reader,null,"99.0.0");Check(J.Str(await newer.Check(),"status")=="current","downgrades are not offered");
        var absent=new UpdateService(dir,null,(url,token)=>Task.FromResult<object>(null));Check(J.Str(await absent.Check(),"status")=="unpublished","repository without releases handled");
        var missing=new UpdateService(dir,null,(url,token)=>Task.FromResult<object>(J.Map("tag_name","v99.0.0")));Check(J.Str(await missing.Check(),"status")=="error","missing manifest explained");
        await FallbackChecks(dir,manifest,tag);checks.Add("GitHub rate limits, release-page fallback, manifest notes, cancellation and invalid fallback rejection");
        var corrupt=new UpdateService(Path.Combine(dir,"corrupt"),null,reader,(url,path,report,token)=>{File.WriteAllBytes(path,new byte[new FileInfo(archive).Length]);return Task.CompletedTask;},"1.0.0");
        await corrupt.Check();Check(J.Str(await corrupt.DownloadUpdate(),"status")=="error","corrupt archive rejected before restart");Reject(()=>corrupt.InstallPlan(payload,1,1),"failed downloads cannot be installed");
        var started=new TaskCompletionSource<bool>();int calls=0;
        var cancelled=new UpdateService(Path.Combine(dir,"cancel"),null,reader,async(url,path,report,token)=>{calls++;File.WriteAllText(path,"partial");started.SetResult(true);await Task.Delay(30000,token);},"1.0.0");
        await cancelled.Check();var downloading=cancelled.DownloadUpdate();await started.Task;
        Check(J.Str(await cancelled.DownloadUpdate(),"status")=="downloading"&&calls==1,"concurrent downloads coalesced");cancelled.Cancel();Check(J.Str(await downloading,"status")=="available"&&!Directory.GetFiles(Path.Combine(dir,"cancel"),"*.zip",SearchOption.AllDirectories).Any(),"cancel removes partial download and permits retry");
        checks.Add("update discovery, version comparison, trusted URLs, full package validation, cancellation and checksum rejection");
        TransactionChecks(dir);checks.Add("update replacement, obsolete managed files, rollback, locked-file recovery and user data preservation");
        await WorkerCheck(dir);checks.Add("standalone updater installs and verifies restarted process in an isolated directory");
    }
    static async Task FallbackChecks(string dir,object manifest,string tag){
        var page=new Uri(UpdateService.ReleaseUrl+"/tag/"+tag);
        Check(UpdateService.ReleaseTag(page)==tag,"official release redirect resolves tag");
        foreach(string url in new[]{"http://github.com/"+UpdateService.Repository+"/releases/tag/"+tag,"https://github.com:444/"+UpdateService.Repository+"/releases/tag/"+tag,"https://github.com/other/repo/releases/tag/"+tag,"https://github.com.evil.example/"+UpdateService.Repository+"/releases/tag/"+tag,page+"?redirect=other",page+"/extra",page+"-rc.1"})Reject(()=>UpdateService.ReleaseTag(new Uri(url)),"reject untrusted or nonstable fallback: "+url);
        foreach(Exception failure in new Exception[]{new UpdateRequestException(HttpStatusCode.Forbidden),new UpdateRequestException((HttpStatusCode)429),new UpdateRequestException(HttpStatusCode.ServiceUnavailable),new HttpRequestException("API unavailable"),new TaskCanceledException("API timeout")}){
            int redirects=0;string requested="";
            var service=new UpdateService(dir,null,(url,token)=>{if(url==UpdateService.ApiUrl)throw failure;requested=url;return Task.FromResult(manifest);},null,"1.0.0",token=>{redirects++;return Task.FromResult(page);});
            var state=await service.Check();Check(J.Str(state,"status")=="available"&&J.Str(state,"notes")=="manifest notes"&&redirects==1&&requested==UpdateService.ReleaseUrl+"/download/"+tag+"/update.json","API failures use official release and validated manifest: "+failure.Message);
        }
        Func<string,CancellationToken,Task<object>> limited=(url,token)=>{Check(url==UpdateService.ApiUrl,"current releases do not require a manifest");throw new UpdateRequestException(HttpStatusCode.Forbidden);};
        foreach(string current in new[]{AppVersion.Number,"99.0.0"}){
            var service=new UpdateService(dir,null,limited,null,current,token=>Task.FromResult(page));Check(J.Str(await service.Check(),"status")=="current","fallback does not downgrade or require assets for current version");
        }
        var absent=new UpdateService(dir,null,limited,null,"1.0.0",token=>Task.FromResult<Uri>(null));Check(J.Str(await absent.Check(),"status")=="unpublished","fallback without published release handled");
        var failed=new UpdateService(dir,null,limited,null,"1.0.0",token=>{throw new UpdateRequestException(HttpStatusCode.ServiceUnavailable);});
        var failedState=await failed.Check();Check(J.Str(failedState,"status")=="error"&&J.Str(failedState,"error").Contains("503"),"fallback failure is reported, never latest");
        var badPage=new UpdateService(dir,null,limited,null,"1.0.0",token=>Task.FromResult(new Uri("https://github.com/other/repo/releases/tag/"+tag)));Check(J.Str(await badPage.Check(),"status")=="error","foreign fallback release rejected before fetching assets");
        var missing=new UpdateService(dir,null,(url,token)=>{throw new UpdateRequestException(url==UpdateService.ApiUrl?HttpStatusCode.Forbidden:HttpStatusCode.NotFound);},null,"1.0.0",token=>Task.FromResult(page));
        var missingState=await missing.Check();Check(J.Str(missingState,"status")=="error"&&J.Str(missingState,"error").Contains("完整更新包"),"new fallback release requires manifest");
        var invalid=new Dictionary<string,object>(J.Obj(manifest));invalid["sha256"]="invalid";
        var unverified=new UpdateService(dir,null,(url,token)=>{if(url==UpdateService.ApiUrl)throw new UpdateRequestException((HttpStatusCode)429);return Task.FromResult<object>(invalid);},null,"1.0.0",token=>Task.FromResult(page));Check(J.Str(await unverified.Check(),"status")=="error","fallback never bypasses manifest validation");
        var denied=new UpdateService(dir,null,(url,token)=>{throw new UpdateRequestException(HttpStatusCode.Unauthorized);},null,"1.0.0",token=>{throw J.Error("unexpected fallback");});
        Check(J.Str(await denied.Check(),"error").Contains("401"),"nontransient API failures are preserved");
        int cancelledRedirects=0;var started=new TaskCompletionSource<bool>();
        var cancelled=new UpdateService(dir,null,async(url,token)=>{started.SetResult(true);await Task.Delay(30000,token);return null;},null,"1.0.0",token=>{cancelledRedirects++;return Task.FromResult(page);});
        var checking=cancelled.Check();await started.Task;cancelled.Cancel();Check(J.Str(await checking,"status")=="error"&&cancelledRedirects==0,"cancelled check does not start fallback");
    }
    static void TransactionChecks(string root){
        string target=Path.Combine(root,"installed"),stage=Path.Combine(root,"new-files");
        Write(target,"ui/renderer.js","old");Write(target,"ui/obsolete.js","obsolete");Write(target,"providers.json","user data");Write(target,"config.toml","codex config");
        J.Atomic(Path.Combine(target,"package-files.json"),J.Encode(J.Map("files",new[]{"ui/renderer.js","ui/obsolete.js","package-files.json"})));
        Write(stage,"ui/renderer.js","new");Write(stage,"ui/updates.js","new updates");
        var files=new List<string>{"ui/renderer.js","ui/updates.js","package-files.json"};J.Atomic(Path.Combine(stage,"package-files.json"),J.Encode(J.Map("files",files)));
        var transaction=new UpdateTransaction(target,stage,Path.Combine(root,"backup"));transaction.Apply(files);
        Check(File.ReadAllText(Path.Combine(target,"ui/renderer.js"))=="new"&&!File.Exists(Path.Combine(target,"ui/obsolete.js")),"new files and obsolete removal");transaction.Rollback();
        Check(File.ReadAllText(Path.Combine(target,"ui/renderer.js"))=="old"&&File.Exists(Path.Combine(target,"ui/obsolete.js"))&&!File.Exists(Path.Combine(target,"ui/updates.js")),"rollback restores previous files and removes additions");
        Check(File.ReadAllText(Path.Combine(target,"providers.json"))=="user data"&&File.ReadAllText(Path.Combine(target,"config.toml"))=="codex config","user data and codex config unchanged");
        var locked=new UpdateTransaction(target,stage,Path.Combine(root,"locked-backup"));
        using(var handle=new FileStream(Path.Combine(target,"ui/renderer.js"),FileMode.Open,FileAccess.Read,FileShare.Read)){Reject(()=>locked.Apply(files),"locked program file prevents replacement");locked.Rollback();}
        Check(File.ReadAllText(Path.Combine(target,"ui/renderer.js"))=="old","locked original remains intact");
    }
    static async Task WorkerCheck(string root){
        string work=Path.Combine(root,"worker"),target=Path.Combine(root,"worker-target"),payload=Path.Combine(root,"worker-payload");Directory.CreateDirectory(work);Directory.CreateDirectory(target);Directory.CreateDirectory(payload);
        // A tiny fixture executable acknowledges startup without opening the user's app or data.
        using(var compiler=new Microsoft.CSharp.CSharpCodeProvider()){
            var options=new System.CodeDom.Compiler.CompilerParameters(new[]{"System.dll","System.Web.Extensions.dll"},Path.Combine(payload,"Codex Relay Lite.exe")){GenerateExecutable=true,CompilerOptions="/target:winexe"};
            string source="using System;using System.IO;using System.Reflection;[assembly:AssemblyFileVersion(\""+AppVersion.FileVersion+"\")]class Fixture{static void Main(string[] a){if(a.Length==2&&a[0]==\"--update-health\")File.WriteAllText(a[1],\"{\\\"version\\\":\\\""+AppVersion.Number+"\\\"}\");}}";
            var result=compiler.CompileAssemblyFromSource(options,source);Check(!result.Errors.HasErrors,"fixture executable compiled");
        }
        foreach(string name in UpdatePackage.Required)if(name!="Codex Relay Lite.exe")Write(payload,name,name=="VERSION.txt"?AppVersion.Number:"fixture");
        J.Atomic(Path.Combine(payload,"package-files.json"),J.Encode(J.Map("files",UpdatePackage.Required)));
        string zip=Path.Combine(work,"package.zip");ZipFile.CreateFromDirectory(payload,zip);
        Write(target,"Codex Relay Lite.exe","old fixture");Write(target,"providers.json","untouched");
        string plan=Path.Combine(work,"plan.json");J.Atomic(plan,J.Encode(J.Map("target",target,"archive",zip,"sha256",UpdatePackage.Hash(zip),"version",AppVersion.Number,"pid",int.MaxValue,"startTicks",0)));
        var assembly=System.Reflection.Assembly.LoadFile(Path.Combine(Program.BaseDir,"Updater.exe"));
        var apply=assembly.GetType("RelayLite.UpdaterProgram").GetMethod("Apply",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
        await Task.Run(()=>apply.Invoke(null,new object[]{plan,"Local\\CodexRelayLiteUpdateFixture"+Guid.NewGuid().ToString("N")}));
        Check(J.True(J.Get(J.Read(Path.Combine(work,"result.json"),null),"ok"))&&File.Exists(Path.Combine(work,"healthy.json")),"worker health acknowledgement");
        Check(File.ReadAllText(Path.Combine(target,"providers.json"))=="untouched"&&File.ReadAllText(Path.Combine(work,"backup/Codex Relay Lite.exe"))=="old fixture","worker preserves data and backs up old executable");
        string failureWork=Path.Combine(root,"failed-start"),failureTarget=Path.Combine(root,"rollback-target");Directory.CreateDirectory(failureWork);Directory.CreateDirectory(failureTarget);
        File.Copy(Path.Combine(target,"Codex Relay Lite.exe"),Path.Combine(failureTarget,"Codex Relay Lite.exe"));string original=UpdatePackage.Hash(Path.Combine(failureTarget,"Codex Relay Lite.exe"));
        Write(failureTarget,"providers.json","keep on rollback");
        using(var compiler=new Microsoft.CSharp.CSharpCodeProvider()){
            var options=new System.CodeDom.Compiler.CompilerParameters(new[]{"System.dll"},Path.Combine(payload,"Codex Relay Lite.exe")){GenerateExecutable=true,CompilerOptions="/target:winexe"};
            var result=compiler.CompileAssemblyFromSource(options,"using System.Reflection;[assembly:AssemblyFileVersion(\""+AppVersion.FileVersion+"\")]class Broken{static void Main(){}}");Check(!result.Errors.HasErrors,"unhealthy fixture compiled");
        }
        string failedZip=Path.Combine(failureWork,"package.zip");ZipFile.CreateFromDirectory(payload,failedZip);
        string failedPlan=Path.Combine(failureWork,"plan.json");J.Atomic(failedPlan,J.Encode(J.Map("target",failureTarget,"archive",failedZip,"sha256",UpdatePackage.Hash(failedZip),"version",AppVersion.Number,"pid",int.MaxValue,"startTicks",0)));
        await Task.Run(()=>Reject(()=>apply.Invoke(null,new object[]{failedPlan,"Local\\CodexRelayLiteUpdateFixture"+Guid.NewGuid().ToString("N")}),"failed startup rolls back"));
        Check(J.True(J.Get(J.Read(Path.Combine(failureWork,"result.json"),null),"restored"))&&UpdatePackage.Hash(Path.Combine(failureTarget,"Codex Relay Lite.exe"))==original&&File.ReadAllText(Path.Combine(failureTarget,"providers.json"))=="keep on rollback","failed startup restores original executable and user data");
    }
}
}
