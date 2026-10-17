using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace RelayLite {
internal static class ConnectionTest {
    internal static async Task<object> Run(object input){
        var checks=new List<object>();string key=J.Str(input,"key").Trim(),account=J.Str(input,"accountKey").Trim(),baseUrl="",site="";
        Func<string,string,string,string,object> row=(field,label,status,message)=>J.Map("field",field,"label",label,"status",status,"message",message);
        Func<Exception,string,string> explain=(error,field)=>{
            string text=Usage.ErrorText(error,key,account);
            if(text.Contains("HTTP 401"))return (field=="accountKey"?"账户访问令牌无效或已过期。":"API 密钥无效、已过期，或与当前中转不匹配。")+text;
            if(text.Contains("HTTP 403"))return "访问被拒绝，请检查令牌权限、用户 ID 或站点访问限制。"+text;
            if(text.Contains("HTTP 404"))return "接口路径不存在，请检查地址或接口类型。"+text;
            if(text.Contains("HTTP 429"))return "请求过于频繁，请稍后重试。"+text;
            return text;
        };
        try{baseUrl=Store.BaseUrl(J.Str(input,"baseUrl"));}catch(Exception e){checks.Add(row("baseUrl","API 地址","error",Usage.ErrorText(e)));return J.Map("checks",checks);}
        if(key==""||key.Length>4096||key.IndexOfAny(new[]{'\r','\n'})>=0){checks.Add(row("apiKey","API 密钥","error","请填写有效的 API 调用密钥。"));return J.Map("checks",checks);}
        key=key.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?key.Substring(7).Trim():key;
        try{
            var models=await Usage.GetForTest(baseUrl+"/v1/models",key);
            if(!(J.Get(models,"data") is System.Collections.IList))throw J.Error("返回格式不是模型列表，请检查 API 地址是否填写为网站首页或登录页面。");
            checks.Add(row("apiKey","API 地址与密钥","success","连接成功，API 密钥验证通过（读取模型列表）。"));
        }catch(Exception e){string message=explain(e,"apiKey"),text=Usage.ErrorText(e);bool unsupported=text.Contains("HTTP 404")||text.Contains("HTTP 405");checks.Add(row(text.Contains("HTTP 401")?"apiKey":"baseUrl","API 地址与密钥",unsupported?"warning":"error",unsupported?"该站点未开放模型列表接口，无法通过此接口验证模型调用权限。":message));}
        if(!J.True(J.Get(input,"includeAccount")))return J.Map("checks",checks);
        try{site=J.Str(input,"siteUrl").Trim();site=site==""?baseUrl:Store.BaseUrl(site);}catch(Exception e){checks.Add(row("siteUrl","统计网站地址","error",Usage.ErrorText(e)));return J.Map("checks",checks);}
        string choice=J.Str(input,"adapter","auto"),detected=null;var reasons=new List<string>();bool stopped=false;
        if(choice!="auto"&&choice!="newapi"&&choice!="sub2api"){checks.Add(row("adapter","接口类型","error","请选择自动识别、NewAPI 或 Sub2API。"));return J.Map("checks",checks);}
        foreach(var type in choice=="auto"?new[]{"newapi","sub2api"}:new[]{choice}){
            try{
                var payload=await Usage.GetForTest(site+(type=="newapi"?"/api/log/token":"/v1/usage"),key);
                if(!Usage.ValidTestPayload(type,payload))throw J.Error("接口返回格式与 "+type+" 不符。");detected=type;break;
            }catch(Exception e){reasons.Add((type=="newapi"?"NewAPI：":"Sub2API：")+explain(e,"apiKey"));if(!Usage.CanProbeAnotherAdapter(e)){stopped=true;break;}}
        }
        if(detected==null&&!stopped&&choice!="sub2api")try{if(Usage.ValidTokenPayload(await Usage.GetForTest(site+"/api/usage/token",key)))detected="newapi";}catch(Exception e){reasons.Add(explain(e,"apiKey"));}
        checks.Add(row("siteUrl","统计接口",detected==null?"error":"success",detected==null?string.Join("；",reasons):"已连接 "+(detected=="newapi"?"NewAPI":"Sub2API")+"，统计密钥验证通过。"));
        if(account!=""&&account.Length<=16384&&account.IndexOfAny(new[]{'\r','\n'})<0){
            account=account.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?account.Substring(7).Trim():account;
            if(detected==null)checks.Add(row("accountKey","账户访问令牌","warning","统计接口未通过，请先检查统计网站地址与接口类型。"));
            else if(detected=="newapi"&&!System.Text.RegularExpressions.Regex.IsMatch(J.Str(input,"accountUserId"),@"^\d+$"))checks.Add(row("accountUserId","NewAPI 用户 ID","error","填写账户令牌后，还需要填写纯数字的 NewAPI 用户 ID。"));
            else try{
                var user=Usage.Unwrap(await Usage.GetForTest(site+(detected=="newapi"?"/api/user/self":"/api/v1/auth/me"),account,detected=="newapi"?J.Str(input,"accountUserId"):""));
                if(!J.Num(user,detected=="newapi"?"quota":"balance").HasValue)throw J.Error("账户接口未返回预期字段，请检查账户令牌或站点是否支持该接口。");
                checks.Add(row("accountKey","账户查询配置","success","账户令牌与查询配置验证通过。"));
            }catch(Exception e){checks.Add(row("accountKey","账户查询配置","error",explain(e,"accountKey")));}
        }else checks.Add(row("accountKey","账户访问令牌",account==""?"skipped":"error",account==""?"未填写，跳过账户令牌验证。":"账户访问令牌格式无效。"));
        return J.Map("checks",checks,"adapter",detected,"label",detected==null?"":detected=="newapi"?"NewAPI":"Sub2API");
    }
}
}
