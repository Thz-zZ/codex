using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RelayLite {
internal static class ModelCatalog {
    internal static object Create(object provider) {
        string model=J.Str(provider,"model").Trim(),effort=J.Str(provider,"reasoning");
        if(model==""||model.Length>256||model.Any(char.IsControl))throw J.Error("模型目录需要有效的完整模型 ID（最多 256 个字符）。");
        bool specified=effort!=""&&effort!="default";
        if(specified&&!new[]{"low","medium","high","xhigh","max","ultra"}.Contains(effort))throw J.Error("模型目录包含不支持的推理强度。");
        bool catalog=J.Str(provider,"modelSelection")=="catalog";
        var models=catalog?Models.Saved(provider).OrderBy(id=>id==model?0:1).ThenBy(id=>id,StringComparer.Ordinal).ToList():new System.Collections.Generic.List<string>{model};
        var entries=models.Select((id,index)=>J.Map("slug",id,"display_name",id,"description",catalog?"中转提供的模型；可在 Codex 中直接切换，默认模型与强度在 Codex Relay 中设置。":"当前已应用的中转模型；切换模型和强度请在 Codex Relay 中操作。",
            "default_reasoning_level",specified?(object)effort:null,"supported_reasoning_levels",specified?new[]{J.Map("effort",effort,"description","中转固定使用 "+effort+"；修改请回中转设置。")}:new object[0],
            "shell_type","shell_command","visibility","list","supported_in_api",true,"priority",index,"support_verbosity",false,
            "truncation_policy",J.Map("mode","tokens","limit",10000),"experimental_supported_tools",new object[0],
            "base_instructions","You are a coding agent working with the user on their computer. Follow the conversation instructions and applicable AGENTS.md files. Inspect the workspace, make focused changes, preserve unrelated user work, use the available tools to verify results, and report accurately.",
            "supports_reasoning_summaries",true,"default_reasoning_summary","none","apply_patch_tool_type","freeform","supports_parallel_tool_calls",true,
            "input_modalities",new[]{"text","image"},"supports_search_tool",false)).ToArray();
        return J.Map("models",entries);
    }
    internal static string Write(string directory,object provider) {
        string content=J.Encode(Create(provider)),digest;
        using(var hash=SHA256.Create())digest=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-","").ToLowerInvariant();
        string path=Path.GetFullPath(Path.Combine(directory,"model-catalogs",digest+".json"));
        if(File.Exists(path)) {
            if(new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)||File.ReadAllText(path)!=content)throw J.Error("已保存的模型目录校验失败，请检查应用数据目录。");
        }else J.Atomic(path,content);
        return path;
    }
}
}
