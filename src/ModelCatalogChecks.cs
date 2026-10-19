using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RelayLite {
internal static class ModelCatalogChecks {
    static void Check(bool condition,string message){if(!condition)throw J.Error("Model catalog: "+message);}
    internal static void Run(string root,List<string> checks) {
        string directory=Path.Combine(root,"catalog 目录"),config=Path.Combine(directory,"config");Directory.CreateDirectory(config);
        string external=Path.Combine(directory,"external.json"),file=Path.Combine(config,"config.toml");J.Atomic(external,"{\"models\":[]}");
        string original="# keep catalog setting\nmodel = \"old-model\"\nmodel_catalog_json = "+J.Encode(external)+"\n[features]\nkeep = true\n";File.WriteAllText(file,original);
        var store=new Store(directory,config,new Secure());
        var input=J.Map("name","catalog fixture","baseUrl","https://catalog.example/v1","key","private-catalog-fixture","modelFamily","claude","model","claude-opus-5-5","reasoning","high","protocol","responses");
        string id=J.Str(store.Upsert(input,true),"id");store.Switch(id);
        string first=File.ReadAllText(file);var document=Nett.Toml.ReadString(first).ToDictionary();string path=J.Str(document,"model_catalog_json");var entry=J.Arr(J.Get(J.Read(path,null),"models"));
        Check(Path.IsPathRooted(path)&&File.Exists(path)&&entry.Count==1,"apply writes an absolute local catalog path with one selected model");
        Check(J.Str(entry[0],"slug")=="claude-opus-5-5"&&J.Str(entry[0],"display_name")=="claude-opus-5-5","model identity and display name match the applied relay exactly");
        Check(J.Str(entry[0],"default_reasoning_level")=="high"&&J.Arr(J.Get(entry[0],"supported_reasoning_levels")).Select(level=>J.Str(level,"effort")).SequenceEqual(new[]{"low","medium","high","xhigh","max","ultra"}),"Responses catalog keeps the configured default without locking any Codex effort choices");
        Check(J.Num(entry[0],"context_window")==272000&&J.Num(entry[0],"effective_context_window_percent")==95,"catalog includes a usable Codex context budgeting window");
        Check(!File.ReadAllText(path).Contains("private-catalog-fixture")&&!File.ReadAllText(path).Contains("catalog.example"),"catalog contains no relay address or credential");
        Check(J.True(J.Get(J.Get(document,"features"),"keep"))&&File.ReadAllText(external)=="{\"models\":[]}","unrelated settings and the user's previous catalog file are untouched");
        Check(ModelCatalog.Write(directory,input)==path,"identical model metadata reuses an immutable catalog snapshot");
        input["id"]=id;input["model"]="deepseek-fixture";input["reasoning"]="default";store.Upsert(input,true);store.Switch(id);
        var next=Nett.Toml.ReadString(File.ReadAllText(file)).ToDictionary();string nextPath=J.Str(next,"model_catalog_json");var nextEntry=J.Arr(J.Get(J.Read(nextPath,null),"models"))[0];
        Check(nextPath!=path&&J.Get(nextEntry,"default_reasoning_level")==null&&J.Arr(J.Get(nextEntry,"supported_reasoning_levels")).Count==6,"Responses service default leaves the default unset but keeps all Codex effort choices and creates a separate snapshot");
        Check(!J.Has(next,"model_reasoning_effort"),"service default still clears inherited config effort rather than pinning a value");
        Check(J.Str(J.Arr(J.Get(J.Read(path,null),"models"))[0],"display_name")=="claude-opus-5-5","switching does not overwrite catalogs referenced by older backups");
        string backup=J.Str(J.Arr(J.Get(store.List(),"history"))[0],"id");store.Restore(backup);Check(File.ReadAllText(file)==first&&File.Exists(path),"restoring a backup restores its exact model catalog and config");
        backup=J.Str(J.Arr(J.Get(store.List(),"history")).First(item=>J.Str(item,"action")=="switch"&&J.Str(item,"id")!=backup),"id");store.Restore(backup);
        Check(File.ReadAllText(file)==original,"restoring the original user catalog is exact");
        foreach(string model in new[]{"","bad\nmodel",new string('a',257)}) {bool rejected=false;try{ModelCatalog.Create(J.Map("model",model));}catch{rejected=true;}Check(rejected,"invalid model names cannot enter a catalog");}
        var allModels=Enumerable.Range(0,250).Select(index=>"relay-model-"+index.ToString("D3")).ToArray();
        var catalog=J.Map("modelSelection","catalog","models",allModels,"model",allModels[249],"reasoning","default");
        var entries=J.Arr(J.Get(ModelCatalog.Create(catalog),"models"));
        Check(entries.Count==250&&J.Str(entries[0],"slug")==allModels[249]&&entries.Select(item=>J.Str(item,"slug")).OrderBy(value=>value).SequenceEqual(allModels),"every fetched model is catalogued, not only the 200-item UI page, with the chosen default first");
        Check(entries.All(item=>J.Arr(J.Get(item,"supported_reasoning_levels")).Count==6),"all fetched Responses models retain every Codex effort choice");
        foreach(string effort in new[]{"max","ultra"}) {
            var selected=J.Arr(J.Get(ModelCatalog.Create(J.Map("model","relay-fixture","reasoning",effort)),"models"))[0];
            Check(J.Str(selected,"default_reasoning_level")==effort&&J.Arr(J.Get(selected,"supported_reasoning_levels")).Select(level=>J.Str(level,"effort")).SequenceEqual(new[]{"low","medium","high","xhigh","max","ultra"}),"saved max and ultra defaults preserve every other Responses effort choice");
        }
        foreach(string protocol in new[]{"chat-completions","anthropic"})foreach(string effort in new[]{"default","high"}) {
            var selected=J.Arr(J.Get(ModelCatalog.Create(J.Map("protocol",protocol,"model","relay-fixture","reasoning",effort)),"models"))[0];
            Check(J.Arr(J.Get(selected,"supported_reasoning_levels")).Select(level=>J.Str(level,"effort")).SequenceEqual(new[]{"low","medium","high"}),"bridged catalogs keep all supported choices regardless of the configured default");
        }
        checks.Add("complete multi-model catalogs and default priority without truncating the saved list");
        checks.Add("selected model display catalog, selectable effort with optional default, credential isolation, immutable snapshots and exact user-catalog restore");
        UpgradeChecks(root,checks);
    }
    static void UpgradeChecks(string root,List<string> checks) {
        string directory=Path.Combine(root,"catalog-upgrade"),config=Path.Combine(directory,"config");Directory.CreateDirectory(config);
        var legacy=J.Map("models",new[]{J.Map("slug","applied-model","display_name","applied-model","default_reasoning_level","high","supported_reasoning_levels",new object[0]),J.Map("slug","other-model","context_window",128000,"effective_context_window_percent",90)});
        string content=J.Encode(legacy),digest;using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-","").ToLowerInvariant();
        string oldPath=Path.Combine(directory,"model-catalogs",digest+".json"),file=Path.Combine(config,"config.toml");J.Atomic(oldPath,content);
        string original="# keep comments\r\nmodel = \"applied-model\"\r\nmodel_reasoning_effort = \"low\"\r\nmodel_context_window = 200000\r\nmodel_catalog_json  = "+J.Encode(oldPath)+" # keep inline comment\r\n[features]\r\nkeep = true\r\n";J.Atomic(file,original);
        var store=new Store(directory,config,new Secure());store.Upsert(J.Map("name","unapplied edits","baseUrl","https://upgrade.example/v1","key","private-upgrade-fixture","modelFamily","codex","protocol","responses","model","unapplied-model","reasoning","medium"),true);
        Check(store.UpgradeManagedModelCatalog(),"startup upgrades an old owned catalog without reapplying saved provider edits");
        string next=File.ReadAllText(file),nextPath=J.Str(Nett.Toml.ReadString(next).ToDictionary(),"model_catalog_json");var entries=J.Arr(J.Get(J.Read(nextPath,null),"models"));
        Check(next==original.Replace(J.Encode(oldPath),J.Encode(nextPath)),"automatic upgrade changes only the catalog path, preserving model, effort, context overrides, comments and line endings");
        Check(J.Str(entries[0],"slug")=="applied-model"&&J.Str(entries[0],"default_reasoning_level")=="high"&&J.Arr(J.Get(entries[0],"supported_reasoning_levels")).Count==6,"upgrade preserves the applied model catalog and its defaults, not pending provider edits");
        Check(J.Num(entries[0],"context_window")==272000&&J.Num(entries[1],"context_window")==128000&&J.Num(entries[1],"effective_context_window_percent")==90,"upgrade fills missing context metadata and preserves existing per-model budgets");
        Check(File.ReadAllText(oldPath)==content&&!store.UpgradeManagedModelCatalog(),"old snapshots stay immutable and a second startup does not rewrite the config");
        string backup=J.Str(J.Arr(J.Get(store.List(),"history"))[0],"id");store.Restore(backup);Check(File.ReadAllText(file)==original,"catalog upgrade has an exact encrypted config backup");
        File.AppendAllText(oldPath," ");Check(!store.UpgradeManagedModelCatalog()&&File.ReadAllText(file)==original,"edited hash snapshots cannot be silently overwritten");
        string external=Path.Combine(directory,"external.json");J.Atomic(external,content);J.Atomic(file,original.Replace(J.Encode(oldPath),J.Encode(external)));
        Check(!store.UpgradeManagedModelCatalog()&&File.ReadAllText(external)==content,"external user catalogs are never automatically upgraded");
        checks.Add("owned old catalog startup upgrade, complete efforts and context budgets, unchanged pending edits, immutable snapshots, exact backups, tamper and external-file isolation");
    }
}
}
