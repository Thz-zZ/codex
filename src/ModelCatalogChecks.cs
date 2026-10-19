using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RelayLite {
internal static class ModelCatalogChecks {
    static void Check(bool condition,string message){if(!condition)throw J.Error("Model catalog: "+message);}
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
        Check(J.Str(entry[0],"default_reasoning_level")=="high"&&J.Arr(J.Get(entry[0],"supported_reasoning_levels")).Count==1,"catalog advertises the saved fixed effort rather than other selectable strengths");
        Check(!File.ReadAllText(path).Contains("private-catalog-fixture")&&!File.ReadAllText(path).Contains("catalog.example"),"catalog contains no relay address or credential");
        Check(J.True(J.Get(J.Get(document,"features"),"keep"))&&File.ReadAllText(external)=="{\"models\":[]}","unrelated settings and the user's previous catalog file are untouched");
        Check(ModelCatalog.Write(directory,input)==path,"identical model metadata reuses an immutable catalog snapshot");
        input["id"]=id;input["model"]="gpt-codex-mini-fixture";input["reasoning"]="default";store.Upsert(input,true);store.Switch(id);
        var next=Nett.Toml.ReadString(File.ReadAllText(file)).ToDictionary();string nextPath=J.Str(next,"model_catalog_json");var nextEntry=J.Arr(J.Get(J.Read(nextPath,null),"models"))[0];
        Check(nextPath!=path&&J.Get(nextEntry,"default_reasoning_level")==null&&J.Arr(J.Get(nextEntry,"supported_reasoning_levels")).Count==0,"service default does not invent a reasoning level and switching creates a separate snapshot");
        Check(J.Str(J.Arr(J.Get(J.Read(path,null),"models"))[0],"display_name")=="gpt-codex-fixture","switching does not overwrite catalogs referenced by older backups");
        string backup=J.Str(J.Arr(J.Get(store.List(),"history"))[0],"id");store.Restore(backup);Check(File.ReadAllText(file)==first&&File.Exists(path),"restoring a backup restores its exact model catalog and config");
        backup=J.Str(J.Arr(J.Get(store.List(),"history")).First(item=>J.Str(item,"action")=="switch"&&J.Str(item,"id")!=backup),"id");store.Restore(backup);
        Check(File.ReadAllText(file)==original,"restoring the original user catalog is exact");
        foreach(string model in new[]{"","bad\nmodel",new string('a',257)}) {bool rejected=false;try{ModelCatalog.Create(J.Map("model",model));}catch{rejected=true;}Check(rejected,"invalid model names cannot enter a catalog");}
        var allModels=Enumerable.Range(0,250).Select(index=>"relay-model-"+index.ToString("D3")).ToArray();
        var catalog=J.Map("modelSelection","catalog","models",allModels,"model",allModels[249],"reasoning","default");
        var entries=J.Arr(J.Get(ModelCatalog.Create(catalog),"models"));
        Check(entries.Count==250&&J.Str(entries[0],"slug")==allModels[249]&&entries.Select(item=>J.Str(item,"slug")).OrderBy(value=>value).SequenceEqual(allModels),"every fetched model is catalogued, not only the 200-item UI page, with the chosen default first");
        checks.Add("complete multi-model catalogs and default priority without truncating the saved list");
        checks.Add("selected model display catalog, fixed effort, credential isolation, immutable snapshots and exact user-catalog restore");
    }
}
}
