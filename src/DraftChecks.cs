using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
namespace RelayLite {
internal static class DraftChecks {
    static void Check(bool condition,string message){if(!condition)throw J.Error(message);}
    public static async Task Run(string root,List<string> checks){
        var store=new Store(Path.Combine(root,"draft-store"),Path.Combine(root,"draft-config"),new Secure());
        foreach(string site in new[]{"","   ","https://stats.example"}){
            var saved=store.Upsert(J.Map("name","draft","baseUrl","https://fixture.example/v1","siteUrl",site,"key","fixture-draft-key"));
            Check(J.Str(saved,"siteUrl")== (site.Trim()==""?"https://fixture.example":"https://stats.example"),"optional stats URL fallback");
        }
        checks.Add("saving blank and whitespace stats URL follows API URL");
        var selectedStore=new Store(Path.Combine(root,"selection-store"),Path.Combine(root,"selection-config"),new Secure());
        foreach(string protocol in new[]{"responses"}){
            var selection=J.Map("name","required selections","baseUrl","https://selection.example/v1","key","fixture-selection-key","modelFamily","codex","protocol",protocol,"model","selected-fixture","reasoning","default");
            foreach(string field in new[]{"model"})foreach(string missing in new[]{"","   "}){
                var invalid=J.Obj(J.Clone(selection));invalid[field]=missing;int count=selectedStore.JCount();bool rejected=false;try{selectedStore.Upsert(invalid,true);}catch(Exception error){rejected=error.Message.Contains(field=="model"?"模型":"推理强度");}
                Check(rejected&&selectedStore.JCount()==count,"Codex requires an explicit default model before saving");
                invalid.Remove(field);rejected=false;try{selectedStore.Upsert(invalid,true);}catch{rejected=true;}Check(rejected&&selectedStore.JCount()==count,"omitted selection cannot bypass validation");
            }
            foreach(string missing in new[]{"","   ",null}){
                var optional=J.Obj(J.Clone(selection));if(missing==null)optional.Remove("reasoning");else optional["reasoning"]=missing;
                Check(J.Str(selectedStore.Upsert(optional,true),"reasoning")=="default","blank, whitespace and omitted reasoning follow Codex");
            }
            foreach(string effort in new[]{"default","low","medium","high","xhigh","max","ultra","invalid"}){
                selection["reasoning"]=effort;bool allowed=effort!="invalid";
                string beforeSelection=File.Exists(selectedStore.File)?File.ReadAllText(selectedStore.File):"";bool rejected=false;object saved=null;try{saved=selectedStore.Upsert(selection,true);}catch{rejected=true;}
                Check(rejected!=allowed,"reasoning choices match the selected protocol");
                if(allowed)Check(J.Str(saved,"reasoning")==effort,"explicit effort and service default are persisted");
                else Check((File.Exists(selectedStore.File)?File.ReadAllText(selectedStore.File):"")==beforeSelection,"invalid effort does not mutate saved providers");
            }
        }
        var selected=J.Map("name","configured selections","baseUrl","https://selection.example/v1","key","fixture-selection-key","modelFamily","codex","protocol","responses","model"," selected-fixture ","reasoning","high");
        var selectedProvider=selectedStore.Upsert(selected,true);selected["id"]=J.Str(selectedProvider,"id");Directory.CreateDirectory(selectedStore.ConfigDir);
        File.WriteAllText(Path.Combine(selectedStore.ConfigDir,"config.toml"),"model = \"old-model\"\nmodel_reasoning_effort = \"ultra\"\n[features]\nkeep = true\n");
        selectedStore.Switch(J.Str(selected,"id"));Check(J.Str(selectedStore.ConfigInfo(),"model")=="selected-fixture"&&J.Str(selectedStore.ConfigInfo(),"reasoning")=="high","selected model and effort replace old direct settings");
        selected["reasoning"]="";Check(J.Str(selectedStore.Upsert(selected,true),"reasoning")=="default","editing with blank reasoning saves an optional default");selectedStore.Switch(J.Str(selected,"id"));
        var selectedConfig=Nett.Toml.ReadString(File.ReadAllText(Path.Combine(selectedStore.ConfigDir,"config.toml"))).ToDictionary();
        Check(!J.Has(selectedConfig,"model_reasoning_effort")&&J.True(J.Get(J.Get(selectedConfig,"features"),"keep")),"service default removes inherited effort without writing an invalid default effort or touching other settings");
        selectedStore=new Store(selectedStore.Dir,selectedStore.ConfigDir,new Secure());Check(J.Str(selectedStore.Details(J.Str(selected,"id")),"reasoning")=="default","explicit service default survives restart");
        checks.Add("mandatory Codex default model, optional reasoning, edit validation and invalid effort rejection");
        checks.Add("optional default persistence and direct config clearing without invalid reasoning values");
        string before=File.ReadAllText(store.File);var paths=new List<string>();var input=J.Map("baseUrl","fixture.example/v1","siteUrl","","key","fixture-draft-key","adapter","auto");
        try{
            Usage.FetchOverride=(url,key,user)=>{paths.Add(url);if(url.Contains("/api/log/token"))return Task.FromResult<object>(J.Map("success",true,"data",new object[0]));if(url.Contains("/api/usage/token"))return Task.FromResult<object>(J.Map("data",J.Map("user_quota",J.Map("total_available",0))));return Task.FromResult<object>(J.Map("data",J.Map("quota_per_unit",500000)));};
            var preview=await Usage.DetectAdapter(input);Check(J.Str(preview,"adapter")=="newapi"&&J.Num(J.Get(preview,"balance"),"value")==0,"draft NewAPI preview including zero balance");Check(paths[0]=="https://fixture.example/api/log/token","draft URL normalization");
            Usage.FetchOverride=(url,key,user)=>{if(url.Contains("/api/log/token"))throw J.Error("404");return Task.FromResult<object>(J.Map("mode","unrestricted","balance",1.25,"daily_usage",new object[0]));};
            preview=await Usage.DetectAdapter(input);Check(J.Str(preview,"adapter")=="sub2api"&&J.Num(J.Get(preview,"balance"),"value")==1.25,"draft Sub2API preview");
            Usage.FetchOverride=(url,key,user)=>{if(url.Contains("/api/log/token")||url.Contains("/v1/usage"))throw J.Error("404");return Task.FromResult<object>(J.Map("data",J.Map("object","token_usage","user_quota",J.Map("total_available",500000))));};
            preview=await Usage.DetectAdapter(input);Check(J.Str(preview,"adapter")=="newapi","draft NewAPI token fallback");
            Usage.FetchOverride=(url,key,user)=>{throw J.Error("denied fixture-draft-key");};preview=await Usage.DetectAdapter(input);Check(J.Get(preview,"adapter")==null&&!J.Encode(preview).Contains("fixture-draft-key"),"draft error redaction and unknown adapter");
            bool fetched=false;Usage.FetchOverride=(url,key,user)=>{fetched=true;throw J.Error("unexpected HTTP");};var invalid=J.Obj(J.Clone(input));invalid["baseUrl"]="https://user:password@fixture.example";bool rejected=false;try{await Usage.DetectAdapter(invalid);}catch{rejected=true;}Check(rejected&&!fetched,"invalid draft URL rejected before HTTP");
            Check(File.ReadAllText(store.File)==before,"draft queries must not persist providers or snapshots");
            checks.Add("unsaved NewAPI and Sub2API detection, balance, token fallback, redaction and no persistence");
        }finally{Usage.FetchOverride=null;}
    }
}
}
