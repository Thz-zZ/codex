using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace RelayLite {
// Only small list summaries are persisted, never credentials or request payloads.
internal static class UsageCache {
    static string FileName(Store store){return Path.Combine(store.Dir,"usage-cache.json");}
    static List<object> Read(Store store){try{return J.Arr(J.Read(FileName(store),null));}catch{return new List<object>();}}
    internal static List<object> Load(Store store,DateTime? now=null){
        var range=Usage.Range("today",now);var providers=J.Arr(J.Get(store.Data,"providers"));var result=new List<object>();
        foreach(var item in Read(store)){
            if(!providers.Any(p=>J.Str(p,"id")==J.Str(item,"id")&&J.Str(p,"revision")==J.Str(item,"revision")))continue;
            var value=J.Obj(item);
            if(J.Str(J.Get(value,"range"),"end")!=J.Str(range,"end")){
                value["totals"]=null;value["usageAt"]=null;value["checkedAt"]=null;value["usageError"]="";value["range"]=range;
            }
            result.Add(value);
        }
        return result;
    }
    internal static object Put(Store store,object query){
        if(J.Str(query,"period")!="today"||J.Str(query,"model")!="all")return null;
        var values=Load(store);string id=J.Str(query,"id");var previous=values.FirstOrDefault(x=>J.Str(x,"id")==id);
        var usage=J.Get(query,"usage");var balance=J.Get(query,"balance");bool hasBalance=J.Num(balance,"value").HasValue;
        var value=J.Map("id",id,"revision",J.Str(query,"revision"),"period","today","model","all","range",J.Get(query,"range"),"at",J.Str(query,"at"),"checkedAt",J.Str(query,"at"),"adapter",J.Get(query,"adapter"),
            "balance",hasBalance?balance:J.Get(previous,"balance",balance),"balanceAt",hasBalance?J.Get(query,"at"):J.Get(previous,"balanceAt"),"balanceError",hasBalance?"":J.Str(balance,"message"),
            "totals",usage!=null?J.Get(usage,"totals"):J.Get(previous,"totals"),"usageAt",usage!=null?J.Get(query,"at"):J.Get(previous,"usageAt"),"usageError",J.Str(query,"usageError"),"costNote",usage!=null?J.Str(usage,"costNote"):J.Str(previous,"costNote"));
        values.RemoveAll(x=>J.Str(x,"id")==id);values.Add(value);J.Atomic(FileName(store),J.Encode(values));return value;
    }
}
}
