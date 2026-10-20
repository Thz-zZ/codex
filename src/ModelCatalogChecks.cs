using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RelayLite {
internal static class ModelCatalogChecks {
    static void Check(bool condition,string message){if(!condition)throw J.Error("Model catalog: "+message);}
    internal static void RunLinked(string root,List<string> checks) {
        string real=Path.Combine(root,"real"),linked=Path.Combine(root,"linked"),config=Path.Combine(real,"config");
        Directory.CreateDirectory(config);
        string content=J.Encode(J.Map("models",new[]{J.Map("slug","linked-model")})),digest;
        using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-","").ToLowerInvariant();
        string realPath=Path.Combine(real,"model-catalogs",digest+".json"),linkedPath=Path.Combine(linked,"model-catalogs",digest+".json");
        J.Atomic(realPath,content);
        Check(ModelCatalog.LinkedPath(linkedPath)&&ModelCatalog.ReadManaged(linked,linkedPath)==null,"junction ancestors cannot make a catalog managed");
        string original="model_catalog_json = "+J.Encode(linkedPath)+"\n",file=Path.Combine(config,"config.toml");J.Atomic(file,original);
        var store=new Store(linked,config,new Secure());
        Check(!store.UpgradeManagedModelCatalog()&&File.ReadAllText(file)==original&&!Directory.Exists(Path.Combine(real,"backups")),"linked catalog directories are skipped without writes");
        original="model_catalog_json = "+J.Encode(realPath)+"\n";J.Atomic(file,original);
        store=new Store(real,Path.Combine(linked,"config"),new Secure());
        Check(!store.UpgradeManagedModelCatalog()&&File.ReadAllText(file)==original&&!Directory.Exists(Path.Combine(real,"backups")),"linked config directories are skipped without writes");
        bool blocked=false;try{ModelCatalog.Write(linked,J.Map("model","linked-model"));}catch{blocked=true;}
        Check(blocked&&File.ReadAllText(realPath)==content,"catalog creation never follows a linked directory");
        checks.Add("directory-junction isolation for managed catalogs, config upgrades and catalog writes");
    }
    internal static void Run(string root,List<string> checks) {
        string directory=Path.Combine(root,"catalog 目录"),config=Path.Combine(directory,"config");Directory.CreateDirectory(config);
        string external=Path.Combine(directory,"external.json"),file=Path.Combine(config,"config.toml");J.Atomic(external,"{\"models\":[]}");
        string original="# keep catalog setting\nmodel = \"old-model\"\nmodel_catalog_json = "+J.Encode(external)+"\n[features]\nkeep = true\n";File.WriteAllText(file,original);
        var store=new Store(directory,config,new Secure());
        var input=J.Map("name","catalog fixture","baseUrl","https://catalog.example/v1","key","private-catalog-fixture","modelFamily","codex","model","gpt-codex-fixture","reasoning","high","protocol","responses");
        string id=J.Str(store.Upsert(input,true),"id");store.Switch(id);
        string first=File.ReadAllText(file);var document=Nett.Toml.ReadString(first).ToDictionary();string path=J.Str(document,"model_catalog_json");var entry=J.Arr(J.Get(J.Read(path,null),"models"));
        Check(Path.IsPathRooted(path)&&File.Exists(path)&&entry.Count==1,"apply writes an absolute local catalog path with one selected model");
        Check(J.Str(entry[0],"slug")=="gpt-codex-fixture"&&J.Str(entry[0],"display_name")=="gpt-codex-fixture","model identity and display name match the applied relay exactly");
        Check(J.Str(entry[0],"default_reasoning_level")=="high"&&J.Arr(J.Get(entry[0],"supported_reasoning_levels")).Select(level=>J.Str(level,"effort")).SequenceEqual(new[]{"low","medium","high","xhigh","max","ultra"}),"Responses catalog keeps the configured default without locking any Codex effort choices");
        Check(J.Num(entry[0],"context_window")==272000&&J.Num(entry[0],"effective_context_window_percent")==95,"catalog includes a usable Codex context budgeting window");
        Check(!File.ReadAllText(path).Contains("private-catalog-fixture")&&!File.ReadAllText(path).Contains("catalog.example"),"catalog contains no relay address or credential");
        Check(J.True(J.Get(J.Get(document,"features"),"keep"))&&File.ReadAllText(external)=="{\"models\":[]}","unrelated settings and the user's previous catalog file are untouched");
        Check(ModelCatalog.Write(directory,input)==path,"identical model metadata reuses an immutable catalog snapshot");
        input["id"]=id;input["model"]="gpt-codex-mini-fixture";input["reasoning"]="default";store.Upsert(input,true);store.Switch(id);
        var next=Nett.Toml.ReadString(File.ReadAllText(file)).ToDictionary();string nextPath=J.Str(next,"model_catalog_json");var nextEntry=J.Arr(J.Get(J.Read(nextPath,null),"models"))[0];
        Check(nextPath!=path&&J.Get(nextEntry,"default_reasoning_level")==null&&J.Arr(J.Get(nextEntry,"supported_reasoning_levels")).Count==6,"Responses service default leaves the default unset but keeps all Codex effort choices and creates a separate snapshot");
        Check(!J.Has(next,"model_reasoning_effort"),"service default still clears inherited config effort rather than pinning a value");
        Check(J.Str(J.Arr(J.Get(J.Read(path,null),"models"))[0],"display_name")=="gpt-codex-fixture","switching does not overwrite catalogs referenced by older backups");
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
        foreach(string protocol in new[]{"chat-completions","anthropic"}){
            bool rejected=false;try{ModelCatalog.Create(J.Map("protocol",protocol,"model","relay-fixture"));}catch{rejected=true;}
            Check(rejected,"removed protocols cannot create direct model catalogs");
        }
        checks.Add("complete multi-model catalogs and default priority without truncating the saved list");
        checks.Add("selected model display catalog, selectable effort with optional default, credential isolation, immutable snapshots and exact user-catalog restore");
        SolContextChecks(root,checks);
        UpgradeChecks(root,checks);
    }
    static void SolContextChecks(string root,List<string> checks) {
        string directory=Path.Combine(root,"sol-context"),config=Path.Combine(directory,"config");Directory.CreateDirectory(config);
        var input=J.Map("name","Sol 500k","baseUrl","https://sol.example/v1","key","fixture-sol-key","model",ModelCatalog.SolModel,"contextPreset",ModelCatalog.SolContextPreset,"modelSelection","catalog","models",new[]{ModelCatalog.SolModel,"gpt-5.6-sol"},"modelMetadata",J.Map(ModelCatalog.SolModel,J.Map("context_window",1000000)));
        var store=new Store(directory,config,new Secure());string id=J.Str(store.Upsert(input,true),"id");
        store=new Store(directory,config,new Secure());Check(J.Str(store.Details(id),"contextPreset")==ModelCatalog.SolContextPreset&&!J.Has(store.Details(id),"modelMetadata"),"explicit Sol context choice survives restart and old fetched metadata is removed");
        store.Switch(id);
        var document=Nett.Toml.ReadString(File.ReadAllText(Path.Combine(config,"config.toml"))).ToDictionary();var entries=J.Arr(J.Get(J.Read(J.Str(document,"model_catalog_json"),null),"models"));
        Check(J.Str(document,"model")==ModelCatalog.SolModel&&!J.Has(document,"model_context_window"),"Sol 500k keeps the real upstream model ID and does not set a global context override");
        Check(entries.Count==2&&J.Str(entries[0],"slug")==ModelCatalog.SolModel&&J.Str(entries[0],"display_name")==ModelCatalog.SolContextLabel&&J.Num(entries[0],"context_window")==500000&&J.Num(entries[0],"effective_context_window_percent")==100,"explicit Sol choice produces a named 500k usable-context preset");
        Check(J.Num(entries[1],"context_window")==272000&&J.Num(entries[1],"effective_context_window_percent")==95,"the Sol preset leaves every other model's budget unchanged");
        input["id"]=id;input["contextPreset"]="default";store.Upsert(input,true);store.Switch(id);
        document=Nett.Toml.ReadString(File.ReadAllText(Path.Combine(config,"config.toml"))).ToDictionary();entries=J.Arr(J.Get(J.Read(J.Str(document,"model_catalog_json"),null),"models"));
        Check(!J.Has(store.Details(id),"contextPreset")&&J.Str(entries[0],"display_name")==ModelCatalog.SolModel&&J.Num(entries[0],"context_window")==272000&&J.Num(entries[0],"effective_context_window_percent")==95,"choosing the normal model restores its original context budget");
        string before=File.ReadAllText(store.File);
        foreach(var invalid in new[]{J.Map("model","gpt-5.6-sol","contextPreset",ModelCatalog.SolContextPreset),J.Map("model",ModelCatalog.SolModel,"contextPreset","unknown")}){
            var changed=J.Obj(J.Clone(input));foreach(var pair in invalid)changed[pair.Key]=pair.Value;
            bool rejected=false;try{store.Upsert(changed,true);}catch{rejected=true;}Check(rejected&&File.ReadAllText(store.File)==before,"unsupported models and invented context presets cannot change saved settings");
        }
        checks.Add("opt-in Sol 500k preset, exact label, original upstream ID, independent model budgets, persistence and ordinary-choice restore");
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
        Check(store.CatalogUpgraded&&J.True(J.Get(store.List(),"catalogUpgraded")),"startup upgrade status is exposed to the UI");
        var pending=store.Details(J.Str(J.Arr(J.Get(store.List(),"providers"))[0],"id"));
        Check(J.Str(pending,"model")=="unapplied-model"&&J.Str(pending,"reasoning")=="medium"&&J.Str(pending,"key")=="private-upgrade-fixture","startup preserves pending provider edits and encrypted credentials");
        string backup=J.Str(J.Arr(J.Get(store.List(),"history"))[0],"id");store.Restore(backup);Check(File.ReadAllText(file)==original,"catalog upgrade has an exact encrypted config backup");
        var backupDocument=J.Read(Path.Combine(directory,"backups",backup+".json"),null);
        Check(J.Str(backupDocument,"contentEncrypted").StartsWith("dpapi1:")&&!J.Encode(backupDocument).Contains(original),"upgrade backup is encrypted, not plaintext");
        string providerHeader="\r\n[model_providers.legacy]\r\nbase_url = \"https://legacy.example/v1\"\r\nwire_api = \"chat\"\r\n";
        string legacyConfig="model_provider = \"legacy\"\r\n"+original+providerHeader;
        J.Atomic(file,legacyConfig);
        int backups=Directory.GetFiles(Path.Combine(directory,"backups")).Length;
        Check(!store.UpgradeManagedModelCatalog()&&File.ReadAllText(file)==legacyConfig&&Directory.GetFiles(Path.Combine(directory,"backups")).Length==backups,"non-Responses applied configs are skipped before writes");
        legacyConfig=legacyConfig.Replace("https://legacy.example/v1","http://127.0.0.1:12345/relay/legacy/revision/v1").Replace("wire_api = \"chat\"","wire_api = \"responses\"");
        J.Atomic(file,legacyConfig);
        Check(!store.UpgradeManagedModelCatalog()&&File.ReadAllText(file)==legacyConfig&&Directory.GetFiles(Path.Combine(directory,"backups")).Length==backups,"old bridge endpoints are not upgraded or silently converted");
        J.Atomic(file,original);
        File.AppendAllText(oldPath," ");Check(!store.UpgradeManagedModelCatalog()&&File.ReadAllText(file)==original,"edited hash snapshots cannot be silently overwritten");
        string external=Path.Combine(directory,"external.json");J.Atomic(external,content);J.Atomic(file,original.Replace(J.Encode(oldPath),J.Encode(external)));
        Check(!store.UpgradeManagedModelCatalog()&&File.ReadAllText(external)==content,"external user catalogs are never automatically upgraded");
        Check(ModelCatalog.Upgrade(directory,legacy,"anthropic")==""&&ModelCatalog.Upgrade(directory,J.Map("models",new object[0]),"responses")=="","removed protocols and empty catalogs cannot be upgraded");
        Check(ModelCatalog.Upgrade(directory,J.Map("relay_catalog_version",99,"models",new[]{J.Map("slug","future-model")}),"responses")=="","newer catalog schemas are not downgraded");
        string malformed="{invalid-json",malformedHash;using(var sha=SHA256.Create())malformedHash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(malformed))).Replace("-","").ToLowerInvariant();
        string malformedPath=Path.Combine(directory,"model-catalogs",malformedHash+".json");J.Atomic(malformedPath,malformed);
        J.Atomic(file,original.Replace(J.Encode(oldPath),J.Encode(malformedPath)));
        string invalidConfig=File.ReadAllText(file);
        Check(!store.UpgradeManagedModelCatalog()&&File.ReadAllText(file)==invalidConfig,"malformed owned JSON is ignored without blocking startup or rewriting config");
        checks.Add("owned old catalog startup upgrade, complete efforts and context budgets, unchanged pending edits, immutable snapshots, exact backups, tamper and external-file isolation");
    }
}
}
