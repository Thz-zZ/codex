using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RelayLite {
internal sealed class ModelBridge:IDisposable {
    readonly string file;readonly Secure secure=new Secure();readonly object sync=new object();
    readonly Dictionary<string,Dictionary<string,object>> routes=new Dictionary<string,Dictionary<string,object>>();
    readonly Dictionary<string,object> latestUsage=new Dictionary<string,object>();
    readonly CancellationTokenSource shutdown=new CancellationTokenSource();readonly SemaphoreSlim slots=new SemaphoreSlim(8,8);
    readonly HttpClient http;TcpListener listener;int port;string token;bool disposed;
    internal ModelBridge(string directory) {
        file=Path.Combine(directory,"model-bridge.json");var saved=J.Read(file,J.Map());port=(int)(J.Num(saved,"port")??0);
        token=J.Str(saved,"tokenEncrypted")==""?NewToken():secure.Decrypt(J.Str(saved,"tokenEncrypted"));
        if(token.Length<32)throw J.Error("本地桥接凭据无效，请检查 model-bridge.json。");
        foreach(var route in J.Arr(J.Get(saved,"routes")))routes[J.Str(route,"path")]=J.Obj(route);
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        http=new HttpClient(new HttpClientHandler {AllowAutoRedirect=false}) {Timeout=Timeout.InfiniteTimeSpan};
    }
    static string NewToken(){var bytes=new byte[32];using(var random=RandomNumberGenerator.Create())random.GetBytes(bytes);return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');}
    internal bool IsProvider(string baseUrl,string key,string id) {
        if(key!=token)return false;
        lock(sync)return routes.Values.Any(route=>J.Str(route,"providerId")==id&&Address(J.Str(route,"path"))==baseUrl.TrimEnd('/'));
    }
    string Address(string path){return "http://127.0.0.1:"+port+path;}
    internal void Restore(object config) {
        string url=J.Str(config,"baseUrl");
        lock(sync)if(routes.Keys.Any(path=>Address(path)==url.TrimEnd('/')))Start();
    }
    void Start() {
        if(listener!=null)return;
        if(disposed)throw J.Error("本地模型桥接已停止。");
        if(port<0||port>65535)throw J.Error("本地模型桥接端口无效。");
        var pending=new TcpListener(IPAddress.Loopback,port);
        pending.Server.ExclusiveAddressUse=true;
        try{pending.Start(16);}catch(SocketException error){pending.Stop();throw J.Error("本地模型桥接端口无法启动，请关闭占用端口的程序后重试。"+error.SocketErrorCode);}
        listener=pending;port=((IPEndPoint)listener.LocalEndpoint).Port;
        Task.Run(()=>Accept());
    }
    internal object Prepare(object provider,string key) {
        lock(sync) {
            string model=J.Str(provider,"model").Trim();if(model=="")throw J.Error("本地模型桥接需要填写明确的模型名称。");
            string upstream=ModelProtocol.ApiRoot(J.Str(provider,"baseUrl"));
            if(routes.Keys.Any(path=>Address(path)==upstream))throw J.Error("上游 API 地址不能指向本地桥接自身。");
            var route=J.Map("providerId",J.Str(provider,"id"),"protocol",ModelProtocol.Protocol(provider),"upstream",upstream,"model",model,"reasoning",J.Str(provider,"reasoning"),"keyEncrypted",secure.Encrypt(key),"at",J.Now());
            if(J.Str(provider,"modelSelection")=="catalog"){route["models"]=Models.Saved(provider);route["modelSelection"]="catalog";}
            Start();string path="/relay/"+J.Str(provider,"id")+"/"+J.Str(provider,"revision")+"/v1";route["path"]=path;
            routes[path]=route;
            var retained=routes.Values.OrderByDescending(route=>J.Str(route,"at")).Take(16).ToArray();
            J.Atomic(file,J.Encode(J.Map("port",port,"tokenEncrypted",secure.Encrypt(token),"routes",retained)));
            var paths=new HashSet<string>(retained.Select(route=>J.Str(route,"path")));foreach(string obsolete in routes.Keys.Where(value=>!paths.Contains(value)).ToArray())routes.Remove(obsolete);
            return J.Map("baseUrl",Address(path),"key",token);
        }
    }
    internal void Forget(string id) {
        lock(sync) {
            latestUsage.Remove(id);
            foreach(string path in routes.Keys.Where(path=>J.Str(routes[path],"providerId")==id).ToArray())routes.Remove(path);
            if(File.Exists(file))J.Atomic(file,J.Encode(J.Map("port",port,"tokenEncrypted",secure.Encrypt(token),"routes",routes.Values.ToArray())));
        }
    }
    internal object LastUsage(string id){lock(sync){object value;return latestUsage.TryGetValue(id,out value)?J.Clone(value):null;}}
    async Task Accept() {
        while(!shutdown.IsCancellationRequested) {
            TcpClient client=null;
            try {
                client=await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                if(!slots.Wait(0)){client.Close();continue;}
                var accepted=client;_ = Task.Run(async()=>{try{await Handle(accepted).ConfigureAwait(false);}catch{}finally{slots.Release();}});
            }catch(ObjectDisposedException){break;}catch(SocketException){if(shutdown.IsCancellationRequested)break;if(client!=null)client.Close();}
        }
    }
    sealed class Reader {
        readonly Stream stream;readonly CancellationToken cancellation;readonly byte[] buffer=new byte[8192];int position,count;
        internal Reader(Stream stream,CancellationToken cancellation){this.stream=stream;this.cancellation=cancellation;}
        async Task<int> Byte() {
            if(position==count){count=await stream.ReadAsync(buffer,0,buffer.Length,cancellation).ConfigureAwait(false);position=0;if(count==0)return -1;}
            return buffer[position++];
        }
        internal async Task<string> Line() {
            var bytes=new List<byte>();
            while(true){int value=await Byte().ConfigureAwait(false);if(value<0)throw new IOException("请求连接提前结束。");if(value==10)break;if(bytes.Count>=8192)throw J.Error("请求头或分块行过长。");bytes.Add((byte)value);}
            if(bytes.Count>0&&bytes[bytes.Count-1]==13)bytes.RemoveAt(bytes.Count-1);return Encoding.ASCII.GetString(bytes.ToArray());
        }
        async Task Copy(Stream output,int length) {
            int remaining=length;
            while(remaining>0) {
                if(position==count){count=await stream.ReadAsync(buffer,0,buffer.Length,cancellation).ConfigureAwait(false);position=0;if(count==0)throw new IOException("请求正文不完整。");}
                int size=Math.Min(remaining,count-position);output.Write(buffer,position,size);position+=size;remaining-=size;
            }
        }
        internal async Task<string> Body(Dictionary<string,string> headers) {
            const int limit=16*1024*1024;string transfer;bool chunked=headers.TryGetValue("Transfer-Encoding",out transfer);
            if(chunked&&headers.ContainsKey("Content-Length"))throw J.Error("请求不能同时使用 Content-Length 和分块编码。");
            using(var output=new MemoryStream()) {
                if(chunked) {
                    if(!string.Equals(transfer,"chunked",StringComparison.OrdinalIgnoreCase))throw J.Error("不支持该传输编码。");
                    while(true) {
                        string line=await Line().ConfigureAwait(false);int length;
                        if(!int.TryParse(line.Split(';')[0],System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out length)||length<0||length>limit-output.Length)throw J.Error("请求分块无效或超过 16 MiB。");
                        if(length==0){int trailerSize=0;while((line=await Line().ConfigureAwait(false))!=""){trailerSize+=line.Length;if(trailerSize>65536)throw J.Error("请求尾部过长。");}break;}
                        await Copy(output,length).ConfigureAwait(false);if(await Line().ConfigureAwait(false)!="")throw J.Error("请求分块缺少结束标记。");
                    }
                }else {
                    string value;int length;
                    if(!headers.TryGetValue("Content-Length",out value)||!int.TryParse(value,out length)||length<0||length>limit)throw J.Error("请求需要不超过 16 MiB 的 Content-Length。");
                    await Copy(output,length).ConfigureAwait(false);
                }
                return new UTF8Encoding(false,true).GetString(output.ToArray());
            }
        }
    }
    static async Task Headers(Stream stream,int status,string contentType,int? length=null) {
        string reason=status==200?"OK":status==400?"Bad Request":status==401?"Unauthorized":status==404?"Not Found":status==405?"Method Not Allowed":status==504?"Gateway Timeout":"Bad Gateway";
        string framing=length.HasValue?"Content-Length: "+length.Value+"\r\n":"Transfer-Encoding: chunked\r\n";
        byte[] bytes=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+" "+reason+"\r\nContent-Type: "+contentType+"\r\n"+framing+"Connection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n\r\n");
        await stream.WriteAsync(bytes,0,bytes.Length).ConfigureAwait(false);
    }
    static async Task Json(Stream stream,int status,object body) {
        byte[] bytes=Encoding.UTF8.GetBytes(J.Encode(body));await Headers(stream,status,"application/json; charset=utf-8",bytes.Length).ConfigureAwait(false);await stream.WriteAsync(bytes,0,bytes.Length).ConfigureAwait(false);
    }
    static void WriteChunk(Stream stream,string text) {
        byte[] bytes=Encoding.UTF8.GetBytes(text),header=Encoding.ASCII.GetBytes(bytes.Length.ToString("x")+"\r\n"),ending=Encoding.ASCII.GetBytes("\r\n");
        stream.Write(header,0,header.Length);stream.Write(bytes,0,bytes.Length);stream.Write(ending,0,ending.Length);stream.Flush();
    }
    static void EndChunks(Stream stream){byte[] ending=Encoding.ASCII.GetBytes("0\r\n\r\n");stream.Write(ending,0,ending.Length);stream.Flush();}
    static object Error(string message){return J.Map("error",J.Map("message",message,"type","bridge_error","code","bridge_error"));}
    async Task Handle(TcpClient client) {
        using(client)using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token)) {
            deadline.CancelAfter(TimeSpan.FromMinutes(10));using(deadline.Token.Register(()=>client.Close())) {
                var stream=client.GetStream();bool started=false;ModelProtocol.Response response=null;string upstreamKey="";
                try {
                    var reader=new Reader(stream,deadline.Token);string[] first=(await reader.Line().ConfigureAwait(false)).Split(' ');
                    if(first.Length!=3||first[2]!="HTTP/1.1"){await Json(stream,400,Error("需要 HTTP/1.1 请求。")).ConfigureAwait(false);return;}
                    var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);string line;int size=0;
                    while((line=await reader.Line().ConfigureAwait(false))!="") {
                        size+=line.Length;if(size>65536)throw J.Error("请求头超过 64 KiB。");int split=line.IndexOf(':');if(split<1)throw J.Error("请求头格式无效。");
                        string name=line.Substring(0,split);if(headers.ContainsKey(name))throw J.Error("请求头重复："+name);headers[name]=line.Substring(split+1).Trim();
                    }
                    string authorization;
                    if(headers.ContainsKey("Origin")||!headers.TryGetValue("Authorization",out authorization)||authorization!="Bearer "+token){await Json(stream,401,Error("本地桥接凭据无效。")).ConfigureAwait(false);return;}
                    if(first[0]!="POST"){await Json(stream,405,Error("本地桥接只接受 POST。")).ConfigureAwait(false);return;}
                    string path=first[1].Split('?')[0],suffix="/responses";Dictionary<string,object> route=null;
                    lock(sync)if(path.EndsWith(suffix,StringComparison.Ordinal))routes.TryGetValue(path.Substring(0,path.Length-suffix.Length),out route);
                    if(route==null){await Json(stream,404,Error("本地桥接路由不存在，或请求了不支持的接口。")).ConfigureAwait(false);return;}
                    if(headers.ContainsKey("Content-Encoding")&&!string.Equals(headers["Content-Encoding"],"identity",StringComparison.OrdinalIgnoreCase))throw J.Error("本地桥接需要未压缩的 JSON 请求。");
                    var input=J.Obj(J.Decode(await reader.Body(headers).ConfigureAwait(false)));input["model"]=ModelProtocol.SelectedModel(input,route);
                    string protocol=J.Str(route,"protocol","chat-completions");bool claude=protocol=="anthropic";
                    var converted=claude?ClaudeProtocol.Request(input,route):ModelProtocol.Request(input,route);
                    upstreamKey=secure.Decrypt(J.Str(route,"keyEncrypted"));
                    using(var request=new HttpRequestMessage(HttpMethod.Post,J.Str(route,"upstream")+(claude?"/messages":"/chat/completions"))) {
                        ModelProtocol.Authenticate(request,protocol,upstreamKey);
                        request.Content=new StringContent(J.Encode(converted),Encoding.UTF8,"application/json");
                        _ = Task.Run(async()=>{try{var probe=new byte[1];if(await stream.ReadAsync(probe,0,1,deadline.Token).ConfigureAwait(false)==0)deadline.Cancel();}catch{} });
                        using(var upstream=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false))using(deadline.Token.Register(()=>upstream.Dispose())) {
                            if(!upstream.IsSuccessStatusCode) {
                                string error=Usage.Sanitize(await ReadBounded(upstream.Content,deadline.Token).ConfigureAwait(false),upstreamKey,token);
                                string message=Usage.Sanitize("模型服务 HTTP "+(int)upstream.StatusCode+"（模型："+J.Str(converted,"model")+"；接口："+(claude?"Claude Messages":"Chat Completions")+"）："+error,upstreamKey,token);
                                await Json(stream,(int)upstream.StatusCode,Error(message)).ConfigureAwait(false);return;
                            }
                            bool streaming=J.True(J.Get(input,"stream"));
                            if(streaming){await Headers(stream,200,"text/event-stream; charset=utf-8").ConfigureAwait(false);started=true;}
                            Action<object> emit=streaming?(Action<object>)(value=>WriteChunk(stream,"event: "+J.Str(value,"type")+"\ndata: "+J.Encode(value)+"\n\n")):null;
                            response=new ModelProtocol.Response(input,emit);response.Start();
                            var claudeResponse=claude?new ClaudeProtocol.Response(response):null;
                            Action<object,bool> push=(value,complete)=>{if(claude)claudeResponse.Push(value,complete);else response.Push(value,complete);};
                            string media=upstream.Content.Headers.ContentType==null?"":upstream.Content.Headers.ContentType.MediaType;
                            if(media.Equals("text/event-stream",StringComparison.OrdinalIgnoreCase)) {
                                using(var source=await upstream.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var events=new StreamReader(source,Encoding.UTF8)) {
                                    var data=new StringBuilder();int consumed=0;
                                    while((line=await events.ReadLineAsync().ConfigureAwait(false))!=null) {
                                        deadline.Token.ThrowIfCancellationRequested();consumed+=line.Length;if(consumed>32*1024*1024)throw J.Error("模型事件流超过 32 MiB。");
                                        if(line=="") {
                                            string payload=data.ToString();data.Clear();if(payload=="[DONE]")break;if(payload!="")push(J.Decode(payload),false);
                                        }else if(line.StartsWith("data:",StringComparison.Ordinal)){if(data.Length>0)data.Append('\n');data.Append(line.Substring(5).TrimStart(' '));}
                                    }
                                    if(data.Length>0&&data.ToString()!="[DONE]")push(J.Decode(data.ToString()),false);
                                }
                            }else push(J.Decode(await ReadBounded(upstream.Content,deadline.Token).ConfigureAwait(false)),true);
                            if(claude)claudeResponse.Finish();
                            var result=response.Finish();
                            lock(sync)if(routes.ContainsKey(J.Str(route,"path")))latestUsage[J.Str(route,"providerId")]=J.Map("at",J.Now(),"model",J.Str(converted,"model"),"reasoning",J.Str(route,"reasoning"),"protocol",protocol,"status",J.Str(result,"status"),"cache",response.CacheUsage);
                            if(streaming)EndChunks(stream);else await Json(stream,200,result).ConfigureAwait(false);
                        }
                    }
                }catch(Exception error) {
                    string message=Usage.ErrorText(error,upstreamKey,token);
                    try{if(started&&response!=null){response.Fail(message);EndChunks(stream);}else await Json(stream,error is OperationCanceledException?504:error is HttpRequestException?502:400,Error(message)).ConfigureAwait(false);}catch{}
                }finally{deadline.Cancel();}
            }
        }
    }
    static async Task<string> ReadBounded(HttpContent content,CancellationToken cancellation) {
        using(var source=await content.ReadAsStreamAsync().ConfigureAwait(false))using(var result=new MemoryStream()) {
            var buffer=new byte[8192];int count;
            while((count=await source.ReadAsync(buffer,0,buffer.Length,cancellation).ConfigureAwait(false))>0){if(result.Length+count>16*1024*1024)throw J.Error("模型响应超过 16 MiB。");result.Write(buffer,0,count);}
            return Encoding.UTF8.GetString(result.ToArray());
        }
    }
    public void Dispose(){lock(sync){if(disposed)return;disposed=true;shutdown.Cancel();if(listener!=null)listener.Stop();http.Dispose();}}
}
}
