using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RelayLite {
internal static class ModelCatalog {
    internal const int SchemaVersion=4;
    static Dictionary<string,object> Entry(string id,int priority,List<object> native){
        var match=native.FirstOrDefault(value=>J.Str(value,"slug")==id);
        // Exact IDs only. An alias must not silently inherit a different model's
        // context capacity, prompt, reasoning levels or tool declarations.
        var entry=match==null?J.Map("supported_reasoning_levels",new object[0],"default_reasoning_level",null,
            "shell_type","shell_command","supported_in_api",true,"support_verbosity",false,
            "truncation_policy",J.Map("mode","tokens","limit",10000),"experimental_supported_tools",new object[0],
            "base_instructions","You are Codex, a coding agent. Follow the user's instructions and applicable AGENTS.md files.",
            "default_reasoning_summary","none","input_modalities",new[]{"text"}):J.Obj(J.Clone(match));
        entry["slug"]=id;entry["display_name"]=id;entry["description"]="中转提供的模型；默认设置可在 Codex 中调整。";entry["visibility"]="list";entry["priority"]=priority;
        return entry;
    }
    internal static object Create(object provider,string configDir=null) {
        ModelProtocol.Protocol(provider);
        string model=J.Str(provider,"model").Trim(),effort=J.Str(provider,"reasoning");
        if(model==""||model.Length>256||model.Any(char.IsControl))throw J.Error("模型目录需要有效的完整模型 ID（最多 256 个字符）。");
        bool specified=effort!=""&&effort!="default";
        if(specified&&!ModelProtocol.ValidReasoning(effort))throw J.Error("模型目录包含不支持的推理强度。");
        bool catalog=J.Str(provider,"modelSelection")=="catalog";
        var native=NativeModelCatalog.Read(configDir);
        var models=catalog?Models.Saved(provider).OrderBy(id=>id==model?0:1).ThenBy(id=>id,StringComparer.Ordinal).ToList():new System.Collections.Generic.List<string>{model};
        var entries=models.Select((id,index)=>{
            var entry=Entry(id,index,native);
            if(id==model&&specified){
                var levels=J.Arr(J.Get(entry,"supported_reasoning_levels"));
                if(native.Any(value=>J.Str(value,"slug")==id)&&!levels.Any(value=>J.Str(value,"effort")==effort))throw J.Error("本机 Codex 的模型目录未声明 "+id+" 支持 "+effort+"，请使用该模型支持的强度或跟随 Codex。");
                if(levels.Count==0)entry["supported_reasoning_levels"]=new[]{J.Map("effort",effort,"description","用户指定的默认强度，需中转支持。")};
                entry["default_reasoning_level"]=effort;
            }
            return entry;
        }).ToArray();
        return J.Map("relay_catalog_version",SchemaVersion,"models",entries);
    }
    internal static string Write(string directory,object provider,string configDir=null) {
        return WriteDocument(directory,Create(provider,configDir));
    }
    static string Digest(string content){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-","").ToLowerInvariant();}
    internal static bool LinkedPath(string path) {
        for(string current=Path.GetFullPath(path);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)return true;
        return false;
    }
    internal static object ReadManaged(string directory,string path) {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path))return null;
        string root=Path.GetFullPath(Path.Combine(directory,"model-catalogs")),full=Path.GetFullPath(path);
        if(!string.Equals(Path.GetDirectoryName(full),root,StringComparison.OrdinalIgnoreCase)||!File.Exists(full)||new FileInfo(full).Length>16*1024*1024)return null;
        if(LinkedPath(full))return null;
        string content=File.ReadAllText(full);
        if(!string.Equals(Path.GetFileName(full),Digest(content)+".json",StringComparison.OrdinalIgnoreCase))return null;
        try{return J.Decode(content);}catch{return null;}
    }
    internal static string Upgrade(string directory,object document,string protocol,string configDir=null,string activeModel=null) {
        if(protocol!="responses"||(J.Num(document,"relay_catalog_version")??0)>=SchemaVersion)return "";
        var entries=J.Arr(J.Get(document,"models"));if(entries.Count==0)return "";
        var upgraded=J.Obj(J.Clone(document));
        var native=NativeModelCatalog.Read(configDir);
        upgraded["models"]=entries.Select((entry,index)=>{
            var value=Entry(J.Str(entry,"slug"),index,native);
            string effort=J.Str(entry,"default_reasoning_level");
            if(effort!=""&&(activeModel==null||J.Str(entry,"slug")==activeModel)){
                var levels=J.Arr(J.Get(value,"supported_reasoning_levels"));
                if(levels.Count==0){value["supported_reasoning_levels"]=new[]{J.Map("effort",effort,"description","原配置指定的强度，需中转支持。")};value["default_reasoning_level"]=effort;}
                else if(levels.Any(level=>J.Str(level,"effort")==effort))value["default_reasoning_level"]=effort;
            }
            // Keep independently supplied budgets, while removing the old
            // synthetic 272k/95% and the discontinued 500k preset.
            bool ordinary=J.Num(entry,"context_window")==272000&&J.Num(entry,"effective_context_window_percent")==95;
            bool preset=J.Str(entry,"display_name")=="6.1 Sol · 500k"&&J.Num(entry,"context_window")==500000&&J.Num(entry,"effective_context_window_percent")==100;
            if(!ordinary&&!preset&&J.Num(entry,"context_window")>0){value["context_window"]=J.Get(entry,"context_window");if(J.Num(entry,"effective_context_window_percent")>0)value["effective_context_window_percent"]=J.Get(entry,"effective_context_window_percent");}
            return value;
        }).ToArray();
        upgraded["relay_catalog_version"]=SchemaVersion;
        return WriteDocument(directory,upgraded);
    }
    static string WriteDocument(string directory,object document) {
        string content=J.Encode(document),digest=Digest(content);
        string path=Path.GetFullPath(Path.Combine(directory,"model-catalogs",digest+".json"));
        if(LinkedPath(path))throw J.Error("模型目录包含链接，请先检查应用数据目录。");
        if(File.Exists(path)) {
            if(new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)||File.ReadAllText(path)!=content)throw J.Error("已保存的模型目录校验失败，请检查应用数据目录。");
        }else J.Atomic(path,content);
        return path;
    }
}
}
