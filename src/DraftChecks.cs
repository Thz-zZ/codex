using System;
using System.Collections.Generic;
using System.IO;
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
