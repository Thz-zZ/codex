using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
namespace RelayLite {
internal static class BalanceChecks {
    static void Check(bool value,string label){if(!value)throw J.Error("Balance refresh: "+label);}
    internal static async Task Run(string root,List<string> checks){
        var store=new Store(Path.Combine(root,"balance-store"),Path.Combine(root,"balance-config"),new Secure());
        var paths=new List<string>();
        Func<string,string,object> provider=(adapter,account)=>store.Upsert(J.Map("name","balance fixture","baseUrl","https://balance-"+adapter+".example/v1","key","fake-balance-secret","adapter",adapter,"accountKey",account,"accountUserId","42"));
        try{
            Usage.FetchOverride=(url,key,user)=>{
                var path=new Uri(url).AbsolutePath;paths.Add(path);
                if(path=="/api/status")return Task.FromResult<object>(J.Map("data",J.Map("quota_per_unit",500000)));
                if(path=="/api/usage/token")return Task.FromResult<object>(J.Map("data",J.Map("object","token_usage","user_quota",J.Map("total_available",0))));
                throw J.Error("Unexpected remote usage request: "+path);
            };
            var p=provider("newapi","");string id=J.Str(p,"id");
            var result=await Usage.InspectBalance(store,id,"7");
            Check(J.Num(J.Get(result,"balance"),"value")==0&&J.Get(result,"usage")==null&&!paths.Contains("/api/log/token"),"NewAPI balance never requests logs or invents usage");
            Check(J.Str(J.Get(J.Get(result,"summary"),"range"),"start")==J.Str(Usage.Range("today"),"start"),"historical selection still saves today's balance summary");
            paths.Clear();await Usage.InspectBalance(store,id,"today");Check(paths.SequenceEqual(new[]{"/api/usage/token"}),"conversion metadata is reused");
            Usage.FetchOverride=(url,key,user)=>{paths.Add(new Uri(url).AbsolutePath);throw new TaskCanceledException("fake-balance-secret");};
            result=await Usage.InspectBalance(store,id,"today");
            Check(J.Num(J.Get(J.Get(result,"summary"),"balance"),"value")==0&&!J.Encode(result).Contains("fake-balance-secret"),"failure keeps known zero balance and hides credentials");

            foreach(string adapter in new[]{"newapi","sub2api"})foreach(Exception failure in new Exception[]{new TaskCanceledException(),new Usage.HttpFailure(404,"unsupported"),new Usage.HttpFailure(401,"fake-balance-secret"),new Usage.HttpFailure(503,"unavailable")}){
                p=provider(adapter,"");id=J.Str(p,"id");paths.Clear();
                Usage.FetchOverride=(url,key,user)=>{paths.Add(new Uri(url).AbsolutePath);throw failure;};
                result=await Usage.InspectBalance(store,id,"today");
                Check(paths.Count==1&&paths[0]==(adapter=="newapi"?"/api/usage/token":"/v1/usage")&&J.Str(result,"adapter")==adapter&&!J.Encode(result).Contains("fake-balance-secret"),"selected type never probes the other type on failure");
            }
            p=provider("sub2api","fake-account-token");id=J.Str(p,"id");paths.Clear();
            Usage.FetchOverride=(url,key,user)=>{var path=new Uri(url).AbsolutePath;paths.Add(path);if(path!="/api/v1/auth/me"||key!="fake-account-token")throw J.Error("Unexpected usage fetch");return Task.FromResult<object>(J.Map("data",J.Map("balance",3)));};
            result=await Usage.InspectBalance(store,id,"today");
            Check(paths.Count==1&&J.Num(J.Get(result,"balance"),"value")==3,"Sub2API uses the independent account balance endpoint when available");
            paths.Clear();Usage.FetchOverride=(url,key,user)=>{var path=new Uri(url).AbsolutePath;paths.Add(path);if(path=="/api/v1/auth/me")throw new Usage.HttpFailure(401,"invalid account token");return Task.FromResult<object>(J.Map("mode","unrestricted","balance",4));};
            result=await Usage.InspectBalance(store,id,"today");
            Check(paths.SequenceEqual(new[]{"/api/v1/auth/me","/v1/usage"})&&J.Num(J.Get(result,"balance"),"value")==4,"account fallback is attempted once");
            p=provider("none","");paths.Clear();await Usage.InspectBalance(store,J.Str(p,"id"),"today");Check(paths.Count==0,"disabled balance performs no network requests");
            checks.Add("balance-only refresh: no remote logs, cached conversion, explicit types, account fallback, failure retention and redaction");
        }finally{Usage.FetchOverride=null;}
    }
}
}
