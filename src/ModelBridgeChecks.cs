using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace RelayLite {
internal static class ModelBridgeChecks {
    static void Check(bool condition,string label){if(!condition)throw J.Error("Model bridge: "+label);}
    static object Tool(){return J.Map("type","function","name","shell","description","Run a command","parameters",J.Map("type","object","properties",J.Map()));}
    static object Input(bool stream=true,string model="deepseek-fixture"){return J.Map("model",model,"stream",stream,"instructions","fixture instructions","input",new[]{J.Map("role","user","content",new[]{J.Map("type","input_text","text","你好")})},"tools",new[]{Tool()});}
    static object Chunk(string text,string finish=null){return J.Map("choices",new[]{J.Map("index",0,"delta",J.Map("content",text),"finish_reason",finish)});}
    internal static async Task Run(string root,List<string> checks) {
        foreach(string url in new[]{"https://api.deepseek.com/v1","https://dashscope.aliyuncs.com/compatible-mode/v1","https://open.bigmodel.cn/api/paas/v4","https://generativelanguage.googleapis.com/v1beta/openai"})
            Check(ModelProtocol.ApiRoot(url)==url,"versioned provider API paths preserved: "+url);
        Check(ModelProtocol.ApiRoot("fixture.example")=="https://fixture.example/v1"&&ModelProtocol.ApiRoot("https://fixture.example/proxy")=="https://fixture.example/proxy","root-only default and custom deployment path");
        var converted=ModelProtocol.Request(Input(),J.Map());
        Check(J.Str(converted,"model")=="deepseek-fixture"&&J.Str(J.Arr(J.Get(converted,"messages"))[1],"content")=="你好"&&!J.Has(converted,"reasoning_effort"),"messages, Unicode and optional reasoning effort");
        Check(J.Str(ModelProtocol.Request(Input(true,"gpt-6.1-sol"),J.Map("model","deepseek-fixture")),"model")=="deepseek-fixture","configured relay model overrides a stale Codex session model");
        var selectable=J.Map("modelSelection","catalog","models",new[]{"deepseek-fixture","claude-fixture"},"model","deepseek-fixture","reasoning","high");
        Check(J.Str(ModelProtocol.Request(Input(true,"claude-fixture"),selectable),"model")=="claude-fixture"&&J.Str(ClaudeProtocol.Request(Input(true,"claude-fixture"),selectable),"model")=="claude-fixture","catalog selection reaches either protocol without being replaced by the default");
        Check(ModelProtocol.SelectedModel(J.Map(),selectable)=="deepseek-fixture","omitted request model falls back to the configured default");
        bool unknown=false;try{ModelProtocol.SelectedModel(Input(true,"gpt-6.1-sol"),selectable);}catch{unknown=true;}Check(unknown,"catalog routes reject models outside the applied list rather than silently rerouting");
        var selectedEffort=J.Obj(Input());selectedEffort["reasoning"]=J.Map("effort","medium");
        Check(J.Str(ModelProtocol.Request(selectedEffort,J.Map("reasoning","low")),"reasoning_effort")=="medium","Codex session effort overrides the relay default");
        Check(J.Str(ModelProtocol.Request(selectedEffort,J.Map("reasoning","default")),"reasoning_effort")=="medium","unset relay effort follows the Codex session selection");
        Check(!J.Has(ModelProtocol.Request(Input(),J.Map("reasoning","default")),"reasoning_effort"),"an absent session effort and unset relay default send no effort");
        foreach(string family in new[]{"codex","claude","grok","deepseek","qwen","glm","gemini"})foreach(string effort in new[]{"low","medium","high"}) {
            var chat=ModelProtocol.Request(Input(),J.Map("modelFamily",family,"reasoning",effort));
            Check(J.Str(chat,"reasoning_effort")==effort&&!J.Has(chat,"cache_control"),"every compatible relay family uses explicit Chat effort without incompatible Claude cache parameters");
            selectedEffort["reasoning"]=J.Map("effort",effort);
            Check(J.Str(ModelProtocol.Request(selectedEffort,J.Map("modelFamily",family,"reasoning","high")),"reasoning_effort")==effort,"all compatible relay families honor Codex session effort");
        }
        var cacheInput=J.Obj(Input());cacheInput["cache_control"]=J.Map("type","ephemeral");cacheInput["prompt_cache_key"]="client-cache-fixture";cacheInput["prompt_cache_retention"]="24h";
        foreach(string protocol in new[]{"chat-completions","anthropic"}){
            var outgoing=protocol=="anthropic"?ClaudeProtocol.Request(cacheInput,J.Map()):ModelProtocol.Request(cacheInput,J.Map());
            Check(!J.Has(outgoing,"cache_control")&&!J.Has(outgoing,"prompt_cache_key")&&!J.Has(outgoing,"prompt_cache_retention"),"global upstream-managed policy does not invent or forward incompatible cache controls into bridged APIs");
        }
        checks.Add("shared upstream-managed prompt caching policy without vendor cache flags or local response reuse");
        foreach(string effort in new[]{"xhigh","max","ultra","invalid"}) {bool invalid=false;try{ModelProtocol.Request(Input(),J.Map("reasoning",effort));}catch{invalid=true;}Check(invalid,"unsupported Chat effort is rejected before reaching upstream");}
        selectedEffort["reasoning"]=J.Map("effort","ultra");bool unsupportedEffort=false;try{ModelProtocol.Request(selectedEffort,J.Map("reasoning","low"));}catch{unsupportedEffort=true;}Check(unsupportedEffort,"unsupported session effort cannot silently fall back to the configured default");
        var custom=J.Map("type","custom","name","apply_patch","description","Apply the exact patch");
        var request=J.Obj(Input());request["tools"]=new[]{Tool(),custom};
        var events=new List<object>();var response=new ModelProtocol.Response(request,value=>events.Add(J.Clone(value)));response.Start();
        response.Push(J.Map("choices",new[]{J.Map("delta",J.Map("reasoning_content","private fixture reasoning"),"finish_reason",null)}));
        response.Push(J.Map("choices",new[]{J.Map("delta",J.Map("tool_calls",new[]{J.Map("index",0,"id","call_fixture","function",J.Map("name","shell","arguments","{\"command\":")),J.Map("index",1,"id","patch_fixture","function",J.Map("name","apply_patch","arguments",J.Encode(J.Map("input","*** Begin Patch\n*** End Patch"))))}),"finish_reason",null)}));
        response.Push(J.Map("choices",new[]{J.Map("delta",J.Map("tool_calls",new[]{J.Map("index",0,"function",J.Map("arguments","\"echo fixture\"}"))}),"finish_reason","tool_calls")},"usage",J.Map("prompt_tokens",20,"completion_tokens",5,"prompt_cache_hit_tokens",10)));
        var result=response.Finish();var output=J.Arr(J.Get(result,"output"));
        Check(output.Count==3&&J.Str(output[1],"arguments")=="{\"command\":\"echo fixture\"}"&&J.Str(output[2],"type")=="custom_tool_call"&&J.Str(output[2],"input").StartsWith("*** Begin Patch"),"fragmented parallel function and custom tool calls");
        Check(J.Num(J.Get(J.Get(result,"usage"),"input_tokens_details"),"cached_tokens")==10,"provider cache usage converted");
        Check(J.Num(response.CacheUsage,"read")==10&&J.Get(response.CacheUsage,"write")==null,"DeepSeek cache hits are retained without treating misses or absent writes as cache creation");
        var cacheResponse=new ModelProtocol.Response(Input(),null);cacheResponse.Push(J.Map("choices",new object[0],"usage",J.Map("prompt_tokens",30,"completion_tokens",1,"prompt_tokens_details",J.Map("cached_tokens",20,"cache_creation_tokens",5))));
        Check(J.Num(cacheResponse.CacheUsage,"read")==20&&J.Num(cacheResponse.CacheUsage,"write")==5,"OpenAI-compatible cache read and creation counters stay separate");
        cacheResponse.Push(J.Map("choices",new object[0],"usage",J.Map("prompt_tokens",30,"prompt_cache_miss_tokens",30)));
        Check(J.Get(cacheResponse.CacheUsage,"read")==null&&J.Get(cacheResponse.CacheUsage,"write")==null,"absent Chat cache counters are unknown and cache misses are not writes");
        Check(!J.Encode(output[0]).Contains("private fixture reasoning"),"reasoning continuation is encrypted");
        var history=new List<object>{J.Map("role","user","content","fixture")};history.AddRange(output);history.Add(J.Map("type","function_call_output","call_id","call_fixture","output","command done"));history.Add(J.Map("type","custom_tool_call_output","call_id","patch_fixture","output",new[]{J.Map("type","input_text","text","patch done")}));
        request["input"]=history;converted=ModelProtocol.Request(request,J.Map());var messages=J.Arr(J.Get(converted,"messages"));var assistant=messages.First(item=>J.Str(item,"role")=="assistant");
        Check(J.Arr(J.Get(assistant,"tool_calls")).Count==2&&J.Str(assistant,"reasoning_content")=="private fixture reasoning"&&messages.Count(item=>J.Str(item,"role")=="tool")==2,"multi-turn tool outputs and provider reasoning restored");
        var shortResponse=new ModelProtocol.Response(Input(),null);shortResponse.Push(Chunk("partial","length"));Check(J.Str(shortResponse.Finish(),"status")=="incomplete","token limit remains incomplete");
        bool rejected=false;try{new ModelProtocol.Response(Input(),null).Finish();}catch{rejected=true;}Check(rejected,"truncated stream cannot report success");
        var unsupported=J.Obj(Input());unsupported["tools"]=new[]{J.Map("type","code_interpreter")};rejected=false;try{ModelProtocol.Request(unsupported,J.Map());}catch{rejected=true;}Check(rejected,"unsupported hosted tools are explained");
        unsupported["tools"]=new[]{J.Map("type","web_search"),Tool()};Check(J.Arr(J.Get(ModelProtocol.Request(unsupported,J.Map()),"tools")).Count==1,"default hosted web search does not block local coding tools");
        var deferred=J.Obj(Input());deferred["tools"]=new[]{J.Map("type","namespace","name","functions","tools",new[]{Tool()}),J.Map("type","web_search"),J.Map("type","tool_search")};
        deferred["input"]=new object[]{J.Map("type","additional_tools","tools",new[]{J.Map("type","namespace","name","functions","tools",new[]{Tool()})}),J.Map("role","user","content","deferred fixture")};
        var deferredTools=J.Arr(J.Get(ModelProtocol.Request(deferred,J.Map()),"tools"));Check(deferredTools.Count==1&&J.Str(J.Get(deferredTools[0],"function"),"name")=="functions__shell","deferred namespaces merge repeated tool definitions and skip hosted discovery");
        var image=J.Obj(Input());image["input"]=new[]{J.Map("role","user","content",new[]{J.Map("type","input_text","text","look"),J.Map("type","input_image","image_url","data:image/png;base64,fixture")})};converted=ModelProtocol.Request(image,J.Map());Check(J.Arr(J.Get(J.Arr(J.Get(converted,"messages"))[1],"content")).Count==2,"image content preserved");
        var imageHistory=new List<object>{J.Map("role","user","content","inspect pictures"),J.Map("type","function_call","name","shell","call_id","image-call","arguments","{}"),J.Map("type","custom_tool_call","name","apply_patch","call_id","text-call","input","fixture")};
        imageHistory.Add(J.Map("type","function_call_output","call_id","image-call","output",new[]{J.Map("type","input_text","text","image metadata"),J.Map("type","input_image","image_url","data:image/png;base64,fixture","detail","high")}));
        imageHistory.Add(J.Map("type","custom_tool_call_output","call_id","text-call","output","parallel text result"));imageHistory.Add(J.Map("role","user","content","continue"));
        image["input"]=imageHistory;messages=J.Arr(J.Get(ModelProtocol.Request(image,J.Map()),"messages"));
        var toolResult=messages.First(message=>J.Str(message,"tool_call_id")=="image-call");int pictureIndex=messages.FindIndex(message=>J.Str(message,"role")=="user"&&J.Get(message,"content") is object[]);
        Check(J.Str(toolResult,"content").Contains("image metadata")&&pictureIndex>messages.FindLastIndex(message=>J.Str(message,"role")=="tool"),"Chat images follow all parallel tool results while tool messages remain text-only");
        var attached=J.Arr(J.Get(messages[pictureIndex],"content"));Check(J.Str(attached[0],"text").Contains("image-call")&&J.Str(J.Get(attached[1],"image_url"),"url")=="data:image/png;base64,fixture"&&J.Str(J.Get(attached[1],"image_url"),"detail")=="high"&&J.Str(messages.Last(),"content")=="continue","tool images preserve data, detail, source identity and the next turn");
        imageHistory.RemoveAt(imageHistory.Count-1);imageHistory[2]=J.Map("type","custom_tool_call","name","apply_patch","call_id","text-call","input","fixture");imageHistory[4]=J.Map("type","custom_tool_call_output","call_id","text-call","output",new[]{J.Map("type","input_image","image_url","https://images.example/fixture.png")});image["input"]=imageHistory;
        messages=J.Arr(J.Get(ModelProtocol.Request(image,J.Map()),"messages"));Check(J.Arr(J.Get(messages.Last(),"content")).Count==4&&messages.Count(message=>J.Str(message,"role")=="tool")==2,"image-only custom tool outputs are preserved in a single trailing image message");
        checks.Add("tool-returned images, text-only Chat tool messages, parallel result ordering and source labels");
        checks.Add("Responses to Chat Completions messages, images, parallel tools, custom tools, reasoning, usage and incomplete responses");
        await Network(root,checks);
    }
    internal static async Task<object> ReadRequest(NetworkStream stream) {
        var header=new List<byte>();
        while(true){int value=stream.ReadByte();if(value<0)throw new IOException("mock request EOF");header.Add((byte)value);if(header.Count>=4&&header.Skip(header.Count-4).SequenceEqual(new byte[]{13,10,13,10}))break;if(header.Count>65536)throw J.Error("mock headers too long");}
        string headers=Encoding.ASCII.GetString(header.ToArray());string line=headers.Split(new[]{"\r\n"},StringSplitOptions.None).First(item=>item.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase));int length=int.Parse(line.Substring(15).Trim());
        var body=new byte[length];int read=0;while(read<length){int count=await stream.ReadAsync(body,read,length-read);if(count==0)throw new IOException("mock body EOF");read+=count;}
        return J.Map("headers",headers,"body",J.Decode(Encoding.UTF8.GetString(body)));
    }
    internal static async Task Write(NetworkStream stream,string value){byte[] bytes=Encoding.UTF8.GetBytes(value);await stream.WriteAsync(bytes,0,bytes.Length);await stream.FlushAsync();}
    internal static string Data(object chunk){return "data: "+J.Encode(chunk)+"\n\n";}
    internal static string HttpChunk(string text){return Encoding.UTF8.GetByteCount(text).ToString("x")+"\r\n"+text+"\r\n";}
    static async Task Network(string root,List<string> checks) {
        string directory=Path.Combine(root,"bridge-service"),config=Path.Combine(root,"bridge-config");Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config,"config.toml"),"model = \"old-fixture\"\nmodel_reasoning_effort = \"ultra\"\n[features]\nkeep = true\n");
        var upstream=new TcpListener(IPAddress.Loopback,0);upstream.Start();int upstreamPort=((IPEndPoint)upstream.LocalEndpoint).Port;
        object endpoint=null,provider=null;string id="";var release=new TaskCompletionSource<bool>();
        using(var http=new HttpClient(new HttpClientHandler {UseProxy=false}) {Timeout=TimeSpan.FromSeconds(12)}) {
            try {
                using(var bridge=new ModelBridge(directory)) {
                    var store=new Store(directory,config,new Secure(),bridge);
                    provider=store.Upsert(J.Map("name","chat fixture","baseUrl","http://127.0.0.1:"+upstreamPort+"/compatible/v4","key","fake-bridge-upstream-secret","modelFamily","deepseek","model","deepseek-fixture","reasoning","default","protocol","chat-completions","adapter","none"),true);id=J.Str(provider,"id");
                    store.Switch(id);var info=store.ConfigInfo();var parsed=Nett.Toml.ReadString(File.ReadAllText(Path.Combine(config,"config.toml"))).ToDictionary();string localKey=J.Str(J.Get(J.Get(parsed,"model_providers"),J.Str(info,"provider")),"experimental_bearer_token");
                    endpoint=J.Map("baseUrl",J.Str(info,"baseUrl"),"key",localKey);
                    Check(store.ActiveProviderId()==id&&J.Str(info,"model")=="deepseek-fixture"&&J.Str(info,"reasoning")=="","bridge activation uses selected model and removes inherited reasoning effort");
                    string persisted=File.ReadAllText(Path.Combine(directory,"model-bridge.json"));Check(!persisted.Contains("fake-bridge-upstream-secret")&&!persisted.Contains(localKey)&&!File.ReadAllText(Path.Combine(config,"config.toml")).Contains("fake-bridge-upstream-secret"),"upstream and local credentials encrypted at rest, Codex receives only local token");
                    using(var denied=await http.PostAsync(J.Str(endpoint,"baseUrl")+"/responses",new StringContent(J.Encode(Input()))))Check(denied.StatusCode==HttpStatusCode.Unauthorized&&!upstream.Pending(),"unauthorized request never reaches upstream");
                    var fixture=Task.Run(async()=>{
                        using(var client=await upstream.AcceptTcpClientAsync())using(var stream=client.GetStream()) {
                            var received=await ReadRequest(stream);Check(J.Str(received,"headers").StartsWith("POST /compatible/v4/chat/completions ")&&J.Str(received,"headers").Contains("Bearer fake-bridge-upstream-secret"),"upstream API path and credential routing");
                            Check(J.Str(J.Arr(J.Get(J.Get(received,"body"),"messages"))[1],"content")=="你好","Unicode HTTP request body");
                            Check(J.Str(J.Get(received,"body"),"model")=="deepseek-fixture","streaming upstream model is bound to the applied relay, not the client model");
                            Check(!J.Has(J.Get(received,"body"),"reasoning_effort"),"service default is not sent to the upstream API");
                            await Write(stream,"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"+HttpChunk(Data(Chunk("第一段"))));
                            if(await Task.WhenAny(release.Task,Task.Delay(5000))!=release.Task)throw J.Error("Bridge failed to deliver an incremental delta.");
                            await Write(stream,HttpChunk(Data(Chunk("第二段","stop"))+Data(J.Map("choices",new object[0],"usage",J.Map("prompt_tokens",3,"completion_tokens",2)))+"data: [DONE]\n\n")+"0\r\n\r\n");
                        }
                    });
                    using(var outgoing=new HttpRequestMessage(HttpMethod.Post,J.Str(endpoint,"baseUrl")+"/responses")) {
                        outgoing.Headers.Authorization=new AuthenticationHeaderValue("Bearer",localKey);outgoing.Content=new StringContent(J.Encode(Input(true,"gpt-6.1-sol")),Encoding.UTF8,"application/json");
                        using(var incoming=await http.SendAsync(outgoing,HttpCompletionOption.ResponseHeadersRead))using(var stream=await incoming.Content.ReadAsStreamAsync())using(var reader=new StreamReader(stream)) {
                            Check(incoming.IsSuccessStatusCode,"stream HTTP status");string line;bool incremental=false;var eventLines=new List<string>();
                            while((line=await reader.ReadLineAsync())!=null) {
                                eventLines.Add(line);
                                if(line.StartsWith("data: ")){var value=J.Decode(line.Substring(6));if(J.Str(value,"type")=="response.output_text.delta"){incremental=true;release.TrySetResult(true);}if(J.Get(value,"response")!=null)Check(J.Str(J.Get(value,"response"),"model")=="deepseek-fixture","Responses lifecycle reports the actual configured model");}
                            }
                            Check(incremental&&string.Join("\n",eventLines).Contains("response.completed"),"first text delta arrives while upstream is still waiting, followed by completion: "+string.Join("\n",eventLines));
                        }
                    }
                    await fixture;var inspected=await Usage.Inspect(store,id,"today");
                    Check(J.Str(J.Get(inspected,"bridgeUsage"),"model")=="deepseek-fixture"&&J.Get(J.Get(J.Get(inspected,"bridgeUsage"),"cache"),"read")==null,"bridge diagnostics refresh even when upstream account statistics are disabled");
                }
                using(var restarted=new ModelBridge(directory)) {
                    var store=new Store(directory,config,new Secure(),restarted);restarted.Restore(store.ConfigInfo());Check(store.ActiveProviderId()==id,"bridge restores active identity after restart");
                    var fixture=Task.Run(async()=>{using(var client=await upstream.AcceptTcpClientAsync())using(var stream=client.GetStream()){await ReadRequest(stream);string body=J.Encode(J.Map("choices",new[]{J.Map("index",0,"message",J.Map("content","restarted fixture"),"finish_reason","stop")}));await Write(stream,"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\nConnection: close\r\n\r\n"+body);}});
                    using(var outgoing=new HttpRequestMessage(HttpMethod.Post,J.Str(endpoint,"baseUrl")+"/responses")) {
                        outgoing.Headers.Authorization=new AuthenticationHeaderValue("Bearer",J.Str(endpoint,"key"));outgoing.Content=new StringContent(J.Encode(Input(false)),Encoding.UTF8,"application/json");
                        using(var incoming=await http.SendAsync(outgoing)){Check(incoming.IsSuccessStatusCode&&J.Str(J.Decode(await incoming.Content.ReadAsStringAsync()),"status")=="completed","non-streaming request survives bridge process restart");}
                    }
                    await fixture;
                }
                using(var selectable=new ModelBridge(directory)) {
                    var store=new Store(directory,config,new Secure(),selectable);var input=J.Obj(store.Details(id));input["modelSelection"]="catalog";input["models"]=new[]{"deepseek-fixture","selected-fixture"};store.Upsert(input,true);store.Switch(id);
                    var info=store.ConfigInfo();endpoint=J.Map("baseUrl",J.Str(info,"baseUrl"),"key",J.Str(endpoint,"key"));
                }
                using(var restarted=new ModelBridge(directory)) {
                    var store=new Store(directory,config,new Secure(),restarted);restarted.Restore(store.ConfigInfo());
                    using(var outgoing=new HttpRequestMessage(HttpMethod.Post,J.Str(endpoint,"baseUrl")+"/responses")) {
                        outgoing.Headers.Authorization=new AuthenticationHeaderValue("Bearer",J.Str(endpoint,"key"));outgoing.Content=new StringContent(J.Encode(Input(false,"unlisted-fixture")),Encoding.UTF8,"application/json");
                        using(var incoming=await http.SendAsync(outgoing))Check(incoming.StatusCode==HttpStatusCode.BadRequest&&!upstream.Pending()&&(await incoming.Content.ReadAsStringAsync()).Contains("模型列表"),"catalog allowlist survives restart and rejects unknown models without upstream traffic");
                    }
                    var fixture=Task.Run(async()=>{using(var client=await upstream.AcceptTcpClientAsync())using(var stream=client.GetStream()){var received=await ReadRequest(stream);Check(J.Str(J.Get(received,"body"),"model")=="selected-fixture","persisted catalog routes honor a non-default model after restart");string body=J.Encode(J.Map("choices",new[]{J.Map("index",0,"message",J.Map("content","selected fixture"),"finish_reason","stop")}));await Write(stream,"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\nConnection: close\r\n\r\n"+body);}});
                    using(var outgoing=new HttpRequestMessage(HttpMethod.Post,J.Str(endpoint,"baseUrl")+"/responses")) {
                        outgoing.Headers.Authorization=new AuthenticationHeaderValue("Bearer",J.Str(endpoint,"key"));outgoing.Content=new StringContent(J.Encode(Input(false,"selected-fixture")),Encoding.UTF8,"application/json");
                        using(var incoming=await http.SendAsync(outgoing))Check(incoming.IsSuccessStatusCode&&J.Str(J.Decode(await incoming.Content.ReadAsStringAsync()),"model")=="selected-fixture","response lifecycle reports the switched model, not the default");
                    }
                    await fixture;
                }
            }finally{release.TrySetResult(true);upstream.Stop();}
        }
        checks.Add("loopback HTTP bridge streaming, credential isolation, encrypted persistence, configuration apply, restart and JSON responses");
        checks.Add("switchable catalog routes survive restart, reject unlisted models before upstream and report the selected model");
    }
}
}
