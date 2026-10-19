using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
namespace RelayLite {
internal static class Models {
    sealed class Discovery {internal string Fingerprint;internal List<string> Models;internal DateTime Expires;}
    static readonly object sync=new object();
    static readonly Dictionary<string,Discovery> discoveries=new Dictionary<string,Discovery>();
    internal static string Fingerprint(object input,string key) {
        key=key.Trim();if(key.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))key=key.Substring(7).Trim();
        using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(J.Encode(new[]{ModelProtocol.ModelRoot(input),ModelProtocol.Protocol(input),key}))));
    }
    internal static List<string> Resolve(string token,object input,string key) {
        lock(sync){Discovery found;if(!discoveries.TryGetValue(token,out found)||found.Expires<=DateTime.UtcNow||found.Fingerprint!=Fingerprint(input,key))throw J.Error("模型列表已失效或地址、协议、密钥已变更，请重新获取模型后选择默认模型。");return new List<string>(found.Models);}
    }
    internal static List<string> Saved(object provider) {
        var values=J.Arr(J.Get(provider,"models"));var models=values.Select(J.S).ToList();
        if(models.Count==0||models.Count>10000||models.Any(id=>id.Trim()!=id||id.Length==0||id.Length>256||id.Any(char.IsControl))||models.Distinct(StringComparer.Ordinal).Count()!=models.Count||!models.Contains(J.Str(provider,"model")))throw J.Error("请重新获取中转模型列表，并从中选择默认模型。");
        return models;
    }
    internal static async Task<object> List(object input){
        string root=ModelProtocol.ModelRoot(input),key=J.Str(input,"key").Trim();
        if(key.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))key=key.Substring(7).Trim();
        if(key==""||key.Length>4096||key.IndexOfAny(new[]{'\r','\n'})>=0)throw J.Error("请先填写有效的 API 调用密钥。");
        try{
            var ids=new SortedSet<string>(StringComparer.Ordinal);
            string url=root+"/models";var cursors=new HashSet<string>();
            for(int page=0;;page++){
                var payload=await Usage.GetForTest(url,key,"",ModelProtocol.Protocol(input));
                if(!(J.Get(payload,"data") is System.Collections.IList))throw J.Error("接口未返回模型列表，请检查 API 地址和调用协议。");
                var items=J.Arr(J.Get(payload,"data"));
                foreach(var item in items){
                    string id=(item is string?(string)item:J.Str(item,"id")).Trim();
                    if(id.Length>0&&id.Length<=256&&!id.Any(char.IsControl))ids.Add(id);
                    if(ids.Count>10000)throw J.Error("接口返回的模型数量超过 10000，无法完整同步。");
                }
                if(!J.True(J.Get(payload,"has_more")))break;
                string cursor=J.Str(payload,"last_id");if(cursor==""&&items.Count>0)cursor=items[items.Count-1] is string?J.S(items[items.Count-1]):J.Str(items[items.Count-1],"id");
                if(page>=99||cursor==""||cursor.Length>256||cursor.Any(char.IsControl)||!cursors.Add(cursor))throw J.Error("模型列表分页无效或过多，未保存不完整列表，请检查中转接口。");
                url=root+"/models?after="+Uri.EscapeDataString(cursor);
            }
            if(ids.Count==0)throw J.Error("该密钥未返回可用模型，无法设置默认模型，请检查中转权限。");
            string token=Guid.NewGuid().ToString("N");var models=ids.ToList();
            lock(sync){foreach(string expired in discoveries.Where(pair=>pair.Value.Expires<=DateTime.UtcNow).Select(pair=>pair.Key).ToArray())discoveries.Remove(expired);if(discoveries.Count>=64)discoveries.Remove(discoveries.OrderBy(pair=>pair.Value.Expires).First().Key);discoveries[token]=new Discovery{Fingerprint=Fingerprint(input,key),Models=models,Expires=DateTime.UtcNow.AddMinutes(30)};}
            return J.Map("models",models,"discoveryToken",token);
        }catch(Exception error){
            string message=Usage.ErrorText(error,key);
            if(message.Contains("HTTP 401"))message="API 密钥无效、已过期或与此中转不匹配。"+message;
            else if(message.Contains("HTTP 403"))message="此密钥没有读取模型列表的权限，或被站点拒绝访问。"+message;
            else if(message.Contains("HTTP 404")||message.Contains("HTTP 405"))message="该地址未开放模型列表接口，请检查 API 地址和调用协议；添加中转需要先获取可用模型。"+message;
            throw J.Error(message);
        }
    }
}
}
