using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace RelayLite {
internal static class ModelProtocol {
    internal static bool IsCodex(object value) {
        string family=J.Str(value,"modelFamily").Trim(),protocol=J.Str(value,"protocol").Trim();
        return (family==""||family=="codex")&&(protocol==""||protocol=="responses");
    }
    internal static string Protocol(object value) {
        if(!IsCodex(value))throw J.Error("本应用仅支持 Codex 的 Responses 直连。旧中转请编辑为 Codex，重新获取模型列表并保存后再应用。");
        return "responses";
    }
    internal static void ValidateAddress(string raw) {
        Uri uri;
        if(Uri.TryCreate(raw,UriKind.Absolute,out uri)&&uri.IsLoopback&&uri.AbsolutePath.StartsWith("/relay/",StringComparison.OrdinalIgnoreCase))
            throw J.Error("本应用不再提供本地模型桥接。请编辑原中转，使用支持 Codex Responses 的上游地址和密钥重新获取模型。");
    }
    internal static string ModelRoot(object value) {
        Protocol(value);string root=Store.BaseUrl(J.Str(value,"baseUrl"));ValidateAddress(root);return root+"/v1";
    }
    internal static void Authenticate(HttpRequestMessage request,string protocol,string key) {
        Protocol(J.Map("protocol",protocol));request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
    }
}
}
