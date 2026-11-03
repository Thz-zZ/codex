using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace RelayLite {
internal static class GenerationChecks {
    static void Check(bool value,string message){if(!value)throw J.Error("Generation test: "+message);}
    static object Completed(string text="OK"){return J.Map("status","completed","output",new[]{J.Map("type","message","role","assistant","content",new[]{J.Map("type","output_text","text",text)})},"usage",J.Map("input_tokens",8,"output_tokens",2));}
    static string Event(string type,object response){return "event: "+type+"\ndata: "+J.Encode(J.Map("type",type,"response",response))+"\n\n";}
    internal static async Task Run(List<string> checks){
        var input=J.Map("baseUrl","https://generation.example/custom/v2","key","fake-generation-key","accountKey","fake-generation-account","model","fixture-model","reasoning","high");int calls=0;
        try{
            string payload=Event("response.completed",Completed());int code=200;string media="text/event-stream";
            GenerationTest.SendOverride=async(request,token)=>{
                calls++;Check(request.Method==HttpMethod.Post&&request.RequestUri.AbsoluteUri=="https://generation.example/custom/v2/responses","POST uses the exact API prefix");
                Check(request.Headers.Authorization.Parameter=="fake-generation-key"&&!request.Headers.ToString().Contains("fake-generation-account"),"only the model API key is sent");
                var body=J.Decode(await request.Content.ReadAsStringAsync());Check(!J.True(J.Get(body,"store"))&&J.True(J.Get(body,"stream"))&&J.Num(body,"max_output_tokens")==512&&J.Str(J.Get(body,"reasoning"),"effort")=="high"&&J.Str(body,"model")=="fixture-model","one bounded nonstored streaming generation uses the selected model and effort");
                return new HttpResponseMessage((HttpStatusCode)code){Content=new StringContent(payload,Encoding.UTF8,media)};
            };
            var result=await GenerationTest.Run(input);Check(J.Str(result,"status")=="success"&&J.True(J.Get(result,"streamed"))&&J.Num(result,"httpStatus")==200&&J.Num(result,"inputTokens")==8,"completed text stream is validated and reports actual metadata");
            Check(J.Str(result,"model")=="fixture-model","a response without model metadata falls back to the requested model");
            var remapped=J.Obj(Completed());remapped["model"]="actual-upstream-model";
            payload=Event("response.completed",remapped);result=await GenerationTest.Run(input);
            Check(J.Str(result,"status")=="success"&&J.Str(result,"model")=="actual-upstream-model","streaming completion reports the response model when the upstream maps an alias");
            media="application/json";payload=J.Encode(remapped);result=await GenerationTest.Run(input);
            Check(J.Str(result,"status")=="warning"&&J.Str(result,"model")=="actual-upstream-model","nonstreaming completion also reports the response model");
            media="text/event-stream";
            foreach(string invalid in new[]{"data: [DONE]\n\n",Event("response.completed",Completed("")),Event("response.incomplete",J.Map("status","incomplete")),"data: broken-json\n\n",Event("response.failed",J.Map("error",J.Map("message","invalid fake-generation-key fake-generation-account")))}){
                payload=invalid;result=await GenerationTest.Run(input);Check(J.Str(result,"status")=="error"&&!J.Encode(result).Contains("fake-generation"),"missing/empty/incomplete/malformed/failed responses cannot be reported as successful or expose secrets");
            }
            code=401;payload="<html>invalid fake-generation-key</html>";int before=calls;result=await GenerationTest.Run(input);Check(calls==before+1&&J.Num(result,"httpStatus")==401&&J.Str(result,"status")=="error"&&!J.Encode(result).Contains("fake-generation"),"HTTP error preserves status, redacts secrets and never retries a POST");
            code=200;media="application/json";payload=J.Encode(Completed());result=await GenerationTest.Run(input);Check(J.Str(result,"status")=="warning"&&!J.True(J.Get(result,"streamed")),"a nonstreaming JSON answer is reported without claiming streaming support");
            input["model"]="";NativeModelCatalog.ReadOverride=()=>J.Map("models",new[]{J.Map("slug","hidden","visibility","hide","supported_in_api",true,"priority",0),J.Map("slug","native-default","visibility","list","supported_in_api",true,"priority",1)});
            Check(GenerationTest.Model(input,null)=="native-default","built-in test mode resolves a local visible API model without discovery");
            input["model"]="fixture-model";GenerationTest.SendOverride=async(request,token)=>{calls++;await Task.Delay(5000,token);return null;};GenerationTest.Deadline=TimeSpan.FromMilliseconds(120);before=calls;result=await GenerationTest.Run(input);Check(J.Str(result,"status")=="error"&&J.Str(result,"message").Contains("超时")&&calls==before+1,"generation has a bounded shared deadline and no automatic retry");
            checks.Add("manual generation: exact path/key/model/effort, real completion validation, stream/error/empty/incomplete handling, redaction and no POST retry");
        }finally{GenerationTest.SendOverride=null;GenerationTest.Deadline=TimeSpan.FromSeconds(30);NativeModelCatalog.ReadOverride=null;}
        await Wire(checks);
    }
    static async Task Wire(List<string> checks){
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();string address="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/v1";
        var served=Task.Run(async()=>{
            using(var client=await listener.AcceptTcpClientAsync())using(var stream=client.GetStream())using(var reader=new StreamReader(stream,Encoding.UTF8,false,1024,true)){
                string start=await reader.ReadLineAsync();Check(start=="POST /v1/responses HTTP/1.1","real wire request targets Responses");int length=0;bool authenticated=false;string line;
                while(!string.IsNullOrEmpty(line=await reader.ReadLineAsync())){if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))length=int.Parse(line.Substring(15).Trim());if(line.Equals("Authorization: Bearer wire-generation-key",StringComparison.OrdinalIgnoreCase))authenticated=true;}
                var content=new char[length];int offset=0;while(offset<length){int count=await reader.ReadAsync(content,offset,length-offset);if(count==0)throw J.Error("Truncated generation fixture");offset+=count;}
                Check(authenticated&&J.Str(J.Decode(new string(content)),"model")=="wire-model","real wire body and authorization are correct");
                string body=Event("response.completed",Completed());byte[] bytes=Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\nConnection: close\r\n\r\n"+body);await stream.WriteAsync(bytes,0,bytes.Length);
            }
        });
        try{var result=await GenerationTest.Run(J.Map("baseUrl",address,"key","wire-generation-key","model","wire-model"));Check(J.Str(result,"status")=="success","actual HTTP transport validates a streamed model answer");await served;}finally{listener.Stop();}
        checks.Add("real loopback Responses POST: streaming text completion, API authorization and request-body verification");
    }
}
}
