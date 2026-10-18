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
internal static class ClaudeProtocolChecks {
    static void Check(bool condition,string label){if(!condition)throw J.Error("Claude bridge: "+label);}
    static Dictionary<string,object> Input(bool stream=true,string model="claude-fixture") {
        return J.Map("model",model,"stream",stream,"instructions","fixture system","input","你好","tools",new[]{J.Map("type","namespace","name","functions","tools",new[]{J.Map("type","function","name","inspect","parameters",J.Map("type","object","properties",J.Map())),J.Map("type","custom","name","exec")})});
    }
    static object Event(string type,params object[] fields){var value=J.Map("type",type);for(int index=0;index<fields.Length;index+=2)value[J.S(fields[index])]=fields[index+1];return value;}
    internal static async Task Run(string root,List<string> checks) {
        Check(ModelProtocol.ApiRoot("https://api.anthropic.com/v1/messages")=="https://api.anthropic.com/v1","Messages suffix normalized");
        var input=Input();input["parallel_tool_calls"]=false;input["tool_choice"]="required";
        var converted=ClaudeProtocol.Request(input,J.Map());
        Check(J.Str(ClaudeProtocol.Request(Input(true,"gpt-6.1-sol"),J.Map("model","claude-fixture")),"model")=="claude-fixture","Claude model selection cannot be overridden by a stale Codex session");
        var defaultEffort=ClaudeProtocol.Request(Input(),J.Map("reasoning","default"));Check(!J.Has(defaultEffort,"reasoning_effort")&&!J.Has(defaultEffort,"thinking"),"explicit Claude service default sends no unsupported thinking parameters");
        Check(!J.Has(defaultEffort,"output_config")&&!J.Has(defaultEffort,"cache_control"),"service default does not inject effort or vendor-specific cache parameters");
        foreach(string effort in new[]{"low","medium","high"}) {
            var stale=Input();stale["reasoning"]=J.Map("effort","ultra");stale["text"]=J.Map("format",J.Map("type","json_schema","schema",J.Map("type","object")));
            var native=ClaudeProtocol.Request(stale,J.Map("reasoning",effort));var config=J.Get(native,"output_config");
            Check(J.Str(config,"effort")==effort&&J.Str(J.Get(config,"format"),"type")=="json_schema","Claude effort uses the applied route and coexists with structured output");
            Check(!J.Has(native,"reasoning_effort")&&!J.Has(native,"thinking")&&!J.Has(native,"cache_control"),"native effort remains independent of upstream-managed caching");
        }
        foreach(string effort in new[]{"xhigh","max","ultra","invalid"}) {bool invalid=false;try{ClaudeProtocol.Request(Input(),J.Map("reasoning",effort));}catch{invalid=true;}Check(invalid,"unsupported native effort cannot be silently omitted or downgraded");}
        Check(J.Str(J.Arr(J.Get(converted,"system"))[0],"text")=="fixture system"&&J.Str(J.Arr(J.Get(J.Arr(J.Get(converted,"messages"))[0],"content"))[0],"text")=="你好","system instructions and Unicode user input");
        Check(J.Num(converted,"max_tokens")==8192&&!J.Has(converted,"stream_options")&&!J.Has(converted,"reasoning_effort"),"required max_tokens and no OpenAI-only fields");
        Check(J.Str(J.Get(converted,"tool_choice"),"type")=="any"&&J.True(J.Get(J.Get(converted,"tool_choice"),"disable_parallel_tool_use")),"tool choice and parallel flag translated");
        var tools=J.Arr(J.Get(converted,"tools"));Check(J.Str(tools[0],"name")=="functions__inspect"&&J.Has(tools[1],"input_schema"),"namespaced functions and custom tools use native schemas");
        var history=new object[]{J.Map("role","user","content",new[]{J.Map("type","input_image","image_url","data:image/png;base64,aGVsbG8="),J.Map("type","input_image","image_url","https://image.example/fixture.png")}),J.Map("type","function_call","namespace","functions","name","inspect","call_id","inspect-fixture","arguments","{}"),J.Map("type","custom_tool_call","namespace","functions","name","exec","call_id","custom-fixture","input","text(1);"),J.Map("type","function_call_output","call_id","inspect-fixture","output","inspected"),J.Map("type","custom_tool_call_output","call_id","custom-fixture","output","executed")};
        input["input"]=history;converted=ClaudeProtocol.Request(input,J.Map());var messages=J.Arr(J.Get(converted,"messages"));
        Check(messages.Count==3&&J.Arr(J.Get(messages[1],"content")).Count==2&&J.Arr(J.Get(messages[2],"content")).Count==2,"parallel tool uses and results grouped into alternating messages");
        var images=J.Arr(J.Get(messages[0],"content"));Check(J.Str(J.Get(images[0],"source"),"type")=="base64"&&J.Str(J.Get(images[1],"source"),"type")=="url","inline and remote images translated");
        history[3]=J.Map("type","function_call_output","call_id","inspect-fixture","output",new[]{J.Map("type","input_text","text","picture metadata"),J.Map("type","input_image","image_url","data:image/png;base64,aGVsbG8=")});
        history[4]=J.Map("type","custom_tool_call_output","call_id","custom-fixture","output",new[]{J.Map("type","input_image","image_url","https://image.example/tool.png")});input["input"]=history;
        messages=J.Arr(J.Get(ClaudeProtocol.Request(input,J.Map()),"messages"));var toolResults=J.Arr(J.Get(messages[2],"content"));var toolContent=J.Arr(J.Get(toolResults[0],"content"));
        Check(messages.Count==3&&toolResults.Count==2&&J.Str(toolResults[0],"tool_use_id")=="inspect-fixture"&&J.Str(toolContent[0],"text")=="picture metadata"&&J.Str(J.Get(toolContent[1],"source"),"type")=="base64","native Claude tool results retain images within the correct parallel tool result");
        Check(J.Str(J.Get(J.Arr(J.Get(toolResults[1],"content"))[0],"source"),"url")=="https://image.example/tool.png","native image-only custom tool results use image source blocks");
        input["text"]=J.Map("format",J.Map("type","json_schema","schema",J.Map("type","object")));Check(J.Has(ClaudeProtocol.Request(input,J.Map()),"output_config"),"JSON schema output translated");
        var output=new ModelProtocol.Response(Input(),null);var response=new ClaudeProtocol.Response(output);
        response.Push(Event("message_start","message",J.Map("usage",J.Map("input_tokens",10,"cache_read_input_tokens",5,"cache_creation_input_tokens",2,"output_tokens",0))));
        response.Push(Event("content_block_start","index",0,"content_block",J.Map("type","text","text","")));
        response.Push(Event("content_block_delta","index",0,"delta",J.Map("type","text_delta","text","检查中")));
        response.Push(Event("content_block_stop","index",0));
        response.Push(Event("content_block_start","index",1,"content_block",J.Map("type","tool_use","id","fixture-tool","name","functions__exec","input",J.Map())));
        response.Push(Event("content_block_delta","index",1,"delta",J.Map("type","input_json_delta","partial_json","{\"input\":")));
        response.Push(Event("content_block_delta","index",1,"delta",J.Map("type","input_json_delta","partial_json","\"text(1);\"}")));
        response.Push(Event("content_block_stop","index",1));
        response.Push(Event("message_delta","delta",J.Map("stop_reason","tool_use"),"usage",J.Map("output_tokens",8)));
        response.Push(Event("message_stop"));response.Finish();var result=output.Finish();var items=J.Arr(J.Get(result,"output"));
        Check(items.Count==2&&J.Str(items[1],"type")=="custom_tool_call"&&J.Str(items[1],"namespace")=="functions"&&J.Str(items[1],"input")=="text(1);","streamed text and fragmented custom tool arguments converted to Responses");
        Check(J.Num(J.Get(result,"usage"),"input_tokens")==17&&J.Num(J.Get(J.Get(result,"usage"),"input_tokens_details"),"cached_tokens")==5,"cache creation, cache reads and output usage combined correctly");
        Check(J.Num(output.CacheUsage,"write")==2&&J.Num(output.CacheUsage,"read")==5,"native cache writes remain separate from cache read hits");
        output=new ModelProtocol.Response(Input(),null);response=new ClaudeProtocol.Response(output);response.Push(Event("message_delta","delta",J.Map("stop_reason","end_turn")));bool rejected=false;try{response.Finish();}catch{rejected=true;}Check(rejected,"missing message_stop cannot report success");
        output=new ModelProtocol.Response(Input(false),null);response=new ClaudeProtocol.Response(output);response.Push(J.Map("type","message","content",new[]{J.Map("type","text","text","partial")},"stop_reason","max_tokens"),true);response.Finish();Check(J.Str(output.Finish(),"status")=="incomplete","native JSON token limit remains incomplete");
        Check(J.Get(output.CacheUsage,"read")==null&&J.Get(output.CacheUsage,"write")==null,"missing native cache counters remain unknown rather than fabricated zeroes");
        output=new ModelProtocol.Response(Input(false),null);response=new ClaudeProtocol.Response(output);response.Push(J.Map("type","message","content",new[]{J.Map("type","text","text","answer")},"stop_reason","end_turn","usage",J.Map("input_tokens",4,"output_tokens",1,"cache_read_input_tokens",0,"cache_creation_input_tokens",12)),true);response.Finish();output.Finish();
        Check(J.Num(output.CacheUsage,"read")==0&&J.Num(output.CacheUsage,"write")==12&&J.Num(output.CacheUsage,"input")==16,"a first cache write is not misreported as a read hit");
        rejected=false;try{new ClaudeProtocol.Response(new ModelProtocol.Response(Input(),null)).Push(Event("error","error",J.Map("message","fixture overload")));}catch(Exception error){rejected=error.Message=="fixture overload";}Check(rejected,"native SSE errors remain actionable");
        checks.Add("Claude Messages instructions, images, native tools, custom tool fragments, JSON schema, cached usage and truncation");
        checks.Add("native Claude low/medium/high effort, no injected cache controls, separate upstream writes/hits and missing-counter handling");
        ThinkingChecks(checks);
        await Network(root,checks);
    }
    static void ThinkingChecks(List<string> checks) {
        var events=new List<object>();var output=new ModelProtocol.Response(Input(),events.Add);output.Start();var response=new ClaudeProtocol.Response(output);
        response.Push(Event("message_start","message",J.Map("usage",J.Map("input_tokens",3))));
        response.Push(Event("content_block_start","index",0,"content_block",J.Map("type","thinking","thinking","private ","signature","")));
        response.Push(Event("content_block_delta","index",0,"delta",J.Map("type","thinking_delta","thinking","native thought")));
        foreach(string signature in new[]{"signed-","fixture"})response.Push(Event("content_block_delta","index",0,"delta",J.Map("type","signature_delta","signature",signature)));
        response.Push(Event("content_block_stop","index",0));
        response.Push(Event("content_block_start","index",1,"content_block",J.Map("type","redacted_thinking","data","opaque-redacted-fixture")));
        response.Push(Event("content_block_stop","index",1));
        response.Push(Event("content_block_start","index",2,"content_block",J.Map("type","text","text","working")));
        response.Push(Event("content_block_stop","index",2));
        response.Push(Event("content_block_start","index",3,"content_block",J.Map("type","tool_use","id","thinking-tool","name","functions__inspect","input",J.Map())));
        response.Push(Event("content_block_delta","index",3,"delta",J.Map("type","input_json_delta","partial_json","{}")));
        response.Push(Event("content_block_stop","index",3));
        response.Push(Event("message_delta","delta",J.Map("stop_reason","tool_use")));response.Push(Event("message_stop"));response.Finish();var result=output.Finish();
        var items=J.Arr(J.Get(result,"output"));string encrypted=J.Str(items[0],"encrypted_content"),prefix="relay-claude-dpapi:";
        Check(items.Count==3&&J.Str(items[0],"type")=="reasoning"&&encrypted.StartsWith(prefix)&&J.Str(items[2],"type")=="function_call","streamed thinking precedes text and tool output without disconnecting");
        string serialized=J.Encode(result)+J.Encode(events);Check(!serialized.Contains("private native thought")&&!serialized.Contains("signed-fixture")&&!serialized.Contains("opaque-redacted-fixture"),"private thinking, signatures and redactions never appear in plaintext Responses output");
        var preserved=J.Arr(J.Decode(new Secure().Decrypt(encrypted.Substring(prefix.Length))));
        Check(preserved.Count==2&&J.Str(preserved[0],"thinking")=="private native thought"&&J.Str(preserved[0],"signature")=="signed-fixture"&&J.Str(preserved[1],"data")=="opaque-redacted-fixture","thinking and split signatures are preserved losslessly with redacted blocks");
        var history=new List<object>{J.Map("role","user","content","inspect")};history.AddRange(items);history.Add(J.Map("type","function_call_output","call_id","thinking-tool","output","inspected"));
        var continuation=Input();continuation["input"]=history;var converted=ClaudeProtocol.Request(continuation,J.Map("reasoning","default"));var messages=J.Arr(J.Get(converted,"messages"));var assistant=J.Arr(J.Get(messages[1],"content"));
        Check(messages.Count==3&&assistant.Count==4&&J.Encode(assistant.Take(2).ToArray())==J.Encode(preserved)&&J.Str(assistant[3],"id")=="thinking-tool"&&J.Str(J.Arr(J.Get(messages[2],"content"))[0],"tool_use_id")=="thinking-tool","signed thinking blocks are replayed unchanged before tool uses on continuation");
        Check(!J.Has(converted,"thinking")&&!J.Encode(converted).Contains("encrypted_reasoning"),"accepting default-service thinking does not enable thinking or leak internal metadata upstream");
        Check(!J.Encode(ModelProtocol.Request(continuation,J.Map())).Contains("opaque-redacted-fixture"),"Claude-native opaque reasoning is not forwarded when switching to Chat Completions");
        output=new ModelProtocol.Response(Input(false),null);response=new ClaudeProtocol.Response(output);
        response.Push(J.Map("type","message","content",new[]{J.Map("type","thinking","thinking","private JSON thought","signature","json-signature"),J.Map("type","redacted_thinking","data","json-redacted"),J.Map("type","text","text","answer")},"stop_reason","end_turn"),true);response.Finish();result=output.Finish();
        Check(J.Str(result,"status")=="completed"&&J.Arr(J.Get(result,"output")).Count==2&&!J.Encode(result).Contains("private JSON thought"),"non-streaming thinking and redacted blocks complete with encrypted continuation");
        output=new ModelProtocol.Response(Input(false),null);response=new ClaudeProtocol.Response(output);
        response.Push(J.Map("type","message","content",new[]{J.Map("type","thinking","thinking","partial private thought","signature","")},"stop_reason","max_tokens"),true);response.Finish();result=output.Finish();
        Check(J.Str(result,"status")=="incomplete","thinking-only token exhaustion is not misreported as completion");
        history=new List<object>{J.Map("role","user","content","first")};history.AddRange(J.Arr(J.Get(result,"output")));history.Add(J.Map("role","user","content","next"));continuation["input"]=history;
        Check(J.Arr(J.Get(ClaudeProtocol.Request(continuation,J.Map()),"messages")).Count==3,"thinking-only history retains its assistant boundary before the next user turn");
        output=new ModelProtocol.Response(Input(),null);response=new ClaudeProtocol.Response(output);response.Push(Event("content_block_start","index",0,"content_block",J.Map("type","thinking","thinking","")));
        bool rejected=false;try{response.Push(Event("message_stop"));}catch{rejected=true;}Check(rejected,"unfinished thinking blocks cannot report successful completion");
        checks.Add("Claude streamed and JSON thinking, split signatures, encrypted redactions, tool continuation and truncated thinking");
    }
    static async Task Network(string root,List<string> checks) {
        string directory=Path.Combine(root,"claude-bridge"),config=Path.Combine(directory,"config");Directory.CreateDirectory(config);
        var server=new TcpListener(IPAddress.Loopback,0);server.Start();string baseUrl="http://127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port+"/v1";
        var release=new TaskCompletionSource<bool>();object endpoint;string id;
        using(var http=new HttpClient(new HttpClientHandler {UseProxy=false}) {Timeout=TimeSpan.FromSeconds(12)})try {
            using(var bridge=new ModelBridge(directory)) {
                var store=new Store(directory,config,new Secure(),bridge);var provider=store.Upsert(J.Map("name","Claude fixture","baseUrl",baseUrl,"modelFamily","claude","protocol","anthropic","model","claude-fixture","reasoning","high","adapter","none","key","fake-claude-secret"),true);id=J.Str(provider,"id");store.Switch(id);
                var info=store.ConfigInfo();
                var document=Nett.Toml.ReadString(File.ReadAllText(Path.Combine(config,"config.toml"))).ToDictionary();endpoint=J.Map("baseUrl",J.Str(info,"baseUrl"),"key",J.Str(J.Get(J.Get(document,"model_providers"),J.Str(info,"provider")),"experimental_bearer_token"));
                Check(J.Str(info,"baseUrl").StartsWith("http://127.0.0.1:")&&!File.ReadAllText(Path.Combine(config,"config.toml")).Contains("fake-claude-secret"),"Claude config uses local credentials only");
                var fixture=Task.Run(async()=>{
                    using(var client=await server.AcceptTcpClientAsync())using(var stream=client.GetStream()) {
                        var request=await ModelBridgeChecks.ReadRequest(stream);string headers=J.Str(request,"headers");
                        Check(headers.StartsWith("POST /v1/messages ")&&headers.Contains("x-api-key: fake-claude-secret")&&headers.Contains("anthropic-version: 2023-06-01")&&!headers.Contains("Authorization:"),"native endpoint and authentication, no bearer credential leak");
                        Check(J.Has(J.Get(request,"body"),"system")&&J.Has(J.Get(request,"body"),"max_tokens"),"native body arrives at upstream");
                        Check(J.Str(J.Get(request,"body"),"model")=="claude-fixture","native upstream model ignores a stale Gemini client model");
                        Check(J.Str(J.Get(J.Get(request,"body"),"output_config"),"effort")=="high"&&!J.Has(J.Get(request,"body"),"cache_control"),"applied high effort reaches upstream without vendor-specific cache injection");
                        await ModelBridgeChecks.Write(stream,"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
                        foreach(var value in new[]{Event("message_start","message",J.Map("usage",J.Map("input_tokens",4))),Event("content_block_start","index",0,"content_block",J.Map("type","thinking","thinking","")),Event("content_block_delta","index",0,"delta",J.Map("type","thinking_delta","thinking","private-network-thought")),Event("content_block_delta","index",0,"delta",J.Map("type","signature_delta","signature","network-signature")),Event("content_block_stop","index",0),Event("content_block_start","index",1,"content_block",J.Map("type","redacted_thinking","data","network-redacted")),Event("content_block_stop","index",1),Event("content_block_start","index",2,"content_block",J.Map("type","text","text","")),Event("content_block_delta","index",2,"delta",J.Map("type","text_delta","text","Claude 你好"))})await ModelBridgeChecks.Write(stream,ModelBridgeChecks.HttpChunk(ModelBridgeChecks.Data(value)));
                        if(await Task.WhenAny(release.Task,Task.Delay(8000))!=release.Task)throw J.Error("Claude bridge did not stream incrementally.");
                        foreach(var value in new[]{Event("content_block_stop","index",2),Event("message_delta","delta",J.Map("stop_reason","end_turn"),"usage",J.Map("output_tokens",2)),Event("message_stop")})await ModelBridgeChecks.Write(stream,ModelBridgeChecks.HttpChunk(ModelBridgeChecks.Data(value)));
                        await ModelBridgeChecks.Write(stream,"0\r\n\r\n");
                    }
                });
                using(var outgoing=new HttpRequestMessage(HttpMethod.Post,J.Str(endpoint,"baseUrl")+"/responses")) {
                    outgoing.Headers.Authorization=new AuthenticationHeaderValue("Bearer",J.Str(endpoint,"key"));outgoing.Content=new StringContent(J.Encode(Input(true,"gemini-stale-session")),Encoding.UTF8,"application/json");
                    using(var incoming=await http.SendAsync(outgoing,HttpCompletionOption.ResponseHeadersRead))using(var stream=await incoming.Content.ReadAsStreamAsync())using(var reader=new StreamReader(stream)) {
                        Check(incoming.IsSuccessStatusCode,"streaming HTTP success");string line;bool completed=false;
                        while((line=await reader.ReadLineAsync())!=null)if(line.StartsWith("data: ")){Check(!line.Contains("private-network-thought")&&!line.Contains("network-signature")&&!line.Contains("network-redacted"),"native thinking remains encrypted across loopback HTTP");var value=J.Decode(line.Substring(6));string type=J.Str(value,"type");if(type=="response.output_text.delta")release.TrySetResult(true);if(type=="response.completed"){completed=true;Check(J.Str(J.Arr(J.Get(J.Get(value,"response"),"output"))[0],"encrypted_content").StartsWith("relay-claude-dpapi:"),"loopback response retains native thinking continuation");}if(J.Get(value,"response")!=null)Check(J.Str(J.Get(value,"response"),"model")=="claude-fixture","native Responses events identify the actual upstream model");}
                        Check(completed&&release.Task.IsCompleted,"incremental text and terminal lifecycle delivered");
                    }
                }
                await fixture;
                var latest=J.Get(store.Details(id),"bridgeUsage");
                Check(J.Str(latest,"model")=="claude-fixture"&&J.Str(latest,"reasoning")=="high"&&J.Get(J.Get(latest,"cache"),"read")==null,"latest native diagnostics expose sent settings without guessing missing counters");
                Check(!J.Encode(latest).Contains("private-network-thought")&&!J.Encode(latest).Contains("network-signature")&&!J.Encode(latest).Contains("fake-claude-secret"),"diagnostic snapshots contain no prompts, thinking or credentials");
            }
            using(var bridge=new ModelBridge(directory)) {
                var store=new Store(directory,config,new Secure(),bridge);bridge.Restore(store.ConfigInfo());Check(store.ActiveProviderId()==id,"Claude route identity restored after restart");
                var fixture=Task.Run(async()=>{using(var client=await server.AcceptTcpClientAsync())using(var stream=client.GetStream()){var request=await ModelBridgeChecks.ReadRequest(stream);Check(J.Str(request,"headers").StartsWith("POST /v1/messages "),"native protocol persisted across restart");string body=J.Encode(J.Map("type","message","content",new[]{J.Map("type","text","text","restored Claude")},"stop_reason","end_turn"));await ModelBridgeChecks.Write(stream,"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\nConnection: close\r\n\r\n"+body);}});
                using(var outgoing=new HttpRequestMessage(HttpMethod.Post,J.Str(endpoint,"baseUrl")+"/responses")){outgoing.Headers.Authorization=new AuthenticationHeaderValue("Bearer",J.Str(endpoint,"key"));outgoing.Content=new StringContent(J.Encode(Input(false)),Encoding.UTF8,"application/json");using(var incoming=await http.SendAsync(outgoing))Check(incoming.IsSuccessStatusCode&&J.Str(J.Decode(await incoming.Content.ReadAsStringAsync()),"status")=="completed","native JSON reply survives restart");}await fixture;
            }
            foreach(bool connectionTest in new[]{false,true}) {
                var fixture=Task.Run(async()=>{using(var client=await server.AcceptTcpClientAsync())using(var stream=client.GetStream()) {
                    var header=new List<byte>();while(!Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n")){int value=stream.ReadByte();if(value<0||header.Count>65536)throw J.Error("Claude models fixture header incomplete.");header.Add((byte)value);}
                    string headers=Encoding.ASCII.GetString(header.ToArray());Check(headers.StartsWith("GET /v1/models ")&&headers.Contains("x-api-key: fake-claude-secret")&&headers.Contains("anthropic-version: 2023-06-01")&&!headers.Contains("Authorization:"),"model discovery and connection diagnostics use native auth");
                    string body=J.Encode(J.Map("data",new[]{J.Map("id","claude-fixture")}));await ModelBridgeChecks.Write(stream,"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\nConnection: close\r\n\r\n"+body);
                }});
                var input=J.Map("baseUrl",baseUrl,"protocol","anthropic","key","fake-claude-secret");var result=connectionTest?await ConnectionTest.Run(input):await Models.List(input);
                Check(connectionTest?J.Str(J.Arr(J.Get(result,"checks"))[0],"status")=="success":J.S(J.Arr(J.Get(result,"models"))[0])=="claude-fixture","native model discovery and diagnostics succeed");await fixture;
            }
        }finally{release.TrySetResult(true);server.Stop();}
        checks.Add("Claude loopback HTTP incremental streaming, native authentication, config apply, restart, JSON replies and model discovery");
    }
}
}
