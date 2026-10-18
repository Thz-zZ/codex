using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RelayLite {
internal static class ClaudeProtocol {
    const string ThinkingPrefix="relay-claude-dpapi:";
    static List<object> RestoreThinking(object values) {
        var result=new List<object>();
        foreach(var value in J.Arr(values)) {
            string encrypted=J.S(value);if(!encrypted.StartsWith(ThinkingPrefix,StringComparison.Ordinal))continue;
            foreach(var block in J.Arr(J.Decode(new Secure().Decrypt(encrypted.Substring(ThinkingPrefix.Length))))) {
                string type=J.Str(block,"type");
                if(type=="thinking")result.Add(J.Map("type",type,"thinking",J.Str(block,"thinking"),"signature",J.Str(block,"signature")));
                else if(type=="redacted_thinking")result.Add(J.Map("type",type,"data",J.Str(block,"data")));
                else throw J.Error("Claude 思考续传数据包含不支持的内容类型。");
            }
        }
        return result;
    }
    static List<object> Content(object value) {
        if(value is string)return J.S(value)==""?new List<object>():new List<object>{J.Map("type","text","text",value)};
        var result=new List<object>();
        foreach(var part in J.Arr(value)) {
            string type=J.Str(part,"type");
            if(type=="text")result.Add(J.Map("type","text","text",J.Str(part,"text")));
            else if(type=="image_url") {
                string url=J.Str(J.Get(part,"image_url"),"url");object source;
                if(url.StartsWith("data:",StringComparison.OrdinalIgnoreCase)) {
                    var match=Regex.Match(url,@"^data:(image/(?:jpeg|png|gif|webp));base64,(.+)$",RegexOptions.IgnoreCase);
                    if(!match.Success)throw J.Error("Claude 图片需要 PNG、JPEG、GIF 或 WebP 的 base64 data URL。");
                    source=J.Map("type","base64","media_type",match.Groups[1].Value.ToLowerInvariant(),"data",match.Groups[2].Value);
                }else {
                    Uri uri;if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||!new[]{"https","http"}.Contains(uri.Scheme))throw J.Error("Claude 图片需要 HTTP(S) URL 或 base64 data URL。");
                    source=J.Map("type","url","url",url);
                }
                result.Add(J.Map("type","image","source",source));
            }else throw J.Error("Claude Messages 桥接不支持内容："+type+"。");
        }
        return result;
    }
    internal static Dictionary<string,object> Request(object request,object route) {
        var chat=ModelProtocol.Request(request,route,true,true);var system=new List<object>();var messages=new List<object>();
        foreach(var message in J.Arr(J.Get(chat,"messages"))) {
            string role=J.Str(message,"role");
            if(role=="system"){system.AddRange(Content(J.Get(message,"content")));continue;}
            object value=J.Get(message,"content","");
            var content=role=="tool"?new List<object>{J.Map("type","tool_result","tool_use_id",J.Str(message,"tool_call_id"),"content",value is string?value:Content(value))}:Content(value);
            if(role=="assistant")content.InsertRange(0,RestoreThinking(J.Get(message,"encrypted_reasoning")));
            if(role=="tool")role="user";
            foreach(var call in J.Arr(J.Get(message,"tool_calls"))) {
                var function=J.Get(call,"function");var arguments=J.Decode(J.Str(function,"arguments","{}"));
                if(!(arguments is Dictionary<string,object>))throw J.Error("Claude 工具参数必须是 JSON 对象。");
                content.Add(J.Map("type","tool_use","id",J.Str(call,"id"),"name",J.Str(function,"name"),"input",arguments));
            }
            if(content.Count==0)continue;
            var previous=messages.Count==0?null:J.Obj(messages[messages.Count-1]);
            if(previous!=null&&J.Str(previous,"role")==role){var combined=J.Arr(J.Get(previous,"content"));combined.AddRange(content);previous["content"]=combined;}
            else messages.Add(J.Map("role",role,"content",content));
        }
        if(messages.Count==0)throw J.Error("Claude Messages 需要用户消息。");
        var result=J.Map("model",J.Str(chat,"model"),"messages",messages,"stream",J.Get(chat,"stream",false),"max_tokens",J.Get(chat,"max_tokens",8192));
        string effort=J.Str(route,"reasoning");
        if(effort!=""&&effort!="default") {
            if(!new[]{"low","medium","high"}.Contains(effort))throw J.Error("Claude Messages 不支持该推理强度；请选择 low、medium、high 或“服务默认”，不会自动降级。");
            result["output_config"]=J.Map("effort",effort);
        }
        if(system.Count>0)result["system"]=system;
        if(J.Get(chat,"temperature")!=null)result["temperature"]=J.Get(chat,"temperature");
        else if(J.Get(chat,"top_p")!=null)result["top_p"]=J.Get(chat,"top_p");
        var tools=J.Arr(J.Get(chat,"tools")).Select(tool=>{var function=J.Get(tool,"function");return (object)J.Map("name",J.Str(function,"name"),"description",J.Str(function,"description"),"input_schema",J.Get(function,"parameters"));}).ToList();
        if(tools.Count>0) {
            result["tools"]=tools;object choice=J.Get(chat,"tool_choice");
            string type=choice is string?J.S(choice):choice==null?"auto":"tool";
            if(type=="required")type="any";
            var selected=J.Map("type",type);
            if(type=="tool")selected["name"]=J.Str(J.Get(choice,"function"),"name");
            if(type!="none"&&J.Get(chat,"parallel_tool_calls") is bool&&!J.True(J.Get(chat,"parallel_tool_calls")))selected["disable_parallel_tool_use"]=true;
            result["tool_choice"]=selected;
        }
        var format=J.Get(chat,"response_format");
        if(J.Str(format,"type")=="json_schema") {
            var config=J.Obj(J.Get(result,"output_config"));config["format"]=J.Map("type","json_schema","schema",J.Get(J.Get(format,"json_schema"),"schema"));result["output_config"]=config;
        }
        else if(format!=null)throw J.Error("Claude Messages 的结构化输出需要明确的 JSON Schema。");
        return result;
    }
    internal sealed class Response {
        readonly ModelProtocol.Response response;readonly Dictionary<int,string> blocks=new Dictionary<int,string>();
        readonly SortedDictionary<int,ThinkingBlock> thinking=new SortedDictionary<int,ThinkingBlock>();
        readonly Dictionary<string,object> usage=J.Map();string reason="";bool ended;
        sealed class ThinkingBlock {
            internal string Type,Data;
            internal readonly StringBuilder Text=new StringBuilder(),Signature=new StringBuilder();
            internal object Content(){return Type=="thinking"?J.Map("type",Type,"thinking",Text.ToString(),"signature",Signature.ToString()):J.Map("type",Type,"data",Data);}
        }
        internal Response(ModelProtocol.Response response){this.response=response;}
        void CheckThinkingSize(){if(thinking.Values.Sum(block=>block.Text.Length+block.Signature.Length+(block.Data??"").Length)>16*1024*1024)throw J.Error("Claude 思考内容超过桥接的 16 MiB 限制。");}
        void BeginThinking(int index,object value) {
            if(thinking.ContainsKey(index))throw J.Error("Claude 返回了重复的思考内容块。");
            var block=new ThinkingBlock {Type=J.Str(value,"type"),Data=J.Str(value,"data")};block.Text.Append(J.Str(value,"thinking"));block.Signature.Append(J.Str(value,"signature"));
            thinking.Add(index,block);CheckThinkingSize();response.BeginReasoning();
        }
        void Delta(object delta) {response.Push(J.Map("choices",new[]{J.Map("index",0,"delta",delta)}));}
        void Tool(int index,string id,string name,string arguments) {
            var function=J.Map("arguments",arguments);if(name!="")function["name"]=name;
            var call=J.Map("index",index,"function",function);if(id!="")call["id"]=id;
            Delta(J.Map("tool_calls",new[]{call}));
        }
        void Usage(object value) {foreach(var pair in J.Obj(value))usage[pair.Key]=pair.Value;}
        void Complete() {
            string finish=reason=="tool_use"?"tool_calls":reason=="max_tokens"||reason=="model_context_window_exceeded"?"length":reason=="refusal"?"content_filter":reason=="end_turn"||reason=="stop_sequence"?"stop":"";
            if(finish=="")throw J.Error("Claude 返回了不支持的结束原因："+reason+"。");
            if(thinking.Count>0)response.PreserveReasoning(ThinkingPrefix,thinking.Values.Select(block=>block.Content()).ToArray());
            double incoming=(J.Num(usage,"input_tokens")??0)+(J.Num(usage,"cache_read_input_tokens")??0)+(J.Num(usage,"cache_creation_input_tokens")??0),outgoing=J.Num(usage,"output_tokens")??0;
            var convertedUsage=J.Map("prompt_tokens",incoming,"completion_tokens",outgoing,"total_tokens",incoming+outgoing);
            if(J.Count(J.Get(usage,"cache_read_input_tokens")).HasValue)convertedUsage["prompt_cache_hit_tokens"]=J.Get(usage,"cache_read_input_tokens");
            if(J.Count(J.Get(usage,"cache_creation_input_tokens")).HasValue)convertedUsage["prompt_cache_creation_tokens"]=J.Get(usage,"cache_creation_input_tokens");
            response.Push(J.Map("choices",new[]{J.Map("index",0,"delta",J.Map(),"finish_reason",finish)},"usage",convertedUsage));
            ended=true;
        }
        internal void Push(object value,bool complete=false) {
            if(ended)throw J.Error("Claude 在消息结束后返回了额外数据。");
            string type=J.Str(value,"type");
            if(type=="error"||J.Get(value,"error")!=null)throw J.Error(J.Str(J.Get(value,"error"),"message","Claude 服务返回错误。"));
            if(complete) {
                if(type!="message")throw J.Error("Claude 接口未返回 Messages 响应。");
                int index=0;
                foreach(var block in J.Arr(J.Get(value,"content"))) {
                    string blockType=J.Str(block,"type");
                    if(blockType=="text")Delta(J.Map("content",J.Str(block,"text")));
                    else if(blockType=="tool_use")Tool(index,J.Str(block,"id"),J.Str(block,"name"),J.Encode(J.Get(block,"input",J.Map())));
                    else if(blockType=="thinking"||blockType=="redacted_thinking")BeginThinking(index,block);
                    else throw J.Error("Claude 返回了未启用的内容类型："+blockType+"。");
                    index++;
                }
                reason=J.Str(value,"stop_reason");Usage(J.Get(value,"usage"));Complete();return;
            }
            int blockIndex=(int)(J.Num(value,"index")??0);
            if(type=="message_start")Usage(J.Get(J.Get(value,"message"),"usage"));
            else if(type=="content_block_start") {
                var block=J.Get(value,"content_block");string blockType=J.Str(block,"type");
                if(blockType!="text"&&blockType!="tool_use"&&blockType!="thinking"&&blockType!="redacted_thinking")throw J.Error("Claude 返回了未启用的内容类型："+blockType+"。");
                if(blocks.ContainsKey(blockIndex))throw J.Error("Claude 返回了重复的内容块。");
                blocks[blockIndex]=blockType;
                if(blockType=="text")Delta(J.Map("content",J.Str(block,"text")));
                else if(blockType=="tool_use")Tool(blockIndex,J.Str(block,"id"),J.Str(block,"name"),"");
                else BeginThinking(blockIndex,block);
            }else if(type=="content_block_delta") {
                var delta=J.Get(value,"delta");string blockType;
                if(!blocks.TryGetValue(blockIndex,out blockType))throw J.Error("Claude 内容片段缺少开始事件。");
                if(blockType=="text"&&J.Str(delta,"type")=="text_delta")Delta(J.Map("content",J.Str(delta,"text")));
                else if(blockType=="tool_use"&&J.Str(delta,"type")=="input_json_delta")Tool(blockIndex,"","",J.Str(delta,"partial_json"));
                else if(blockType=="thinking"&&J.Str(delta,"type")=="thinking_delta"){thinking[blockIndex].Text.Append(J.Str(delta,"thinking"));CheckThinkingSize();}
                else if(blockType=="thinking"&&J.Str(delta,"type")=="signature_delta"){thinking[blockIndex].Signature.Append(J.Str(delta,"signature"));CheckThinkingSize();}
                else throw J.Error("Claude 返回了不支持的内容片段。");
            }else if(type=="content_block_stop") {
                if(!blocks.Remove(blockIndex))throw J.Error("Claude 内容结束事件没有对应的开始事件。");
            }else if(type=="message_delta"){reason=J.Str(J.Get(value,"delta"),"stop_reason");Usage(J.Get(value,"usage"));}
            else if(type=="message_stop") {
                if(blocks.Count>0)throw J.Error("Claude 消息包含未结束的内容块。");
                Complete();
            }else if(type!="ping")throw J.Error("Claude 返回了不支持的事件："+type+"。");
        }
        internal void Finish(){if(!ended)throw J.Error("Claude 连接提前结束，未收到 message_stop；请重试。");}
    }
}
}
