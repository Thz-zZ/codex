using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace RelayLite {
internal static class ConnectionChecks {
    static void Check(bool ok,string label){if(!ok)throw J.Error("Connection test: "+label);}
    internal static async Task Run(List<string> checks){
        var input=J.Map("baseUrl","https://model.example/v1","siteUrl","https://stats.example","key","fake-connection-api","accountKey","fake-connection-account","accountUserId","123","adapter","newapi","includeAccount",true);var paths=new List<string>();
        try{
            Usage.FetchOverride=(url,key,user)=>{
                paths.Add(url);
                if(url=="https://model.example/v1/models"){Check(key=="fake-connection-api","API credential routing");return Task.FromResult<object>(J.Map("data",new object[0]));}
                Check(url=="https://stats.example/api/user/self"&&key=="fake-connection-account"&&user=="123","account credential routing");return Task.FromResult<object>(J.Map("data",J.Map("quota",0)));
            };
            var result=await ConnectionTest.Run(input);Check(J.Arr(J.Get(result,"checks")).All(x=>J.Str(x,"status")=="success")&&J.Str(result,"adapter")=="newapi","successful NewAPI staged checks including zero quota");
            Check(!J.Has(result,"balance")&&!J.Has(result,"usage")&&paths.Count==2,"test returns validation only, without fetching balance conversion or saving data");
            input["includeAccount"]=false;paths.Clear();await ConnectionTest.Run(input);Check(paths.Count==1&&paths[0].Contains("model.example"),"disabled account queries are never checked");
            input["includeAccount"]=true;input["accountUserId"]="oops";paths.Clear();result=await ConnectionTest.Run(input);Check(J.Arr(J.Get(result,"checks")).Any(x=>J.Str(x,"field")=="accountUserId"&&J.Str(x,"status")=="error")&&!paths.Any(x=>x.Contains("/api/user/self")),"invalid account ID is identified without sending account token");
            input["accountUserId"]="123";Usage.FetchOverride=(url,key,user)=>{throw J.Error("HTTP 401：invalid fake-connection-api fake-connection-account");};result=await ConnectionTest.Run(input);Check(J.Arr(J.Get(result,"checks")).Any(x=>J.Str(x,"field")=="apiKey"&&J.Str(x,"status")=="error")&&!J.Encode(result).Contains("fake-connection"),"401 identifies API key and redacts all credentials");
            input["adapter"]="sub2api";Usage.FetchOverride=(url,key,user)=>Task.FromResult<object>(url.EndsWith("/v1/models")?J.Map("data",new object[0]):J.Map("balance",0,"mode","unrestricted"));result=await ConnectionTest.Run(input);Check(J.Str(result,"adapter")=="sub2api"&&J.Arr(J.Get(result,"checks")).All(x=>J.Str(x,"status")=="success"),"Sub2API and account validation");
            input["adapter"]="none";paths.Clear();Usage.FetchOverride=(url,key,user)=>{paths.Add(url);return Task.FromResult<object>(J.Map("data",new object[0]));};await ConnectionTest.Run(input);Check(paths.Count==1&&paths[0].EndsWith("/v1/models"),"disabled balance makes no balance requests even when includeAccount is true");
            input["adapter"]="newapi";input["accountKey"]="";paths.Clear();Usage.FetchOverride=(url,key,user)=>{paths.Add(url);if(url.EndsWith("/v1/models"))return Task.FromResult<object>(J.Map("data",new object[0]));throw new Usage.HttpFailure(404,"unsupported");};result=await ConnectionTest.Run(input);Check(paths.Count==2&&paths[1].EndsWith("/api/usage/token")&&!paths.Any(x=>x.EndsWith("/v1/usage")),"manual connection test never auto-switches the selected balance type");
            input["baseUrl"]="invalid://test";bool fetched=false;Usage.FetchOverride=(url,key,user)=>{fetched=true;throw J.Error("unexpected");};result=await ConnectionTest.Run(input);Check(!fetched&&J.Str(J.Arr(J.Get(result,"checks"))[0],"field")=="baseUrl","invalid address diagnosed before network");
            checks.Add("manual staged connection checks, expanded sections only, correct credential routing and field errors");
        }finally{Usage.FetchOverride=null;}
    }
}
}
