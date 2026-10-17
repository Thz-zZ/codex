using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RelayLite {
internal static class UpdatePackage {
    internal const long MaxArchive=256L*1024*1024,MaxExpanded=512L*1024*1024;
    internal static readonly string[] Required={"Codex Relay Lite.exe","Updater.exe","SessionBackend.exe","Microsoft.Web.WebView2.Core.dll","Microsoft.Web.WebView2.WinForms.dll","WebView2Loader.dll","Nett.dll","ui/index.html","ui/renderer.js","ui/bridge.js","VERSION.txt","package-files.json"};
    static readonly string[] RootFiles={"Codex Relay Lite.exe","Updater.exe","SessionBackend.exe","Microsoft.Web.WebView2.Core.dll","Microsoft.Web.WebView2.WinForms.dll","WebView2Loader.dll","Nett.dll","VERSION.txt","build-info.json","package-files.json","THIRD-PARTY-NOTICES.txt","会话管理说明.md","版本记录.md","使用说明.txt"};
    internal static Version VersionOf(string value){
        Version version;string text=(value??"").Trim().TrimStart('v','V');
        if(!Regex.IsMatch(text,@"^\d+\.\d+\.\d+(\.\d+)?$")||!Version.TryParse(text,out version))throw J.Error("更新版本号无效。");
        return new Version(version.Major,version.Minor,Math.Max(0,version.Build),Math.Max(0,version.Revision));
    }
    internal static string Hash(string path){using(var sha=SHA256.Create())using(var file=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
    internal static bool ValidHash(string value){return Regex.IsMatch(value??"",@"\A[0-9a-fA-F]{64}\z");}
    internal static string SafeName(string name){
        if(string.IsNullOrEmpty(name)||name.Contains('\\')||name.StartsWith("/")||name.IndexOfAny(new[]{':','\0'})>=0)throw J.Error("更新包包含无效路径。");
        var parts=name.Split('/');
        foreach(string part in parts)if(part==""||part=="."||part==".."||part.TrimEnd(' ','.')!=part||part.IndexOfAny(Path.GetInvalidFileNameChars())>=0||Regex.IsMatch(part,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)",RegexOptions.IgnoreCase))throw J.Error("更新包包含无效文件名。");
        bool allowed=RootFiles.Contains(name,StringComparer.OrdinalIgnoreCase)||parts.Length>1&&parts[0]=="ui"&&new[]{".html",".js",".css",".png",".ico"}.Contains(Path.GetExtension(name),StringComparer.OrdinalIgnoreCase)||parts.Length>1&&parts[0]=="session-licenses";
        if(!allowed)throw J.Error("更新包包含非程序文件："+name);
        return name;
    }
    internal static string Under(string root,string name){
        string basis=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string path=Path.GetFullPath(Path.Combine(basis,SafeName(name).Replace('/',Path.DirectorySeparatorChar)));
        if(!path.StartsWith(basis,StringComparison.OrdinalIgnoreCase))throw J.Error("更新路径超出程序目录。");
        for(string current=path;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw J.Error("更新路径不能包含目录链接。");
        return path;
    }
    internal static List<string> FileList(string root){
        var raw=J.Arr(J.Get(J.Read(Path.Combine(root,"package-files.json"),null),"files"));
        var files=raw.Select(x=>SafeName(J.S(x))).ToList();
        if(files.Count==0||files.Count>5000||files.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Count)throw J.Error("更新文件清单无效。");
        return files;
    }
    internal static List<string> Extract(string archive,string stage,string hash,string version){
        if(!ValidHash(hash)||Hash(archive)!=hash.ToLowerInvariant())throw J.Error("更新包校验失败，请重新下载。");
        Directory.CreateDirectory(stage);var files=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long expanded=0;
        using(var zip=ZipFile.OpenRead(archive)){
            if(zip.Entries.Count>5000)throw J.Error("更新包文件数量超限。");
            foreach(var entry in zip.Entries){
                string entryName=entry.FullName.Replace('\\','/');if(entryName.EndsWith("/"))continue;
                string name=SafeName(entryName);if(!seen.Add(name))throw J.Error("更新包包含重复文件。");
                if(((entry.ExternalAttributes>>16)&0xf000)==0xa000)throw J.Error("更新包不能包含文件链接。");
                expanded+=entry.Length;if(expanded>MaxExpanded)throw J.Error("更新包解压大小超限。");
                string path=Under(stage,name);Directory.CreateDirectory(Path.GetDirectoryName(path));
                using(var input=entry.Open())using(var output=new FileStream(path,FileMode.CreateNew))input.CopyTo(output);
                files.Add(name);
            }
        }
        if(Required.Any(name=>!seen.Contains(name)))throw J.Error("更新包不完整，请发布完整 Windows x64 程序包。");
        var declared=FileList(stage);
        if(!new HashSet<string>(declared,StringComparer.OrdinalIgnoreCase).SetEquals(files))throw J.Error("更新包与文件清单不一致。");
        if(VersionOf(File.ReadAllText(Path.Combine(stage,"VERSION.txt")))!=VersionOf(version)||VersionOf(FileVersionInfo.GetVersionInfo(Path.Combine(stage,"Codex Relay Lite.exe")).FileVersion)!=VersionOf(version))throw J.Error("更新包内的程序版本与发布版本不一致。");
        return files;
    }
}

internal sealed class UpdateTransaction {
    readonly string target,stage,backup;
    readonly List<string> touched=new List<string>();
    readonly HashSet<string> existed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal UpdateTransaction(string target,string stage,string backup){this.target=Path.GetFullPath(target);this.stage=Path.GetFullPath(stage);this.backup=Path.GetFullPath(backup);}
    internal void Apply(List<string> files){
        var old=File.Exists(Path.Combine(target,"package-files.json"))?UpdatePackage.FileList(target):new List<string>();
        var all=files.Concat(old).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Back up every managed file before replacing any of them.
        foreach(string name in all){string dest=UpdatePackage.Under(target,name);if(Directory.Exists(dest))throw J.Error("更新目标被同名文件夹占用："+name);if(File.Exists(dest)){string copy=UpdatePackage.Under(backup,name);Directory.CreateDirectory(Path.GetDirectoryName(copy));File.Copy(dest,copy,false);existed.Add(name);}}
        J.Atomic(Path.Combine(backup,"transaction.json"),J.Encode(J.Map("target",target,"files",all,"existed",existed.ToArray())));
        foreach(string name in all){string dest=UpdatePackage.Under(target,name);touched.Add(name);if(files.Contains(name,StringComparer.OrdinalIgnoreCase)){Directory.CreateDirectory(Path.GetDirectoryName(dest));string temp=dest+".update-"+Guid.NewGuid().ToString("N");try{File.Copy(UpdatePackage.Under(stage,name),temp);if(File.Exists(dest))File.Replace(temp,dest,null);else File.Move(temp,dest);}finally{if(File.Exists(temp))File.Delete(temp);}}else if(File.Exists(dest))File.Delete(dest);}
    }
    internal void Rollback(){
        var errors=new List<string>();
        foreach(string name in touched.AsEnumerable().Reverse())try{string dest=UpdatePackage.Under(target,name);if(existed.Contains(name)){string original=UpdatePackage.Under(backup,name);if(!File.Exists(dest)||UpdatePackage.Hash(dest)!=UpdatePackage.Hash(original))File.Copy(original,dest,true);}else if(File.Exists(dest))File.Delete(dest);}catch(Exception ex){errors.Add(name+": "+ex.Message);}
        if(errors.Count>0)throw J.Error("部分旧文件未能恢复，备份保留在 "+backup+"\n"+string.Join("\n",errors));
    }
}
}
