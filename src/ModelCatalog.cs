using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RelayLite {
internal static class ModelCatalog {
    internal const int SchemaVersion=2;
    // Codex-compatible client budgeting defaults, not a claim about an upstream model's capacity.
    internal const int DefaultContextWindow=272000,DefaultContextPercent=95;
    internal const string SolModel="gpt-6.1-sol",SolContextPreset="sol-500k",SolContextLabel="6.1 Sol · 500k";
    internal const int SolContextWindow=500000;
    internal static bool UsesSolContext(object provider) {
        string preset=J.Str(provider,"contextPreset");
        if(preset==""||preset=="default")return false;
        if(preset!=SolContextPreset||!string.Equals(J.Str(provider,"model").Trim(),SolModel,StringComparison.OrdinalIgnoreCase))throw J.Error("500k 上下文预设仅支持 6.1 Sol，请重新选择默认模型。");
        return true;
    }
    static object ReasoningLevels(){return new[]{"low","medium","high","xhigh","max","ultra"}.Select(level=>J.Map("effort",level,"description","在 Codex 中选择 "+level+"；需中转和模型支持。")).ToArray();}
    internal static object Create(object provider) {
        ModelProtocol.Protocol(provider);
        string model=J.Str(provider,"model").Trim(),effort=J.Str(provider,"reasoning");
        if(model==""||model.Length>256||model.Any(char.IsControl))throw J.Error("模型目录需要有效的完整模型 ID（最多 256 个字符）。");
        bool specified=effort!=""&&effort!="default";
        if(specified&&!new[]{"low","medium","high","xhigh","max","ultra"}.Contains(effort))throw J.Error("模型目录包含不支持的推理强度。");
        bool catalog=J.Str(provider,"modelSelection")=="catalog";
        bool solContext=UsesSolContext(provider);
        var reasoningLevels=ReasoningLevels();
        var models=catalog?Models.Saved(provider).OrderBy(id=>id==model?0:1).ThenBy(id=>id,StringComparer.Ordinal).ToList():new System.Collections.Generic.List<string>{model};
        var entries=models.Select((id,index)=>J.Map("slug",id,"display_name",solContext&&id==model?SolContextLabel:id,"description",solContext&&id==model?"用户选择的 6.1 Sol 500k 上下文预设；实际容量需中转支持。可在 Codex Relay 中选择普通模型项恢复默认预算。":catalog?"中转提供的模型；可在 Codex 中切换模型和推理强度，Codex Relay 中的设置仅作默认。":"当前已应用的中转模型；可在 Codex 中调整推理强度，切换模型请在 Codex Relay 中操作。",
            "default_reasoning_level",specified?(object)effort:null,"supported_reasoning_levels",reasoningLevels,
            "context_window",solContext&&id==model?SolContextWindow:DefaultContextWindow,"effective_context_window_percent",solContext&&id==model?100:DefaultContextPercent,
            "shell_type","shell_command","visibility","list","supported_in_api",true,"priority",index,"support_verbosity",false,
            "truncation_policy",J.Map("mode","tokens","limit",10000),"experimental_supported_tools",new object[0],
            "base_instructions","You are a coding agent working with the user on their computer. Follow the conversation instructions and applicable AGENTS.md files. Inspect the workspace, make focused changes, preserve unrelated user work, use the available tools to verify results, and report accurately.",
            "supports_reasoning_summaries",true,"default_reasoning_summary","none","apply_patch_tool_type","freeform","supports_parallel_tool_calls",true,
            "input_modalities",new[]{"text","image"},"supports_search_tool",false)).ToArray();
        return J.Map("relay_catalog_version",SchemaVersion,"models",entries);
    }
    internal static string Write(string directory,object provider) {
        return WriteDocument(directory,Create(provider));
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
    internal static string Upgrade(string directory,object document,string protocol) {
        if(protocol!="responses"||(J.Num(document,"relay_catalog_version")??0)>=SchemaVersion)return "";
        var entries=J.Arr(J.Get(document,"models"));if(entries.Count==0)return "";
        var upgraded=J.Obj(J.Clone(document));
        upgraded["models"]=entries.Select(entry=>{
            var value=J.Obj(J.Clone(entry));value["supported_reasoning_levels"]=ReasoningLevels();
            if(!(J.Num(value,"context_window")>0))value["context_window"]=DefaultContextWindow;
            if(!(J.Num(value,"effective_context_window_percent")>0))value["effective_context_window_percent"]=DefaultContextPercent;
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
