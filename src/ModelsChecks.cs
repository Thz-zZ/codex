using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
namespace RelayLite {
internal static class ModelsChecks {
    static void Check(bool ok,string label){if(!ok)throw J.Error("Model list: "+label);}
    internal static async Task Run(string root,List<string> checks){
        var input=J.Map("baseUrl","https://models.example/proxy/v1/","key","Bearer fixture-model-secret","siteUrl","https://stats.example","accountKey","unused-account");
        try{
            Usage.FetchOverride=(url,key,user)=>{Check(url=="https://models.example/proxy/v1/models"&&key=="fixture-model-secret"&&user=="","only model API receives API credentials");return Task.FromResult<object>(J.Map("data",new object[]{J.Map("id","gpt-fixture-mini"),J.Map("id","gpt-fixture"),J.Map("id","gpt-fixture-mini"),J.Map("object","invalid"),J.Map("id","bad\nmodel")}));};
            var result=await Models.List(input);var models=J.Arr(J.Get(result,"models")).Select(J.S).ToList();Check(models.SequenceEqual(new[]{"gpt-fixture","gpt-fixture-mini"}),"model IDs normalized, filtered and deduplicated");
            string config=Path.Combine(root,"models-config"),dir=Path.Combine(root,"models-store");Directory.CreateDirectory(config);File.WriteAllText(Path.Combine(config,"config.toml"),"model = \"original-model\"\n");
            var store=new Store(dir,config,new Secure());var provider=store.Upsert(J.Map("name","models fixture","baseUrl","https://models.example/v1","key","fixture-model-key","model",models[1]));string id=J.Str(provider,"id");
            store=new Store(dir,config,new Secure());Check(J.Str(store.Details(id),"model")==models[1],"chosen default survives restart");store.Switch(id);Check(J.Str(store.ConfigInfo(),"model")==models[1],"applying provider writes selected default");
            Usage.FetchOverride=(url,key,user)=>Task.FromResult<object>(J.Map("data",new object[0]));Check(J.Arr(J.Get(await Models.List(input),"models")).Count==0,"empty list supported");
            Usage.FetchOverride=(url,key,user)=>Task.FromResult<object>(J.Map("data",J.Map("id","not-a-list")));bool rejected=false;try{await Models.List(input);}catch(Exception e){rejected=e.Message.Contains("模型列表");}Check(rejected,"malformed list explained");
            Usage.FetchOverride=(url,key,user)=>{throw J.Error("HTTP 401 fixture-model-secret");};try{await Models.List(input);throw J.Error("expected rejection");}catch(Exception e){Check(e.Message.Contains("API 密钥无效")&&!e.Message.Contains("fixture-model-secret"),"invalid-key error is actionable and redacted");}
            bool fetched=false;Usage.FetchOverride=(url,key,user)=>{fetched=true;throw J.Error("unexpected HTTP");};input["key"]="";rejected=false;try{await Models.List(input);}catch{rejected=true;}Check(rejected&&!fetched,"missing API key rejected before HTTP");
            checks.Add("relay model discovery, credential routing, malformed and unauthorized responses, selected default save and apply");
        }finally{Usage.FetchOverride=null;}
    }
}
}
