using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace RelayLite {
internal static class ModelProtocol {
    internal static string ApiRoot(string raw) {
        string normalized=Store.BaseUrl(raw);
        var supplied=new Uri(raw.Contains("://")?raw.Trim():"https://"+raw.Trim());
        string path=supplied.AbsolutePath.TrimEnd('/');
        foreach(string suffix in new[]{"/chat/completions","/responses","/messages","/models"})
            if(path.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))path=path.Substring(0,path.Length-suffix.Length);
        if(path=="")path="/v1";
        return new Uri(normalized).GetLeftPart(UriPartial.Authority)+path;
    }
    internal static string Protocol(object value) {
        string protocol=J.Str(value,"protocol","responses");
        if(protocol!="responses"&&protocol!="chat-completions"&&protocol!="anthropic")throw J.Error("请选择 Responses、Chat Completions 或 Claude Messages 调用协议。");
        return protocol;
    }
    internal static bool Bridged(object value){return Protocol(value)!="responses";}
    internal static string SelectedReasoning(object request,object route) {
        string selected=J.Str(J.Get(request,"reasoning"),"effort").Trim();
        if(selected=="")selected=J.Str(route,"reasoning").Trim();
        return selected=="default"?"":selected;
    }
    internal static string SelectedModel(object request,object route) {
        string configured=J.Str(route,"model").Trim();
        if(J.Str(route,"modelSelection")=="catalog"){
            string selected=J.Str(request,"model").Trim();if(selected=="")selected=configured;
            if(!Models.Saved(route).Contains(selected))throw J.Error("请求模型不在已应用的中转模型列表中，请在 Codex 中重新选择可用模型；列表变更后请重新获取并应用中转。");
            return selected;
        }
        return configured!=""?configured:J.Str(request,"model").Trim();
    }
    internal static string ModelRoot(object value){return Bridged(value)?ApiRoot(J.Str(value,"baseUrl")):Store.BaseUrl(J.Str(value,"baseUrl"))+"/v1";}
    internal static void Authenticate(HttpRequestMessage request,string protocol,string key) {
        if(protocol=="anthropic") {
            request.Headers.Add("x-api-key",key);
            request.Headers.Add("anthropic-version","2023-06-01");
        }else request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
    }
    static string WireName(string space,string name) {
        if(space=="")return name;string value=space.Replace('.','_')+"__"+name;
        if(value.Length<=64)return value;
        using(var hash=SHA256.Create())return value.Substring(0,40)+"_"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").Substring(0,20).ToLowerInvariant();
    }
    static List<object> Definitions(object request) {
        var result=new List<object>();Action<object,string> add=null;
        add=(tool,space)=>{
            if(J.Str(tool,"type")=="namespace") {
                string nested=space==""?J.Str(tool,"name"):space+"."+J.Str(tool,"name");
                foreach(var child in J.Arr(J.Get(tool,"tools")))add(child,nested);return;
            }
            var definition=new Dictionary<string,object>(J.Obj(tool));definition["namespace"]=space;definition["wireName"]=WireName(space,J.Str(tool,"name"));result.Add(definition);
        };
        foreach(var tool in J.Arr(J.Get(request,"tools")))add(tool,"");
        foreach(var item in J.Arr(J.Get(request,"input")))if(J.Str(item,"type")=="additional_tools")foreach(var tool in J.Arr(J.Get(item,"tools")))add(tool,"");
        var merged=new Dictionary<string,object>(StringComparer.Ordinal);var hosted=new List<object>();
        foreach(var tool in result) {
            if(!new[]{"function","custom"}.Contains(J.Str(tool,"type"))){hosted.Add(tool);continue;}
            string name=J.Str(tool,"wireName");object previous;
            if(merged.TryGetValue(name,out previous)&&(J.Str(previous,"name")!=J.Str(tool,"name")||J.Str(previous,"namespace")!=J.Str(tool,"namespace")))throw J.Error("桥接工具名称冲突，请调整工具或命名空间名称。");
            merged[name]=tool;
        }
        return merged.Values.Concat(hosted).ToList();
    }
    static object Content(object value) {
        if(value is string)return value;
        var parts=new List<object>();
        foreach(var part in J.Arr(value)) {
            string type=J.Str(part,"type");
            if(type=="input_text"||type=="output_text"||type=="text")parts.Add(J.Map("type","text","text",J.Str(part,"text")));
            else if(type=="input_image") {
                var image=J.Map("url",J.Str(part,"image_url"));
                if(J.Str(part,"detail")!="")image["detail"]=J.Str(part,"detail");
                if(J.Str(image,"url")=="")throw J.Error("桥接模式的图片需要 image_url 或 data URL。");
                parts.Add(J.Map("type","image_url","image_url",image));
            }else throw J.Error("Chat Completions 桥接暂不支持输入内容："+type+"。");
        }
        if(parts.All(part=>J.Str(part,"type")=="text"))return string.Join("\n",parts.Select(part=>J.Str(part,"text")));
        return parts;
    }
    internal static Dictionary<string,object> Request(object request,object route,bool preserveEncryptedReasoning=false,bool preserveToolImages=false) {
        if(J.Str(request,"previous_response_id")!="")throw J.Error("桥接模式需要完整会话输入，不支持 previous_response_id。");
        var messages=new List<object>();var toolImages=new List<object>();string instructions=J.Str(request,"instructions"),reasoning="";
        Action flushImages=()=>{if(toolImages.Count>0){messages.Add(J.Map("role","user","content",toolImages.ToArray()));toolImages.Clear();}};
        if(instructions!="")messages.Add(J.Map("role","system","content",instructions));
        object input=J.Get(request,"input");
        if(input is string)messages.Add(J.Map("role","user","content",input));
        else foreach(var item in J.Arr(input)) {
            string type=J.Str(item,"type","message");
            if(type=="additional_tools")continue;
            if(type=="reasoning") {
                string encrypted=J.Str(item,"encrypted_content");
                if(encrypted.StartsWith("relay-dpapi:",StringComparison.Ordinal))reasoning=new Secure().Decrypt(encrypted.Substring(12));
                else if(preserveEncryptedReasoning&&encrypted!="")messages.Add(J.Map("role","assistant","content",null,"encrypted_reasoning",new[]{encrypted}));
                continue;
            }
            if(type=="function_call_output"||type=="custom_tool_call_output") {
                object output=Content(J.Get(item,"output"));
                if(!preserveToolImages&&!(output is string)){
                    var parts=J.Arr(output);string callId=J.Str(item,"call_id");
                    toolImages.Add(J.Map("type","text","text","Images returned by tool call "+callId+":"));
                    toolImages.AddRange(parts.Where(part=>J.Str(part,"type")=="image_url"));
                    output=string.Join("\n",parts.Where(part=>J.Str(part,"type")=="text").Select(part=>J.Str(part,"text")))+"\n[Tool images are attached in the following user message.]";
                }
                messages.Add(J.Map("role","tool","tool_call_id",J.Str(item,"call_id"),"content",output));
                continue;
            }
            flushImages();
            if(type=="function_call"||type=="custom_tool_call") {
                var last=messages.Count==0?null:J.Obj(messages[messages.Count-1]);
                if(last==null||J.Str(last,"role")!="assistant") {last=J.Map("role","assistant","content",null);messages.Add(last);}
                if(reasoning!=""){last["reasoning_content"]=reasoning;reasoning="";}
                var calls=J.Arr(J.Get(last,"tool_calls"));
                calls.Add(J.Map("id",J.Str(item,"call_id"),"type","function","function",J.Map("name",WireName(J.Str(item,"namespace"),J.Str(item,"name")),"arguments",type=="custom_tool_call"?J.Encode(J.Map("input",J.Str(item,"input"))):J.Str(item,"arguments","{}"))));
                last["tool_calls"]=calls;continue;
            }
            if(type!="message")throw J.Error("Chat Completions 桥接暂不支持输入项目："+type+"。");
            string role=J.Str(item,"role","user");if(role=="developer")role="system";
            if(!new[]{"system","user","assistant"}.Contains(role))throw J.Error("桥接收到不支持的消息角色："+role+"。");
            var message=J.Map("role",role,"content",Content(J.Get(item,"content")));
            if(role=="assistant"&&reasoning!=""){message["reasoning_content"]=reasoning;reasoning="";}
            messages.Add(message);
        }
        flushImages();
        string model=SelectedModel(request,route);
        if(model==""||messages.Count==0)throw J.Error("桥接调用需要模型名称与消息内容。");
        var result=J.Map("model",model,"messages",messages,"stream",J.True(J.Get(request,"stream")));
        if(J.True(J.Get(request,"stream")))result["stream_options"]=J.Map("include_usage",true);
        foreach(string field in new[]{"temperature","top_p","parallel_tool_calls"})if(J.Get(request,field)!=null)result[field]=J.Get(request,field);
        if(J.Num(request,"max_output_tokens").HasValue)result["max_tokens"]=J.Get(request,"max_output_tokens");
        var format=J.Get(J.Get(request,"text"),"format");
        if(J.Str(format,"type")=="json_schema")result["response_format"]=J.Map("type","json_schema","json_schema",J.Map("name",J.Str(format,"name","response"),"schema",J.Get(format,"schema"),"strict",J.Get(format,"strict",false)));
        else if(J.Str(format,"type")=="json_object")result["response_format"]=J.Map("type","json_object");
        string effort=SelectedReasoning(request,route);
        if(effort!=""&&effort!="default") {
            if(!new[]{"low","medium","high"}.Contains(effort))throw J.Error("桥接不支持该推理强度；请选择 low、medium、high 或“服务默认”，不会自动降级。");
            result["reasoning_effort"]=effort;
        }
        var tools=new List<object>();
        foreach(var tool in Definitions(request)) {
            string type=J.Str(tool,"type");var function=J.Map("name",J.Str(tool,"wireName"),"description",J.Str(tool,"description"));
            if(type=="web_search"||type=="web_search_preview"||type=="tool_search")continue;
            if(type=="function")function["parameters"]=J.Get(tool,"parameters",J.Map("type","object","properties",J.Map()));
            else if(type=="custom")function["parameters"]=J.Map("type","object","properties",J.Map("input",J.Map("type","string","description","The exact raw input for this tool.")),"required",new[]{"input"},"additionalProperties",false);
            else throw J.Error("Chat Completions 桥接暂不支持工具类型："+type+"；请关闭对应的服务端工具后重试。");
            tools.Add(J.Map("type","function","function",function));
        }
        if(tools.Count>0)result["tools"]=tools;
        object choice=J.Get(request,"tool_choice");
        if(choice is string)result["tool_choice"]=choice;
        else if(choice!=null) {
            if(!new[]{"function","custom"}.Contains(J.Str(choice,"type")))throw J.Error("桥接不支持该 tool_choice。");
            result["tool_choice"]=J.Map("type","function","function",J.Map("name",WireName(J.Str(choice,"namespace"),J.Str(choice,"name"))));
        }
        return result;
    }

    internal sealed class Response {
        readonly object request;readonly Action<object> emit;readonly Secure secure=new Secure();
        readonly List<object> output=new List<object>();readonly SortedDictionary<int,ToolCall> calls=new SortedDictionary<int,ToolCall>();
        readonly StringBuilder text=new StringBuilder(),reasoning=new StringBuilder();
        Dictionary<string,object> message,thought;object usage;string finish="";int sequence;
        internal object CacheUsage {get;private set;}
        readonly string id="resp_"+Guid.NewGuid().ToString("N");readonly long created=J.Unix();
        sealed class ToolCall {internal string Id="";internal readonly StringBuilder Name=new StringBuilder(),Arguments=new StringBuilder();}
        internal Response(object request,Action<object> emit){this.request=request;this.emit=emit;}
        void Event(string type,params object[] fields) {
            if(emit==null)return;var value=J.Map("type",type,"sequence_number",sequence++);
            for(int index=0;index<fields.Length;index+=2)value[(string)fields[index]]=fields[index+1];emit(value);
        }
        internal void Start(){Event("response.created","response",Envelope("in_progress"));Event("response.in_progress","response",Envelope("in_progress"));}
        internal void BeginReasoning(){if(thought==null){thought=J.Map("id","rs_"+Guid.NewGuid().ToString("N"),"type","reasoning","summary",new object[0]);output.Add(thought);Event("response.output_item.added","output_index",output.IndexOf(thought),"item",thought);}}
        internal void PreserveReasoning(string prefix,object value){
            string serialized=J.Encode(value);
            if(text.Length+reasoning.Length+calls.Sum(pair=>pair.Value.Arguments.Length)+serialized.Length>16*1024*1024)throw J.Error("模型响应超过桥接的 16 MiB 限制。");
            BeginReasoning();thought["encrypted_content"]=prefix+secure.Encrypt(serialized);
        }
        object Envelope(string status) {
            return J.Map("id",id,"object","response","created_at",created,"status",status,"model",J.Str(request,"model"),"output",output.ToArray(),"error",null,"incomplete_details",status=="incomplete"?J.Map("reason","max_output_tokens"):null,"usage",usage,"parallel_tool_calls",J.Get(request,"parallel_tool_calls",true));
        }
        internal void Push(object chunk,bool complete=false) {
            if(J.Get(chunk,"error")!=null)throw J.Error(J.Str(J.Get(chunk,"error"),"message","模型服务返回错误。"));
            if(J.Get(chunk,"usage")!=null) {
                var source=J.Get(chunk,"usage");double incoming=J.Num(source,"prompt_tokens")??0,outgoing=J.Num(source,"completion_tokens")??0;
                var details=J.Get(source,"prompt_tokens_details");
                double? read=J.Count(J.Get(details,"cached_tokens"))??J.Count(J.Get(source,"prompt_cache_hit_tokens")),write=J.Count(J.Get(source,"prompt_cache_creation_tokens"))??J.Count(J.Get(details,"cache_creation_tokens"))??J.Count(J.Get(source,"cache_creation_input_tokens"));
                CacheUsage=J.Map("input",incoming,"output",outgoing,"read",read,"write",write);
                usage=J.Map("input_tokens",incoming,"output_tokens",outgoing,"total_tokens",J.Num(source,"total_tokens")??incoming+outgoing,"input_tokens_details",J.Map("cached_tokens",read??0),"output_tokens_details",J.Map("reasoning_tokens",J.Num(J.Get(source,"completion_tokens_details"),"reasoning_tokens")??0));
            }
            foreach(var choice in J.Arr(J.Get(chunk,"choices"))) {
                if((J.Num(choice,"index")??0)!=0)continue;
                var delta=J.Get(choice,complete?"message":"delta");
                string thoughtDelta=J.Str(delta,"reasoning_content",J.Str(delta,"reasoning"));
                if(thoughtDelta!="") {
                    BeginReasoning();
                    reasoning.Append(thoughtDelta);
                }
                string deltaText=J.Str(delta,"content");
                if(deltaText=="")deltaText=J.Str(delta,"refusal");
                if(J.Get(delta,"content")!=null&&!(J.Get(delta,"content") is string))deltaText=J.S(Content(J.Get(delta,"content")));
                if(deltaText!="") {
                    if(message==null) {
                        message=J.Map("id","msg_"+Guid.NewGuid().ToString("N"),"type","message","status","in_progress","role","assistant","content",new List<object>());output.Add(message);
                        Event("response.output_item.added","output_index",output.IndexOf(message),"item",message);
                        Event("response.content_part.added","item_id",J.Str(message,"id"),"output_index",output.IndexOf(message),"content_index",0,"part",J.Map("type","output_text","text","","annotations",new object[0]));
                    }
                    text.Append(deltaText);Event("response.output_text.delta","item_id",J.Str(message,"id"),"output_index",output.IndexOf(message),"content_index",0,"delta",deltaText);
                }
                var toolDeltas=J.Arr(J.Get(delta,"tool_calls"));
                for(int toolIndex=0;toolIndex<toolDeltas.Count;toolIndex++) {
                    var tool=toolDeltas[toolIndex];int index=(int)(J.Num(tool,"index")??toolIndex);ToolCall call;
                    if(!calls.TryGetValue(index,out call)){call=new ToolCall();calls.Add(index,call);}
                    if(J.Str(tool,"id")!="")call.Id+=J.Str(tool,"id");
                    call.Name.Append(J.Str(J.Get(tool,"function"),"name"));call.Arguments.Append(J.Str(J.Get(tool,"function"),"arguments"));
                }
                if(J.Str(choice,"finish_reason")!="")finish=J.Str(choice,"finish_reason");
            }
            if(text.Length+reasoning.Length+calls.Sum(pair=>pair.Value.Arguments.Length)>16*1024*1024)throw J.Error("模型响应超过桥接的 16 MiB 限制。");
        }
        internal object Finish() {
            if(finish=="")throw J.Error("模型连接提前结束，未收到 finish_reason；请重试。");
            if(finish=="content_filter")throw J.Error("模型服务拦截了本次响应（content_filter）。");
            if(!new[]{"stop","tool_calls","length","function_call"}.Contains(finish))throw J.Error("模型返回了不支持的结束原因："+finish+"。");
            if(thought!=null){if(!J.Has(thought,"encrypted_content"))thought["encrypted_content"]="relay-dpapi:"+secure.Encrypt(reasoning.ToString());Event("response.output_item.done","output_index",output.IndexOf(thought),"item",thought);}
            if(message!=null) {
                var part=J.Map("type","output_text","text",text.ToString(),"annotations",new object[0]);message["content"]=new[]{part};message["status"]="completed";
                Event("response.output_text.done","item_id",J.Str(message,"id"),"output_index",output.IndexOf(message),"content_index",0,"text",text.ToString());
                Event("response.content_part.done","item_id",J.Str(message,"id"),"output_index",output.IndexOf(message),"content_index",0,"part",part);
                Event("response.output_item.done","output_index",output.IndexOf(message),"item",message);
            }
            foreach(var call in calls.Values) {
                string name=call.Name.ToString(),arguments=call.Arguments.ToString();
                if(name=="")throw J.Error("模型返回的工具调用缺少名称。");
                var definition=Definitions(request).FirstOrDefault(tool=>J.Str(tool,"wireName")==name);
                if(definition==null)throw J.Error("模型调用了未提供的工具："+name+"。");
                bool custom=J.Str(definition,"type")=="custom";string value=arguments==""?"{}":arguments;
                if(custom) {
                    var parsed=J.Decode(value);
                    if(!(J.Get(parsed,"input") is string))throw J.Error("自定义工具 "+name+" 缺少字符串 input。");
                    value=J.Str(parsed,"input");
                }
                string field=custom?"input":"arguments",eventName=custom?"custom_tool_call_input":"function_call_arguments";
                var item=J.Map("id",(custom?"ctc_":"fc_")+Guid.NewGuid().ToString("N"),"type",custom?"custom_tool_call":"function_call","call_id",call.Id==""?"call_"+Guid.NewGuid().ToString("N"):call.Id,"name",J.Str(definition,"name"),"status","in_progress",field,"");
                if(J.Str(definition,"namespace")!="")item["namespace"]=J.Str(definition,"namespace");
                int index=output.Count;output.Add(item);Event("response.output_item.added","output_index",index,"item",item);
                Event("response."+eventName+".delta","item_id",J.Str(item,"id"),"output_index",index,"delta",value);
                item[field]=value;item["status"]="completed";
                Event("response."+eventName+".done","item_id",J.Str(item,"id"),"output_index",index,field,value);
                Event("response.output_item.done","output_index",index,"item",item);
            }
            string status=finish=="length"?"incomplete":"completed";var response=Envelope(status);
            Event("response."+status,"response",response);return response;
        }
        internal void Fail(string message){var response=J.Obj(Envelope("failed"));response["error"]=J.Map("code","bridge_error","message",message);Event("response.failed","response",response);}
    }
}
}
