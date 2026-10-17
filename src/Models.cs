using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace RelayLite {
internal static class Models {
    internal static async Task<object> List(object input){
        string root=Store.BaseUrl(J.Str(input,"baseUrl")),key=J.Str(input,"key").Trim();
        if(key.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))key=key.Substring(7).Trim();
        if(key==""||key.Length>4096||key.IndexOfAny(new[]{'\r','\n'})>=0)throw J.Error("请先填写有效的 API 调用密钥。");
        try{
            var payload=await Usage.GetForTest(root+"/v1/models",key);
            if(!(J.Get(payload,"data") is System.Collections.IList))throw J.Error("接口未返回模型列表，请检查 API 地址，或手动填写模型名称。");
            var ids=new SortedSet<string>(StringComparer.Ordinal);
            foreach(var item in J.Arr(J.Get(payload,"data"))){
                string id=(item is string?(string)item:J.Str(item,"id")).Trim();
                if(id.Length>0&&id.Length<=256&&!id.Any(char.IsControl))ids.Add(id);
                if(ids.Count>10000)throw J.Error("接口返回的模型数量过多，请手动填写模型名称。");
            }
            return J.Map("models",ids.ToList());
        }catch(Exception error){
            string message=Usage.ErrorText(error,key);
            if(message.Contains("HTTP 401"))message="API 密钥无效、已过期或与此中转不匹配。"+message;
            else if(message.Contains("HTTP 403"))message="此密钥没有读取模型列表的权限，或被站点拒绝访问。"+message;
            else if(message.Contains("HTTP 404")||message.Contains("HTTP 405"))message="该地址未开放模型列表接口，请检查 API 地址，也可以手动填写模型。"+message;
            throw J.Error(message);
        }
    }
}
}
