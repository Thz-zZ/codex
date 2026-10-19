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
            var catalogInput=J.Map("name","all model fixture","baseUrl",J.Str(input,"baseUrl"),"key",J.Str(input,"key"),"modelFamily","codex","protocol","responses","model",models[1],"reasoning","default","discoveryToken",J.Str(result,"discoveryToken"));
            var allProvider=store.Upsert(catalogInput,true,true);string allId=J.Str(allProvider,"id");
            Check(J.Str(allProvider,"modelSelection")=="catalog"&&J.Arr(J.Get(allProvider,"models")).Select(J.S).SequenceEqual(models),"backend saves the full verified discovery list");
            catalogInput["id"]=allId;catalogInput.Remove("discoveryToken");catalogInput["key"]="";catalogInput["model"]=models[0];
            store=new Store(dir,config,new Secure());store.Upsert(catalogInput,true,true);store.Switch(allId);
            Check(J.Str(store.ConfigInfo(),"model")==models[0]&&J.Arr(J.Get(store.Details(allId),"models")).Count==2,"saved discovery survives restart and changing only the default requires no HTTP request");
            var importStore=new Store(Path.Combine(root,"import-draft-store"),config,new Secure());var imported=importStore.Import();Check(importStore.JCount()==0&&J.Str(imported,"model")==models[0]&&J.Str(imported,"reasoning")=="","importing existing config opens an unsaved draft rather than bypassing discovery and explicit selections");
            string unchanged=File.ReadAllText(store.File);
            foreach(var invalid in new[]{J.Map("id",allId,"key","different-key"),J.Map("id",allId,"baseUrl","https://changed.example"),J.Map("id",allId,"protocol","chat-completions"),J.Map("id",allId,"model","unlisted-model"),J.Map("id","","models",models,"modelSelection","catalog")}){
                var changed=J.Obj(J.Clone(catalogInput));foreach(var pair in invalid)changed[pair.Key]=pair.Value;if(J.Str(changed,"id")=="")changed["key"]="fixture-new-key";
                bool failed=false;try{store.Upsert(changed,true,true);}catch{failed=true;}Check(failed&&File.ReadAllText(store.File)==unchanged,"stale credentials, protocol, endpoint, unlisted defaults and invented model lists cannot bypass verified discovery");
            }
            catalogInput["discoveryToken"]=J.Str(result,"discoveryToken");catalogInput["key"]="wrong-token-key";bool stale=false;try{store.Upsert(catalogInput,true,true);}catch{stale=true;}Check(stale&&File.ReadAllText(store.File)==unchanged,"discovery proof is bound to credentials");
            Usage.FetchOverride=(url,key,user)=>Task.FromResult<object>(J.Map("data",new object[0]));bool empty=false;try{await Models.List(input);}catch(Exception error){empty=error.Message.Contains("未返回可用模型");}Check(empty,"empty discovery cannot be saved as a complete catalog");
            Usage.FetchOverride=(url,key,user)=>Task.FromResult<object>(J.Map("data",J.Map("id","not-a-list")));bool rejected=false;try{await Models.List(input);}catch(Exception e){rejected=e.Message.Contains("模型列表");}Check(rejected,"malformed list explained");
            Usage.FetchOverride=(url,key,user)=>{throw J.Error("HTTP 401 fixture-model-secret");};try{await Models.List(input);throw J.Error("expected rejection");}catch(Exception e){Check(e.Message.Contains("API 密钥无效")&&!e.Message.Contains("fixture-model-secret"),"invalid-key error is actionable and redacted");}
            bool fetched=false;Usage.FetchOverride=(url,key,user)=>{fetched=true;throw J.Error("unexpected HTTP");};input["key"]="";rejected=false;try{await Models.List(input);}catch{rejected=true;}Check(rejected&&!fetched,"missing API key rejected before HTTP");
            input["key"]="fixture-model-secret";input["protocol"]="responses";input["baseUrl"]="https://pages.example/proxy/v1";int pages=0;
            Usage.FetchOverride=(url,key,user)=>{pages++;Check(url=="https://pages.example/proxy/v1/models"+(pages==1?"":"?after=page-1"),"pagination stays on the authenticated Responses model endpoint");return Task.FromResult<object>(J.Map("data",new[]{J.Map("id","page-"+pages)},"has_more",pages==1,"last_id","page-1"));};
            Check(J.Arr(J.Get(await Models.List(input),"models")).Count==2&&pages==2,"paginated Codex relay model list is collected in full");
            Usage.FetchOverride=(url,key,user)=>Task.FromResult<object>(J.Map("data",new[]{J.Map("id","same-page")},"has_more",true,"last_id","same-page"));bool loop=false;try{await Models.List(input);}catch(Exception error){loop=error.Message.Contains("分页");}Check(loop,"looping pagination is rejected instead of saving an incomplete list");
            var familyInput=J.Map("name","Codex relay","baseUrl","https://shared-relay.example/v1","key","fixture-family-key","accountKey","fixture-family-account","adapter","sub2api","model","relay-aliased-model","modelFamily","codex","protocol","responses");
            var familyProvider=store.Upsert(familyInput);string familyId=J.Str(familyProvider,"id");
            store=new Store(dir,config,new Secure());var familyDetails=store.Details(familyId);
            Check(J.Str(familyDetails,"modelFamily")=="codex"&&J.Str(familyDetails,"protocol")=="responses"&&J.Str(familyDetails,"adapter")=="sub2api"&&J.Str(familyDetails,"baseUrl")=="https://shared-relay.example","Codex type persists with a custom relay host and independent statistics adapter");
            familyInput["id"]=familyId;familyInput.Remove("modelFamily");familyInput.Remove("protocol");familyInput["key"]="";familyInput["accountKey"]="";store.Upsert(familyInput);familyDetails=store.Details(familyId);
            Check(J.Str(familyDetails,"modelFamily")=="codex"&&J.Str(familyDetails,"protocol")=="responses"&&J.Str(familyDetails,"key")=="fixture-family-key"&&J.Str(familyDetails,"accountKey")=="fixture-family-account","single-product defaults preserve encrypted relay credentials");
            familyInput["modelFamily"]="invalid-family";rejected=false;try{store.Upsert(familyInput);}catch{rejected=true;}Check(rejected&&J.Str(store.Details(familyId),"modelFamily")=="codex","unsupported model family is rejected without changing a relay");
            checks.Add("relay model discovery, credential routing, malformed and unauthorized responses, selected default save and apply");
            checks.Add("verified full model catalogs, required default membership, pagination, credential binding and saved-list editing after restart");
            checks.Add("Codex-only defaults with custom relay hosts, independent statistics and preserved encrypted credentials");
        }finally{Usage.FetchOverride=null;}
    }
}
}
