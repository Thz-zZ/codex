using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RelayLite {
internal static class GenerationTest {
    static readonly HttpClient Http=CreateHttp();
    internal static Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> SendOverride;
    internal static TimeSpan Deadline=TimeSpan.FromSeconds(30);
    static HttpClient CreateHttp(){ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;return new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};}
    internal static string Model(object input,string configDir){
        string model=J.Str(input,"model").Trim();
        if(model=="")model=NativeModelCatalog.Read(configDir).Where(value=>J.Str(value,"visibility")=="list"&&J.True(J.Get(value,"supported_in_api"))).OrderBy(value=>J.Num(value,"priority")??double.MaxValue).Select(value=>J.Str(value,"slug")).FirstOrDefault()??"";
        if(model=="")throw J.Error("未找到可用的本机内置模型，请先获取并选择测试模型。");
        if(model.Length>256||model.Any(char.IsControl))throw J.Error("测试模型 ID 无效。");
        return model;
    }
    internal static async Task<object> Run(object input,string configDir=null){
        string root=ModelProtocol.ModelRoot(input),key=ModelProtocol.NormalizeKey(J.Str(input,"key"));
        if(key==""||key.Length>4096||key.IndexOfAny(new[]{'\r','\n'})>=0)throw J.Error("请填写有效的 API 调用密钥。");
        string model=Model(input,configDir),effort=J.Str(input,"reasoning","default");
        if(!ModelProtocol.ValidReasoning(effort))throw J.Error("请选择有效的推理强度。");
        var body=J.Map("model",model,"input",new[]{J.Map("role","user","content",new[]{J.Map("type","input_text","text","Reply with exactly OK.")})},"stream",true,"store",false,"max_output_tokens",512);
        if(effort!="default")body["reasoning"]=J.Map("effort",effort);
        var watch=Stopwatch.StartNew();int status=0;
        using(var timeout=new CancellationTokenSource(Deadline))try{
            using(var request=new HttpRequestMessage(HttpMethod.Post,root+"/responses")){
                ModelProtocol.Authenticate(request,"responses",key);request.Headers.TryAddWithoutValidation("Accept","text/event-stream");
                request.Content=new StringContent(J.Encode(body),Encoding.UTF8,"application/json");
                // A generation request can incur charges: never retry it automatically.
                using(var response=await (SendOverride==null?Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token):SendOverride(request,timeout.Token)).ConfigureAwait(false))
                using(timeout.Token.Register(()=>response.Dispose())){
                    status=(int)response.StatusCode;
                    string text=await ReadBody(response,timeout.Token).ConfigureAwait(false);
                    timeout.Token.ThrowIfCancellationRequested();
                    bool streamed=response.Content.Headers.ContentType?.MediaType=="text/event-stream"||text.Split('\n').Any(line=>line.StartsWith("data:",StringComparison.Ordinal));
                    object completed=null;string failure="";
                    if(!response.IsSuccessStatusCode){try{var error=J.Decode(text);failure=J.Str(J.Get(error,"error"),"message",J.Str(error,"message","接口请求失败。"));}catch{failure="接口未返回有效 JSON 错误信息。";}throw new Usage.HttpFailure(status,failure);}
                    if(streamed){
                        foreach(string data in EventData(text)){
                            object item;try{item=J.Decode(data);}catch{throw J.Error("流式响应包含无法解析的数据。");}
                            string type=J.Str(item,"type");
                            if(type=="error"||type=="response.failed")throw J.Error(J.Str(J.Get(J.Get(item,"response"),"error"),"message",J.Str(J.Get(item,"error"),"message",J.Str(item,"message","模型返回错误。"))));
                            if(type=="response.incomplete")throw J.Error("模型响应未完成，可能已达到测试输出上限；未确认生成成功。");
                            if(type=="response.completed")completed=J.Get(item,"response");
                        }
                    }else{try{completed=J.Decode(text);}catch{throw J.Error("接口未返回有效的 Responses 数据。");}}
                    if(completed==null)throw J.Error("响应已结束，但未收到生成完成事件。");
                    if(J.Get(completed,"error")!=null)throw J.Error(J.Str(J.Get(completed,"error"),"message","模型返回错误。"));
                    if(J.Str(completed,"status")!="completed")throw J.Error("模型响应未完成，未确认生成成功。");
                    string output=string.Concat(J.Arr(J.Get(completed,"output")).Where(item=>J.Str(item,"type")=="message"&&J.Str(item,"role")=="assistant").SelectMany(item=>J.Arr(J.Get(item,"content"))).Where(part=>J.Str(part,"type")=="output_text").Select(part=>J.Str(part,"text")));
                    if(string.IsNullOrWhiteSpace(output))throw J.Error("请求已结束，但没有返回可验证的文本。");
                    string responseModel=J.Str(completed,"model").Trim();
                    return J.Map("status",streamed?"success":"warning","message",streamed?"短文本流式生成成功；工具调用仍需在 Codex 中验证。":"文本生成成功，但接口未返回流式响应。","model",responseModel==""?model:responseModel,"httpStatus",status,"elapsedMs",watch.ElapsedMilliseconds,"streamed",streamed,"inputTokens",J.Num(J.Get(completed,"usage"),"input_tokens"),"outputTokens",J.Num(J.Get(completed,"usage"),"output_tokens"));
                }
            }
        }catch(Exception error){return J.Map("status","error","message",timeout.IsCancellationRequested?"测试已超时；请求可能已被上游接收，请勿连续重复测试。":Usage.ErrorText(error,key,J.Str(input,"accountKey")),"model",model,"httpStatus",status==0?(object)null:status,"elapsedMs",watch.ElapsedMilliseconds);}
    }
    static IEnumerable<string> EventData(string text){
        foreach(string packet in text.Replace("\r\n","\n").Replace('\r','\n').Split(new[]{"\n\n"},StringSplitOptions.RemoveEmptyEntries)){
            string data=string.Join("\n",packet.Split('\n').Where(line=>line.StartsWith("data:",StringComparison.Ordinal)).Select(line=>line.Substring(5).TrimStart(' ')));
            if(data!=""&&data!="[DONE]")yield return data;
        }
    }
    static async Task<string> ReadBody(HttpResponseMessage response,CancellationToken cancellation){
        using(var stream=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using(var buffer=new MemoryStream()){
            byte[] block=new byte[4096];int read;
            while((read=await stream.ReadAsync(block,0,block.Length,cancellation).ConfigureAwait(false))>0){
                if(buffer.Length+read>256*1024)throw J.Error("测试响应过大，已停止读取。");buffer.Write(block,0,read);
                string text=Encoding.UTF8.GetString(buffer.GetBuffer(),0,(int)buffer.Length).Replace("\r\n","\n").Replace('\r','\n');
                int end=text.LastIndexOf("\n\n",StringComparison.Ordinal);
                if(end<0)continue;
                foreach(string data in EventData(text.Substring(0,end))){
                    try{if(new[]{"response.completed","response.failed","response.incomplete","error"}.Contains(J.Str(J.Decode(data),"type")))return text.Substring(0,end+2);}catch{}
                }
            }
            return Encoding.UTF8.GetString(buffer.GetBuffer(),0,(int)buffer.Length);
        }
    }
}
}
