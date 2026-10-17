using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RelayLite {
internal static class Usage {
    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp(){
        // The standalone compiler does not supply modern framework TLS defaults.
        // Keep certificate validation enabled and use the protocol supported by both relay backends.
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(12) };
    }
    internal static Func<string,string,string,Task<object>> FetchOverride;
    static string Root(object p) { return J.Str(p,"siteUrl",J.Str(p,"baseUrl")).TrimEnd('/'); }
    internal static object Unwrap(object value) { var data=J.Get(value,"data");return data is Dictionary<string,object>?data:value; }
    static string Day(DateTime now) { return now.ToUniversalTime().AddHours(8).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture); }
    internal static object Range(string period,DateTime? now=null) {
        string today=Day(now??DateTime.UtcNow);DateTime end=DateTime.ParseExact(today,"yyyy-MM-dd",CultureInfo.InvariantCulture),start=end;
        if(period=="yesterday") start=end=end.AddDays(-1);
        else if(period=="7") start=end.AddDays(-6);
        else if(period=="30") start=end.AddDays(-29);
        else if(period=="all") start=end.AddDays(-89);
        else if(period.StartsWith("range:",StringComparison.Ordinal)) {
            string[] parts=period.Split(':');
            if(parts.Length!=3||!DateTime.TryParseExact(parts[1],"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out start)||!DateTime.TryParseExact(parts[2],"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out end)||end<start||string.CompareOrdinal(parts[2],today)>0||(end-start).TotalDays>=90)throw J.Error("请选择有效日期，单次查询最多 90 天。");
        } else if(period!="today") throw J.Error("未知的时间范围。");
        return J.Map("period",period,"start",start.ToString("yyyy-MM-dd"),"end",end.ToString("yyyy-MM-dd"),"days",(int)(end-start).TotalDays+1,"timezone","Asia/Shanghai");
    }
    static string Endpoint(object p,string adapter,object range) {
        string root=Root(p);if(adapter=="newapi")return root+"/api/log/token";
        return root+"/v1/usage?start_date="+J.Str(range,"start")+"&end_date="+J.Str(range,"end")+"&days="+J.Str(range,"days")+"&timezone=Asia%2FShanghai";
    }
    static async Task<object> Get(string url,string key,string userId="") {
        if(FetchOverride!=null)return await FetchOverride(url,key,userId);
        using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12)))
        using(var request=new HttpRequestMessage(HttpMethod.Get,url)) {
            request.Headers.TryAddWithoutValidation("Accept","application/json");
            if(key!="")request.Headers.TryAddWithoutValidation("Authorization","Bearer "+key);
            if(userId!="")request.Headers.TryAddWithoutValidation("New-Api-User",userId);
            using(var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token)) {
                using(var stream=await response.Content.ReadAsStreamAsync())
                using(var buffer=new MemoryStream()) {
                    byte[] block=new byte[16384];int read;
                    while((read=await stream.ReadAsync(block,0,block.Length,timeout.Token))>0) {
                        if(buffer.Length+read>8*1024*1024)throw J.Error("接口返回数据过大。");
                        buffer.Write(block,0,read);
                    }
                    object data;try{data=J.Decode(Encoding.UTF8.GetString(buffer.ToArray()));}catch{throw J.Error("站点没有返回 JSON 接口。");}
                    var code=J.Get(data,"code");bool codeOK=code==null||code is bool&&(bool)code||new[]{"0","200"}.Contains(J.S(code));
                    if(!response.IsSuccessStatusCode||J.Get(data,"success") is bool&&!J.True(J.Get(data,"success"))||!codeOK)throw J.Error("HTTP "+(int)response.StatusCode+"："+J.Str(data,"message",J.Str(J.Get(data,"error"),"message","查询失败")));
                    return data;
                }
            }
        }
    }
    static List<object> Unpack(object payload) {
        object list=J.Get(payload,"data");if(!(list is System.Collections.IList))list=J.Get(list,"items");
        if(!J.True(J.Get(payload,"success"))||!(list is System.Collections.IList))throw J.Error("接口返回格式不同：未找到日志数组");
        return J.Arr(list).Where(x=>x is Dictionary<string,object>).ToList();
    }
    static bool IsSub(object payload) {
        var p=Unwrap(payload);return J.Get(p,"model_stats") is System.Collections.IList||J.Get(p,"daily_usage") is System.Collections.IList||J.Get(J.Get(p,"usage"),"today")!=null||J.Get(J.Get(p,"usage"),"total")!=null||new[]{"unrestricted","quota_limited"}.Contains(J.Str(p,"mode"))&&(J.Num(p,"balance").HasValue||J.Get(p,"quota") is Dictionary<string,object>);
    }
    static bool IsNewToken(object payload) {var p=Unwrap(payload);return J.Str(p,"object")=="token_usage"||J.Get(p,"unlimited_quota") is bool&&J.Num(p,"total_available").HasValue;}
    internal static Task<object> GetForTest(string url,string key,string user=""){return Get(url,key,user);}
    internal static bool ValidTestPayload(string type,object payload){if(type=="sub2api")return IsSub(payload);try{Unpack(payload);return true;}catch{return false;}}
    internal static bool ValidTokenPayload(object payload){return IsNewToken(payload);}
    public static async Task<object> DetectAdapter(object input) {
        string key=J.Str(input,"key").Trim().Replace("Bearer ","").Trim(),account=J.Str(input,"accountKey").Trim().Replace("Bearer ","").Trim();
        if(key=="")return J.Map("adapter",null,"message","请先填写 API 调用密钥。");
        if(key.Length>4096||account.Length>16384||(key+account).IndexOfAny(new[]{'\r','\n'})>=0)throw J.Error("密钥格式无效。");
        string baseUrl=Store.BaseUrl(J.Str(input,"baseUrl")),site=J.Str(input,"siteUrl").Trim();
        var p=J.Map("baseUrl",baseUrl,"siteUrl",site==""?baseUrl:Store.BaseUrl(site),"accountUserId",J.Str(input,"accountUserId"),"quotaPerUnit",J.N(J.Get(input,"quotaPerUnit"))??500000);
        var range=Range("today");var errors=new List<string>();string choice=J.Str(input,"adapter","auto"),detected=null;object payload=null;
        if(!new[]{"auto","newapi","sub2api"}.Contains(choice))throw J.Error("请选择有效的接口类型。");
        foreach(string type in choice=="auto"?new[]{"newapi","sub2api"}:new[]{choice}){
            try{var result=await Get(Endpoint(p,type,range),key);if(type=="newapi")Unpack(result);else if(!IsSub(result))throw J.Error("返回的数据不是 Sub2API 用量格式。");detected=type;payload=result;break;}
            catch(Exception ex){errors.Add((type=="newapi"?"NewAPI":"Sub2API")+"："+ErrorText(ex,key,account));}
        }
        if(detected==null&&choice!="sub2api")try{if(IsNewToken(await Get(Root(p)+"/api/usage/token",key)))detected="newapi";}catch{}
        if(detected==null)return J.Map("adapter",null,"message","暂时无法识别接口类型。"+string.Join("；",errors));
        object balance;try{balance=await AccountBalance(p,detected,key,account,payload);}catch(Exception ex){balance=J.Map("value",null,"message",ErrorText(ex,key,account));}
        return J.Map("adapter",detected,"label",detected=="newapi"?"NewAPI":"Sub2API","balance",balance);
    }
    internal static Dictionary<string,object> NormalizeLog(object log) {
        object other=J.Get(log,"other");if(other is string)try{other=J.Decode((string)other);}catch{other=null;}
        double? raw=J.Count(J.Get(log,"created_at"));string date="未记录日期",hour="未记录时间";double? timestamp=null;
        if(raw.HasValue&&raw<=253402271999d){timestamp=raw;var when=DateTimeOffset.FromUnixTimeSeconds((long)raw.Value).UtcDateTime.AddHours(8);date=when.ToString("yyyy-MM-dd");hour=when.ToString("yyyy-MM-dd HH:00");}
        var row=J.Map("model",J.Str(log,"model_name","未记录模型"),"timestamp",timestamp,"date",date,"hour",hour,"read",null,"write",null,"input",null,"rate",null,"valid",false,"reason","","hasCache",false);
        if(!J.Has(other,"cache_tokens")){row["reason"]="缺少缓存读取字段";return row;}
        var read=J.Count(J.Get(other,"cache_tokens"));if(!read.HasValue){row["reason"]="缓存读取数量无效";return row;}row["read"]=read;row["hasCache"]=true;
        string[] writeFields={"cache_write_tokens","cache_creation_tokens","cache_creation_tokens_5m","cache_creation_tokens_1h"};
        if(writeFields.Any(k=>J.Has(other,k)&&!J.Count(J.Get(other,k)).HasValue)){row["reason"]="缓存写入数量无效";return row;}
        double split=(J.Count(J.Get(other,"cache_creation_tokens_5m"))??0)+(J.Count(J.Get(other,"cache_creation_tokens_1h"))??0);
        double write=J.Has(other,"cache_write_tokens")?J.Count(J.Get(other,"cache_write_tokens")).Value:Math.Max(J.Count(J.Get(other,"cache_creation_tokens"))??0,split);row["write"]=write;
        string semantic=J.Str(other,"usage_semantic").ToLowerInvariant();double? total;
        if(J.Has(other,"input_tokens_total"))total=J.Count(J.Get(other,"input_tokens_total"));
        else if(J.Has(J.Get(other,"billing_tokens"),"len"))total=J.Count(J.Get(J.Get(other,"billing_tokens"),"len"));
        else {
            var prompt=J.Count(J.Get(log,"prompt_tokens"));if(!prompt.HasValue){row["reason"]="缺少有效输入数量";return row;}
            if(semantic!=""&&semantic!="openai"&&semantic!="anthropic"){row["reason"]="未知输入口径";return row;}
            bool separate=semantic=="anthropic"||semantic==""&&(J.True(J.Get(other,"claude"))||J.Num(other,"claude")==1);
            total=separate?prompt+read+write:prompt;
        }
        if(!total.HasValue||total<=0||total>9007199254740991d||write>9007199254740991d||read+write>total){row["reason"]="输入与缓存数量矛盾或输入为零";return row;}
        row["input"]=total;row["rate"]=read/total;row["valid"]=true;return row;
    }
    static double? Sum(IEnumerable<Dictionary<string,object>> rows,string field) {var values=rows.Select(r=>J.Num(r,field)).ToList();return values.Any(v=>!v.HasValue)?null:(double?)values.Sum(v=>v.Value);}
    static Dictionary<string,object> AggregateLogs(List<Dictionary<string,object>> rows) {
        var valid=rows.Where(r=>J.True(J.Get(r,"valid"))).ToList();double input=valid.Sum(r=>J.Num(r,"input").Value),read=valid.Sum(r=>J.Num(r,"read").Value),write=valid.Sum(r=>J.Num(r,"write").Value);
        var samples=rows.Where(r=>J.True(J.Get(r,"hasCache"))).ToList();int hits=samples.Count(r=>J.Num(r,"read")>0),missing=rows.Count-valid.Count;var output=Sum(rows,"output");
        return J.Map("input",input,"read",read,"write",write,"valid",valid.Count,"samples",samples.Count,"hits",hits,"missing",missing,"reasons",rows.Where(r=>!J.True(J.Get(r,"valid"))).GroupBy(r=>J.Str(r,"reason")).ToDictionary(g=>g.Key,g=>(object)g.Count()),"tokenRate",input>0?(double?)read/input:null,"requestRate",samples.Count>0?(double?)hits/samples.Count:null,"requests",rows.Count,"excluded",missing,"output",output,"cost",Sum(rows,"cost"),"standardCost",Sum(rows,"standardCost"),"plain",missing==0?(double?)input-read-write:null,"totalTokens",missing==0&&output.HasValue?input+output:null);
    }
    internal static object NewUsage(object payload,string period,string model,double divisor) {
        var range=Range(period);var raw=Unpack(payload).Where(r=>J.Num(r,"type")==2).ToList();var rows=new List<Dictionary<string,object>>();
        foreach(var log in raw){var row=NormalizeLog(log);row["output"]=J.Nonnegative(J.Get(log,"completion_tokens"));var quota=J.Nonnegative(J.Get(log,"quota"));row["cost"]=quota.HasValue?quota/divisor:null;row["standardCost"]=J.Nonnegative(J.Get(log,"standard_cost"));rows.Add(row);}
        var selected=rows.Where(r=>(model=="all"||J.Str(r,"model")==model)&&(period=="all"||J.Num(r,"timestamp").HasValue&&string.CompareOrdinal(J.Str(r,"date"),J.Str(range,"start"))>=0&&string.CompareOrdinal(J.Str(r,"date"),J.Str(range,"end"))<=0)).ToList();
        Func<string,List<object>> groups=field=>selected.GroupBy(r=>J.Str(r,field)).Select(g=>{var x=AggregateLogs(g.ToList());x["name"]=g.Key;return (object)x;}).ToList();
        return J.Map("mode","logs","totals",AggregateLogs(selected),"models",groups("model"),"daily",groups("date").OrderBy(r=>J.Str(r,"name")).ToList(),"hourly",groups("hour").Where(r=>J.Str(r,"name")!="未记录时间").OrderBy(r=>J.Str(r,"name")).ToList(),"logs",selected.OrderByDescending(r=>J.Num(r,"timestamp")??0).Take(300).Cast<object>().ToList(),"modelNames",rows.Select(r=>J.Str(r,"model")).Distinct().ToList(),"range",range,"note","近期日志快照；站点可能限制返回条数，时间筛选只作用于已返回的记录。","costNote","API 密钥消费 · 日志额度 ÷ "+divisor.ToString(CultureInfo.InvariantCulture)+"；仅统计返回记录，账户余额为当前值。");
    }
    internal static Dictionary<string,object> NormalizeSub(object value,string name) {
        var plain=J.Count(J.Get(value,"input_tokens"));var read=J.Count(J.Get(value,"cache_read_tokens"));var write=J.Count(J.Has(value,"cache_creation_tokens")?J.Get(value,"cache_creation_tokens"):J.Get(value,"cache_write_tokens"));var output=J.Count(J.Get(value,"output_tokens"));
        var row=J.Map("name",name,"requests",J.Count(J.Get(value,"requests")),"read",read,"write",write,"output",output,"cost",J.Nonnegative(J.Get(value,"actual_cost")),"standardCost",J.Nonnegative(J.Get(value,"total_cost",J.Get(value,"standard_cost"))),"input",null,"plain",plain,"valid",false,"reason","");
        if(!plain.HasValue||!read.HasValue||!write.HasValue){row["reason"]="缺少有效输入或缓存字段";return row;}
        double input=plain.Value+read.Value+write.Value;if(input>9007199254740991d){row["reason"]="Token 数量超出精确范围";return row;}
        if(J.Has(value,"total_tokens")&&J.Has(value,"output_tokens")){var full=J.Count(J.Get(value,"total_tokens"));if(!full.HasValue||!output.HasValue||full!=input+output){row["reason"]="总 Token 与分项数量不一致";return row;}}
        row["input"]=input;row["totalTokens"]=output.HasValue?(double?)input+output:null;row["tokenRate"]=input>0?read/input:null;row["valid"]=true;return row;
    }
    static Dictionary<string,object> AggregateSub(List<Dictionary<string,object>> groups) {
        var valid=groups.Where(r=>J.True(J.Get(r,"valid"))).ToList();double input=valid.Sum(r=>J.Num(r,"input").Value),read=valid.Sum(r=>J.Num(r,"read").Value),write=valid.Sum(r=>J.Num(r,"write").Value);int excluded=groups.Count-valid.Count;var output=Sum(groups,"output");
        return J.Map("input",input,"read",read,"write",write,"output",output,"plain",excluded==0?(double?)input-read-write:null,"totalTokens",excluded==0&&output.HasValue?input+output:null,"cost",Sum(groups,"cost"),"standardCost",Sum(groups,"standardCost"),"requests",Sum(groups,"requests"),"excluded",excluded,"tokenRate",input>0?(double?)read/input:null,"requestRate",null);
    }
    internal static object SubUsage(object payload,string period,string model) {
        var data=Unwrap(payload);var range=Range(period);
        var models=J.Arr(J.Get(data,"model_stats")).Select(r=>NormalizeSub(r,J.Str(r,"model","未记录模型"))).ToList();
        var daily=J.Arr(J.Get(data,"daily_usage")).Where(r=>Regex.IsMatch(J.Str(r,"date"),@"^\d{4}-\d{2}-\d{2}$")&&string.CompareOrdinal(J.Str(r,"date"),J.Str(range,"start"))>=0&&string.CompareOrdinal(J.Str(r,"date"),J.Str(range,"end"))<=0).Select(r=>NormalizeSub(r,J.Str(r,"date"))).ToList();
        var selected=models.Where(r=>model=="all"||J.Str(r,"name")==model).ToList();object totals;
        if(model!="all"||models.Count>0)totals=AggregateSub(selected);
        else if(daily.Count>0)totals=AggregateSub(daily);
        else if(period=="today"&&J.Get(J.Get(data,"usage"),"today")!=null)totals=AggregateSub(new List<Dictionary<string,object>>{NormalizeSub(J.Get(J.Get(data,"usage"),"today"),"")});
        else totals=J.Map("input",null,"read",null,"write",null,"requests",null,"excluded",0,"tokenRate",null,"requestRate",null);
        return J.Map("mode","summary","totals",totals,"models",selected.Cast<object>().ToList(),"daily",model=="all"?daily.Cast<object>().ToList():new List<object>(),"hourly",new List<object>(),"logs",new List<object>(),"modelNames",models.Select(r=>J.Str(r,"name")).ToList(),"range",range,"note","所选时间段的接口汇总，最多近 90 天；无逐条请求。"+(model=="all"?"":"接口不支持按模型拆分每日用量。"),"costNote","仅统计此 API 密钥的实际消费；账户余额为当前值。");
    }
    static async Task<object> AccountBalance(object p,string adapter,string key,string account,object usage) {
        string root=Root(p);
        if(adapter=="sub2api") {
            var value=J.Num(Unwrap(usage),"balance");if(value.HasValue)return J.Map("value",value,"unit","USD","label","账户余额","source","/v1/usage · balance");
            if(account!=""){var user=Unwrap(await Get(root+"/api/v1/auth/me",account));value=J.Num(user,"balance");if(value.HasValue)return J.Map("value",value,"unit","USD","label","账户余额","source","/api/v1/auth/me · balance");}
            return J.Map("value",null,"label","账户余额","message","接口未提供账户余额，可在编辑中填写账户访问令牌。");
        }
        double? quota;string source;
        if(account!="") {
            string userId=J.Str(p,"accountUserId");if(!Regex.IsMatch(userId,@"^\d+$"))return J.Map("value",null,"label","账户余额","message","请在编辑中填写 NewAPI 用户 ID。");
            quota=J.Num(Unwrap(await Get(root+"/api/user/self",account,userId)),"quota");source="/api/user/self · quota";
        } else {
            var data=Unwrap(await Get(root+"/api/usage/token",key));quota=J.Num(J.Get(data,"user_quota"),"total_available");source="/api/usage/token · user_quota";
            if(!quota.HasValue)return J.Map("value",null,"label","账户余额","message","该密钥未开放账户额度；请在编辑中填写账户访问令牌和用户 ID。");
        }
        if(!quota.HasValue)return J.Map("value",null,"label","账户余额","message","接口没有返回账户余额字段。");
        double divisor=J.Num(p,"quotaPerUnit")??500000;string conversion="按设置换算";
        try {var status=Unwrap(await Get(root+"/api/status",""));var unit=J.Num(status,"quota_per_unit");if(unit>0){divisor=unit.Value;conversion="按站点换算";}}catch{}
        return J.Map("value",quota/divisor,"unit","USD","label","账户余额","source",source+" · "+conversion+" 1 USD = "+divisor.ToString(CultureInfo.InvariantCulture)+" 额度","rawQuota",quota,"divisor",divisor);
    }
    public static async Task<object> Inspect(Store store,string id,string period,string model="all") {
        var range=Range(period);var p=J.Obj(J.Clone(store.Details(id)));string key=J.Str(p,"key"),account=J.Str(p,"accountKey"),choice=J.Str(p,"adapter","auto"),preferred=J.Str(p,"detectedAdapter","newapi");
        string[] types=choice=="auto"?new[]{preferred,preferred=="newapi"?"sub2api":"newapi"}:new[]{choice};string detected=null,error="";object payload=null,usage=null,balance=null;var errors=new List<string>();
        foreach(string type in types) {
            try {var result=await Get(Endpoint(p,type,range),key);if(type=="newapi")Unpack(result);else if(!IsSub(result))throw J.Error("返回的数据不是 Sub2API 用量格式。");payload=result;detected=type;break;}
            catch(Exception ex){errors.Add((type=="newapi"?"NewAPI":"Sub2API")+"："+ErrorText(ex,key,account));}
        }
        if(detected==null&&choice=="auto") {
            try {var token=await Get(Root(p)+"/api/usage/token",key);if(IsNewToken(token)){detected="newapi";error="已识别为 NewAPI，但近期日志查询失败："+string.Join("；",errors);}}catch{}
        }
        string effective=detected??(choice=="auto"?J.Str(p,"detectedAdapter"):choice);
        if(detected==null)error=choice=="auto"?"无法自动识别接口类型。"+string.Join("；",errors):string.Join("；",errors);
        try {balance=effective!=""?await AccountBalance(p,effective,key,account,payload):J.Map("value",null,"label","账户余额","message","尚未识别接口类型，请检查地址和密钥。");}
        catch(Exception ex){balance=J.Map("value",null,"label","账户余额","message",ErrorText(ex,key,account));}
        if(payload!=null)try {
            double divisor=J.Num(balance,"divisor")??J.Num(p,"quotaPerUnit")??500000;
            if(effective=="newapi"&&!J.Num(balance,"divisor").HasValue)try{var status=Unwrap(await Get(Root(p)+"/api/status",""));if(J.Num(status,"quota_per_unit")>0)divisor=J.Num(status,"quota_per_unit").Value;}catch{}
            usage=effective=="newapi"?NewUsage(payload,period,model,divisor):SubUsage(payload,period,model);
        }catch(Exception ex){error=ErrorText(ex,key,account);}
        if(choice=="auto"&&detected!=null)store.SetDetection(id,J.Str(p,"revision"),detected);
        store.RequireRevision(id,J.Str(p,"revision"));string at=J.Now();error=Sanitize(error,key,account);
        store.Record(id,J.Map("at",at,"period",period,"model",model,"adapter",effective==""?null:effective,"balance",balance,"totals",J.Get(usage,"totals"),"error",error));
        var inspection=J.Map("id",id,"at",at,"period",period,"model",model,"range",range,"revision",J.Str(p,"revision"),"adapter",effective==""?null:effective,"usage",usage,"usageError",error,"balance",balance,"history",store.Snapshots(id));
        inspection["summary"]=UsageCache.Put(store,inspection);return inspection;
    }
    internal static string ErrorText(Exception error,params string[] secrets){
        var messages=new List<string>();bool timeout=false,tls=false;
        for(var current=error;current!=null;current=current.InnerException){
            timeout|=current is OperationCanceledException;
            var web=current as WebException;tls|=web!=null&&(web.Status==WebExceptionStatus.SecureChannelFailure||web.Status==WebExceptionStatus.TrustFailure);
            if(!messages.Contains(current.Message))messages.Add(current.Message);
        }
        return Sanitize((timeout?"连接超时，请检查网络或代理。":tls?"HTTPS 安全连接失败，请检查系统时间、证书或代理。":"")+string.Join(" → ",messages),secrets);
    }
    public static string Sanitize(string value,params string[] secrets) {value=value??"查询失败";foreach(string secret in secrets)if(!string.IsNullOrEmpty(secret))value=value.Replace(secret,"[密钥已隐藏]");value=Regex.Replace(value,@"(?:sk-[\w-]{10,}|Bearer\s+[\w.\-]+)","[密钥已隐藏]");return value.Length>500?value.Substring(0,500):value;}
}
}
