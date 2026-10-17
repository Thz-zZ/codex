using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
namespace RelayLite {
internal static class NetworkFailureChecks {
    static void Check(bool ok,string label){if(!ok)throw J.Error("Network failure: "+label);}
    internal static async Task Run(string root,List<string> checks){
        var store=new Store(Path.Combine(root,"network-failure-store"),Path.Combine(root,"network-failure-config"),new Secure());
        var provider=store.Upsert(J.Map("name","network fixture","baseUrl","https://network.example/v1","key","fake-network-secret","adapter","auto"));string id=J.Str(provider,"id");
        store.SetDetection(id,J.Str(provider,"revision"),"newapi");var paths=new List<string>();int attempts=0;
        Func<string,object> success=url=>url.Contains("/api/log/token")?J.Map("success",true,"data",new object[0]):J.Map("data",J.Map("quota_per_unit",500000,"user_quota",J.Map("total_available",0)));
        try{
            Usage.FetchOverride=(url,key,user)=>{paths.Add(url);if(url.Contains("/api/log/token")&&++attempts==1)throw new HttpRequestException("connection closed",new IOException("fake-network-secret"));return Task.FromResult(success(url));};
            var result=await Usage.Inspect(store,id,"today");
            Check(J.Get(result,"usage")!=null&&attempts==2&&!paths.Any(x=>x.Contains("/v1/usage")),"one transient disconnect retries the same adapter and recovers");
            foreach(string adapter in new[]{"newapi","sub2api"}){
                store.SetDetection(id,J.Str(provider,"revision"),adapter);paths.Clear();
                Usage.FetchOverride=(url,key,user)=>{paths.Add(url);throw new TaskCanceledException("fixture cancellation");};
                result=await Usage.Inspect(store,id,"today");
                Check(paths.Count==1&&J.Str(result,"adapter")==adapter&&J.Str(store.Details(id),"detectedAdapter")==adapter,"timeout stops probing, balance requests and retries, preserving known "+adapter);
                Check(J.Str(result,"usageError").Contains("连接超时")&&!J.Str(result,"usageError").Contains("识别接口"),"timeout is not reported as an unknown API format");
                Check(J.Num(J.Get(J.Get(result,"summary"),"balance"),"value")==0,"failed network request retains known zero balance");
            }
            store.SetDetection(id,J.Str(provider,"revision"),"newapi");paths.Clear();
            Usage.FetchOverride=(url,key,user)=>{paths.Add(url);throw new HttpRequestException("connection closed",new IOException("fake-network-secret"));};
            result=await Usage.Inspect(store,id,"today");
            Check(paths.Count==2&&paths[0]==paths[1]&&!J.Encode(result).Contains("fake-network-secret"),"repeated disconnect is bounded to one retry and redacted");
            paths.Clear();Usage.FetchOverride=(url,key,user)=>{paths.Add(url);if(url.Contains("/api/log/token"))throw new Usage.HttpFailure(401,"invalid fake-network-secret");return Task.FromResult(success(url));};
            result=await Usage.Inspect(store,id,"today");
            Check(paths.Count(x=>x.Contains("/api/log/token"))==1&&!paths.Any(x=>x.Contains("/v1/usage"))&&!J.Encode(result).Contains("fake-network-secret"),"401 neither retries credentials nor probes the other protocol");
            var input=J.Map("baseUrl","https://network.example/v1","key","fake-network-secret","adapter","auto");
            foreach(Exception failure in new Exception[]{new TaskCanceledException(),new WebException("bad certificate",null,WebExceptionStatus.TrustFailure,null),new Usage.HttpFailure(503,"unavailable")}){
                paths.Clear();Usage.FetchOverride=(url,key,user)=>{paths.Add(url);throw failure;};var preview=await Usage.DetectAdapter(input);
                Check(paths.Count==1&&J.Get(preview,"adapter")==null&&J.Str(preview,"message").Contains("统计接口请求失败"),"draft request failure stops adapter probing and retry: "+failure.GetType().Name);
            }
            string tls=Usage.ErrorText(new HttpRequestException("closed",new AuthenticationException("EOF",new IOException("fake-network-secret"))),"fake-network-secret");
            Check(tls.Contains("HTTPS")&&!tls.Contains("fake-network-secret"),"nested TLS disconnect is identified and redacted");
            checks.Add("bounded GET retry, network failures preserve known adapters and balances, stop protocol probing, and redact errors");
        }finally{Usage.FetchOverride=null;}
        await WireChecks(checks);
    }
    static async Task WireChecks(List<string> checks){
        // No real relay, credentials or TLS overrides: exercise the actual HTTP parser and body deadline locally.
        foreach(bool stalled in new[]{false,true}){
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            string url="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/fixture";
            var served=Task.Run(async()=>{
                using(var client=await listener.AcceptTcpClientAsync())
                using(var stream=client.GetStream()){
                    client.ReceiveTimeout=5000;var request=new StringBuilder();var block=new byte[1024];
                    while(!request.ToString().Contains("\r\n\r\n")){int read=stream.Read(block,0,block.Length);if(read==0||request.Length>16384)throw J.Error("Invalid local HTTP fixture request");request.Append(Encoding.ASCII.GetString(block,0,read));}
                    string body=stalled?"{":"<html>unavailable</html>",status=stalled?"200 OK":"503 Service Unavailable";
                    byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+"\r\nContent-Length: "+(stalled?128:body.Length)+"\r\nConnection: close\r\n\r\n"+body);
                    await stream.WriteAsync(response,0,response.Length);
                    if(stalled)await Task.Delay(15000);
                }
            });
            var watch=Stopwatch.StartNew();Exception failure=null;
            try{
                if(stalled)Usage.FetchOverride=(address,key,user)=>{Usage.FetchOverride=null;throw new HttpRequestException("initial fixture disconnect");};
                try{await Usage.GetForTest(url,"");}catch(Exception ex){failure=ex;}
                watch.Stop();
            }finally{Usage.FetchOverride=null;listener.Stop();await served;}
            if(stalled)Check(failure is OperationCanceledException&&watch.Elapsed.TotalSeconds>10&&watch.Elapsed.TotalSeconds<14.5,"stalled response body after a retry obeys the shared 12-second deadline");
            else Check(failure is Usage.HttpFailure&&((Usage.HttpFailure)failure).Status==503&&!Usage.CanProbeAnotherAdapter(failure),"an HTML 503 preserves HTTP status and cannot masquerade as an unsupported adapter");
        }
        checks.Add("real loopback HTTP: HTML 503 classification and a stalled body cancelled within the shared GET deadline");
    }
}
}
