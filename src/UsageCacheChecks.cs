using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace RelayLite {
internal static class UsageCacheChecks {
    static void Check(bool value,string label){if(!value)throw J.Error("Usage cache: "+label);}
    internal static void Run(string root,List<string> checks){
        string config=Path.Combine(root,"cache-config"),dir=Path.Combine(root,"cache-store");Directory.CreateDirectory(config);
        var store=new Store(dir,config,new Secure());var p=store.Upsert(J.Map("name","cache fixture","baseUrl","https://cache.example/v1","key","fake-cache-key-only","adapter","newapi"));string id=J.Str(p,"id");
        var query=J.Map("id",id,"revision",J.Str(p,"revision"),"period","today","model","all","range",Usage.Range("today"),"at",J.Now(),"adapter","newapi","usage",J.Map("totals",J.Map("cost",1.25,"tokenRate",0.8),"logs",new[]{J.Map("unused","do not persist")}),"balance",J.Map("value",0),"usageError","");
        UsageCache.Put(store,query);store=new Store(dir,config,new Secure());var cached=J.Arr(J.Get(store.List(),"usageCache")).Single();
        Check(J.Num(J.Get(cached,"balance"),"value")==0&&J.Num(J.Get(cached,"totals"),"tokenRate")==0.8,"restart restores zero balance and totals");
        Check(!File.ReadAllText(Path.Combine(dir,"usage-cache.json")).Contains("do not persist"),"only compact summaries persisted");
        query["usage"]=null;query["balance"]=J.Map("value",null,"message","offline");query["usageError"]="offline";UsageCache.Put(store,query);cached=UsageCache.Load(store).Single();
        Check(J.Num(J.Get(cached,"balance"),"value")==0&&J.Num(J.Get(cached,"totals"),"cost")==1.25&&J.Str(cached,"usageError")=="offline","failed refresh retains last good values");
        var tomorrow=UsageCache.Load(store,DateTime.UtcNow.AddDays(1)).Single();Check(J.Get(tomorrow,"totals")==null&&J.Get(tomorrow,"checkedAt")==null&&J.Num(J.Get(tomorrow,"balance"),"value")==0,"midnight expires daily totals but keeps dated balance");
        query["period"]="7";Check(UsageCache.Put(store,query)==null&&J.Num(J.Get(UsageCache.Load(store).Single(),"totals"),"cost")==1.25,"historical queries leave today cache untouched");
        File.WriteAllText(Path.Combine(config,"config.toml"),"model_provider = \"custom\"\n[model_providers.custom]\nbase_url = \"https://cache.example/v1\"\nexperimental_bearer_token = \"fake-cache-key-only\"\n");
        Check(J.True(J.Get(J.Arr(J.Get(store.List(),"providers")).Single(),"active")),"active detection normalizes v1 suffix");
        store.Upsert(J.Map("id",id,"name","changed credentials","baseUrl","https://cache.example/v1","key","fake-new-cache-key","adapter","newapi"));Check(UsageCache.Load(store).Count==0,"editing credentials invalidates cache");
        J.Atomic(Path.Combine(dir,"usage-cache.json"),"broken");Check(UsageCache.Load(store).Count==0,"corrupt cache does not prevent startup");
        checks.Add("persistent compact usage cache, failure retention, midnight, credential invalidation and active URL normalization");
    }
}
}
