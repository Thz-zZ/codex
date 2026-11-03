using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace RelayLite {
internal static class ConnectionTest {
    internal static async Task<object> Run(object input){
        ModelProtocol.Protocol(input);
        ModelProtocol.ValidateAddress(J.Str(input,"baseUrl"));
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
        key=ModelProtocol.NormalizeKey(key);
        try{
            string modelRoot=ModelProtocol.ModelRoot(input);
            var models=await Usage.GetForTest(modelRoot+"/models",key,"",ModelProtocol.Protocol(input));
            if(!(J.Get(models,"data") is System.Collections.IList))throw J.Error("返回格式不是模型列表，请检查 API 地址是否填写为网站首页或登录页面。");
            checks.Add(row("apiKey","API 地址与密钥","success","模型列表读取成功；实际生成与工具调用需应用后在 Codex 中验证。"));
        }catch(Exception e){string message=explain(e,"apiKey"),text=Usage.ErrorText(e);bool unsupported=text.Contains("HTTP 404")||text.Contains("HTTP 405");checks.Add(row(text.Contains("HTTP 401")?"apiKey":"baseUrl","API 地址与密钥",unsupported?"warning":"error",unsupported?"该站点未开放模型列表接口，无法通过此接口验证模型调用权限。":message));}
        if(!J.True(J.Get(input,"includeAccount")))return J.Map("checks",checks);
        if(J.Str(input,"adapter")=="none"){checks.Add(row("adapter","统计接口","skipped","未启用余额与用量查询，不影响模型调用。"));return J.Map("checks",checks);}
        try{site=J.Str(input,"siteUrl").Trim();site=Store.SiteRoot(site==""?baseUrl:site);}catch(Exception e){checks.Add(row("siteUrl","统计网站地址","error",Usage.ErrorText(e)));return J.Map("checks",checks);}
        string choice=J.Str(input,"adapter","none");
        if(choice!="newapi"&&choice!="sub2api"){checks.Add(row("adapter","中转类型","error","请选择 NewAPI 或 Sub2API 中转类型。"));return J.Map("checks",checks);}
        account=ModelProtocol.NormalizeKey(account);
        if(account.Length>16384||account.IndexOfAny(new[]{'\r','\n'})>=0){checks.Add(row("accountKey","账户访问令牌","error","账户访问令牌格式无效。"));return J.Map("checks",checks);}
        if(choice=="newapi"&&account!=""&&!System.Text.RegularExpressions.Regex.IsMatch(J.Str(input,"accountUserId"),@"^\d+$")){checks.Add(row("accountUserId","NewAPI 用户 ID","error","填写账户令牌后，还需要填写纯数字的 NewAPI 用户 ID。"));return J.Map("checks",checks);}
        try{
            if(account!=""){
                var user=Usage.Unwrap(await Usage.GetForTest(site+(choice=="newapi"?"/api/user/self":"/api/v1/auth/me"),account,choice=="newapi"?J.Str(input,"accountUserId"):""));
                if(!J.Num(user,choice=="newapi"?"quota":"balance").HasValue)throw J.Error("账户接口未返回余额字段，请检查账户令牌或站点是否支持该接口。");
            }else{
                var payload=await Usage.GetForTest(site+(choice=="newapi"?"/api/usage/token":"/v1/usage"),key);
                if(choice=="newapi"?!Usage.ValidTokenPayload(payload):!Usage.ValidTestPayload(choice,payload))throw J.Error("余额接口返回格式与所选中转类型不符，请检查地址与中转类型。");
                if(choice=="newapi"&&!J.Num(J.Get(Usage.Unwrap(payload),"user_quota"),"total_available").HasValue)throw J.Error("该密钥未开放账户额度，请填写账户访问令牌和 NewAPI 用户 ID。");
            }
            checks.Add(row(account!=""?"accountKey":"siteUrl","余额查询","success","已连接 "+(choice=="newapi"?"NewAPI":"Sub2API")+"，余额查询配置验证通过。"));
        }catch(Exception e){checks.Add(row(account!=""?"accountKey":"siteUrl","余额查询","error",explain(e,account!=""?"accountKey":"apiKey")));}
        return J.Map("checks",checks,"adapter",choice,"label",choice=="newapi"?"NewAPI":"Sub2API");
    }
}
}
