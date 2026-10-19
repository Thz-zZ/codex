using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace RelayLite {
internal sealed class Store {
    public readonly string Dir, File;
    public string ConfigDir {get {return J.Str(Data,"configDir");}}
    public readonly Secure Secure;
    public Dictionary<string,object> Data;
    public Store(string dir,string configDir,Secure secure) {
        Dir=dir;Secure=secure;File=Path.Combine(dir,"providers.json");
        Data=J.Obj(J.Read(File,J.Map("version",1,"providers",new List<object>(),"history",new List<object>(),"snapshots",new List<object>(),"appSettings",J.Map("launchAtLogin",false,"silentStartup",false),"configDir",configDir)));
        if(J.Str(Data,"configDir")=="")Data["configDir"]=configDir;
        if(J.Get(Data,"appSettings")==null)Data["appSettings"]=J.Map("launchAtLogin",false,"silentStartup",false);
        foreach(var p in Providers)foreach(string key in new[]{"keyEncrypted","accountEncrypted"}){var value=J.Str(p,key);if(value.StartsWith("v10",StringComparison.Ordinal)||value.Length>0&&!value.StartsWith("dpapi1:",StringComparison.Ordinal)){p[key]=Secure.Encrypt(Secure.Decrypt(value));Save();}}
    }
    List<Dictionary<string,object>> Providers {get{return J.Arr(J.Get(Data,"providers")).Select(J.Obj).ToList();}}
    List<Dictionary<string,object>> History {get{return J.Arr(J.Get(Data,"history")).Select(J.Obj).ToList();}}
    List<Dictionary<string,object>> SnapshotsData {get{return J.Arr(J.Get(Data,"snapshots")).Select(J.Obj).ToList();}}
    public void Save(){J.Atomic(File,J.Encode(Data));}
    public bool AutoRefresh {get{return !J.Has(J.Get(Data,"appSettings"),"autoRefresh")||J.True(J.Get(J.Get(Data,"appSettings"),"autoRefresh"));}}
    public void SetAppSetting(string key,bool value){if(key!="launchAtLogin"&&key!="silentStartup"&&key!="autoCheckUpdates"&&key!="autoRefresh")throw J.Error("未知应用设置。");var settings=J.Obj(J.Get(Data,"appSettings"));settings[key]=value;if(key=="launchAtLogin"&&!value)settings["silentStartup"]=false;Save();}
    Dictionary<string,object> Find(string id){var p=Providers.FirstOrDefault(x=>J.Str(x,"id")==id);if(p==null)throw J.Error("中转不存在。");return p;}
    string Key(object p,string name="keyEncrypted"){return Secure.Decrypt(J.Str(p,name));}
    Dictionary<string,object> Pub(Dictionary<string,object> p){var x=new Dictionary<string,object>(p);x.Remove("keyEncrypted");x.Remove("accountEncrypted");x.Remove("bridgeUsage");x["hasKey"]=J.Str(p,"keyEncrypted")!="";x["hasAccountKey"]=J.Str(p,"accountEncrypted")!="";x["requiresCodexSetup"]=!ModelProtocol.IsCodex(p);return x;}
    public object Details(string id){var p=Find(id);var x=Pub(p);x["key"]=Key(p);x["accountKey"]=Key(p,"accountEncrypted");return x;}
    Dictionary<string,object> ReadConfig(){string file=Path.Combine(ConfigDir,"config.toml");return System.IO.File.Exists(file)?ParseToml(System.IO.File.ReadAllText(file)):null;}
    string ActiveId(object doc,object info,List<Dictionary<string,object>> providers){
        string currentKey=J.Str(J.Get(J.Get(doc,"model_providers"),J.Str(info,"provider")),"experimental_bearer_token"),saved=J.Str(Data,"activeId"),first="";
        foreach(var p in providers){
            string id=J.Str(p,"id");
            if(!SameBaseUrl(J.Str(info,"baseUrl"),J.Str(p,"baseUrl"))||!(currentKey!=""?Key(p)==currentKey:(J.Str(info,"provider")=="codex_relay"||J.Str(info,"provider")=="custom")&&saved==id))continue;
            if(id==saved)return id;if(first=="")first=id;
        }
        return first;
    }
    internal string ActiveProviderId(){var doc=ReadConfig();return ActiveId(doc,ConfigInfo(doc),Providers);}
    internal IEnumerable<string> ProviderIds {get{return Providers.Select(p=>J.Str(p,"id"));}}
    public object List(){var doc=ReadConfig();var info=ConfigInfo(doc);var providers=Providers;string active=ActiveId(doc,info,providers);
        return J.Map("providers",providers.Select(p=>{var x=Pub(p);x["active"]=J.Str(p,"id")==active;return x;}).ToList(),"usageCache",UsageCache.Load(this),"appSettings",J.Get(Data,"appSettings"),"config",info,"history",History.Take(100).Cast<object>().ToList(),"dataDir",Dir);}
    static bool SameBaseUrl(string left,string right){try{return BaseUrl(left)==BaseUrl(right);}catch{return left==right;}}
    public object ConfigInfo(){return ConfigInfo(ReadConfig());}
    object ConfigInfo(object doc){if(doc==null)return J.Map("exists",false,"dir",ConfigDir,"model","","reasoning","","provider","","baseUrl","");string id=J.Str(doc,"model_provider","openai");var providers=J.Obj(J.Get(doc,"model_providers"));var p=J.Obj(J.Get(providers,id));return J.Map("exists",true,"dir",ConfigDir,"provider",id,"model",J.Str(doc,"model"),"reasoning",J.Str(doc,"model_reasoning_effort"),"baseUrl",J.Str(p,"base_url"),"hasKey",J.Str(p,"experimental_bearer_token")!="");}
    static string NormalizeToml(string source){var lines=source.Split(new[]{"\r\n","\n"},StringSplitOptions.None).ToList();for(int i=0;i<lines.Count;i++){if(!Regex.IsMatch(lines[i],@"^\s*\[\s*model_providers\s*\]\s*(?:#.*)?$"))continue;bool implicitParent=lines.Take(i).Any(line=>Regex.IsMatch(line,@"^\s*\[\[?\s*model_providers\s*\."));if(!implicitParent)continue;bool empty=true;for(int j=i+1;j<lines.Count&&!Regex.IsMatch(lines[j],@"^\s*\[\[?");j++)if(!Regex.IsMatch(lines[j],@"^\s*(?:#.*)?$")){empty=false;break;}if(empty){lines.RemoveAt(i);i--;}}return string.Join(source.Contains("\r\n")?"\r\n":"\n",lines);}
    static Dictionary<string,object> ParseToml(string source){try{var table=Nett.Toml.ReadString(NormalizeToml(source));return table.ToDictionary();}catch{throw J.Error("Codex 的 config.toml 格式无效；请先修复，或在设置中选择正确目录。");}}
    internal static string BaseUrl(string raw){var s=(raw??"").Trim();if(s=="")throw J.Error("请填写中转地址。");if(!s.Contains("://"))s="https://"+s;Uri u;if(!Uri.TryCreate(s,UriKind.Absolute,out u)||(u.Scheme!="https"&&!(u.Scheme=="http"&&(u.Host=="localhost"||u.Host=="127.0.0.1"))))throw J.Error("中转地址格式不正确，请使用 HTTPS 地址。");if(u.UserInfo!=""||u.Query!=""||u.Fragment!="")throw J.Error("地址请勿包含账号、查询参数或片段。");string path=u.AbsolutePath.TrimEnd('/');foreach(string suffix in new[]{"/api/log/token","/v1/usage","/v1"})if(path.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))path=path.Substring(0,path.Length-suffix.Length);return u.GetLeftPart(UriPartial.Authority)+path;}
    public object Upsert(object input,bool requireSelections=false,bool requireDiscovery=false){
        var id=J.Str(input,"id");var old=id==""?null:Find(id);string name=J.Str(input,"name").Trim();if(name.Length<1||name.Length>80)throw J.Error("名称需为 1–80 个字符。");
        string protocol=ModelProtocol.Protocol(input),model=J.Str(input,"model").Trim(),reasoning=J.Str(input,"reasoning").Trim();
        if(requireSelections){
            if(model=="")throw J.Error("请获取模型列表并选择默认模型。");
            if(reasoning=="")throw J.Error("请选择推理强度；不指定参数时请手动选择“服务默认”。");
            if(!new[]{"default","low","medium","high","xhigh","max","ultra"}.Contains(reasoning))throw J.Error("请选择有效的推理强度。");
        }
        string baseUrl=BaseUrl(J.Str(input,"baseUrl")),siteRaw=J.Str(input,"siteUrl").Trim(),site=BaseUrl(siteRaw==""?baseUrl:siteRaw),adapter=J.Str(input,"adapter","auto");
        ModelProtocol.ValidateAddress(baseUrl);
        if(!new[]{"auto","newapi","sub2api","none"}.Contains(adapter))throw J.Error("请选择自动识别、NewAPI、Sub2API 或不查询统计。");
        double? divisor=J.N(J.Get(input,"quotaPerUnit"))??500000;if(divisor<=0)throw J.Error("额度换算值必须大于 0。");
        string key=J.Str(input,"key").Trim(),account=J.Str(input,"accountKey").Trim();
        if(key.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))key=key.Substring(7).Trim();if(account.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))account=account.Substring(7).Trim();
        if(key.IndexOfAny(new[]{'\r','\n'})>=0||account.IndexOfAny(new[]{'\r','\n'})>=0||key.Length>4096||account.Length>16384)throw J.Error("密钥格式无效。");
        if(key==""&&old==null)throw J.Error("请填写 API 调用密钥。");
        List<string> models=null;
        if(requireDiscovery){
            string effectiveKey=key!=""?key:Key(old),discovery=J.Str(input,"discoveryToken");
            if(discovery!="")models=Models.Resolve(discovery,input,effectiveKey);
            else if(ModelProtocol.IsCodex(old)&&J.Str(old,"modelSelection")=="catalog"&&Models.Fingerprint(old,Key(old))==Models.Fingerprint(input,effectiveKey))models=Models.Saved(old);
            else throw J.Error("请先获取中转模型列表，再选择默认模型和推理强度。");
            if(!models.Contains(model))throw J.Error("默认模型必须从获取到的模型列表中选择，请重新选择或获取列表。");
        }else if(J.Str(input,"modelSelection")=="catalog")models=Models.Saved(input);
        var p=old==null?new Dictionary<string,object>():new Dictionary<string,object>(old);p["id"]=id==""?Guid.NewGuid().ToString():id;p["name"]=name;p["baseUrl"]=baseUrl;p["siteUrl"]=site;p["adapter"]=adapter;p["protocol"]=protocol;p["model"]=model;p["reasoning"]=reasoning;p["accountUserId"]=J.Str(input,"accountUserId").Trim();p["quotaPerUnit"]=divisor.Value;p["notes"]=J.Str(input,"notes");
        if(models!=null){p["models"]=models;p["modelSelection"]="catalog";}else {p.Remove("models");p.Remove("modelSelection");}
        p["modelFamily"]="codex";p.Remove("bridgeUsage");
        if(key!="")p["keyEncrypted"]=Secure.Encrypt(key);if(J.True(J.Get(input,"clearAccount")))p["accountEncrypted"]="";else if(account!="")p["accountEncrypted"]=Secure.Encrypt(account);
        if(old==null||J.Str(old,"siteUrl")!=site||J.Str(old,"adapter")!=adapter||key!=""&&key!=Key(old)){p.Remove("detectedAdapter");p.Remove("detectedAt");}
        p["updatedAt"]=J.Now();p["revision"]=Guid.NewGuid().ToString();var list=J.Arr(J.Get(Data,"providers"));if(old==null)list.Add(p);else list[list.IndexOf(old)]=p;Data["providers"]=list;Save();return Pub(p);
    }
    public void Delete(string id){if(J.Str(Data,"activeId")==id)throw J.Error("请先切换到其他中转，再删除当前中转。");var p=Find(id);Data["providers"]=Providers.Where(x=>!ReferenceEquals(x,p)).Cast<object>().ToList();Data["snapshots"]=SnapshotsData.Where(x=>J.Str(x,"id")!=id).Cast<object>().ToList();Save();}
    public object Reorder(object ids){var input=J.Arr(ids).Select(J.S).ToList();var old=Providers;if(input.Count!=old.Count||input.Distinct().Count()!=input.Count||input.Any(id=>!old.Any(p=>J.Str(p,"id")==id)))throw J.Error("中转列表已变化，请刷新后重试。");Data["providers"]=input.Select(id=>old.First(p=>J.Str(p,"id")==id)).Cast<object>().ToList();Save();return List();}
    static void WriteConfig(string dir,string body){string file=Path.Combine(dir,"config.toml"),tmp=file+"."+Guid.NewGuid().ToString("N")+".tmp";Directory.CreateDirectory(dir);System.IO.File.WriteAllText(tmp,body,new System.Text.UTF8Encoding(false));if(System.IO.File.Exists(file))System.IO.File.Replace(tmp,file,null);else System.IO.File.Move(tmp,file);}
    static readonly string[] RootKeys={"model_provider","model","model_reasoning_effort","model_catalog_json"};
    static string Patch(string source,object p,string key){
        source=NormalizeToml(source);var doc=ParseToml(source==""?"# Codex configuration\n":source);
        string provider=J.Str(doc,"model_provider");if(provider==""||provider=="openai")provider="custom";
        string nl=source.Contains("\r\n")?"\r\n":"\n";var lines=source.Split(new[]{"\r\n","\n"},StringSplitOptions.None).ToList();
        int boundary=lines.FindIndex(line=>Regex.IsMatch(line,@"^\s*\["));if(boundary<0)boundary=lines.Count;
        var replacements=new Dictionary<string,string>{{"model_provider",J.Encode(provider)}};
        if(J.Str(p,"model")!="")replacements["model"]=J.Encode(J.Str(p,"model"));
        if(J.Str(p,"modelCatalogPath")!="")replacements["model_catalog_json"]=J.Encode(J.Str(p,"modelCatalogPath"));
        string reasoning=J.Str(p,"reasoning");
        if(reasoning!=""&&reasoning!="default")replacements["model_reasoning_effort"]=J.Encode(reasoning);
        if(reasoning=="default"){
            for(int index=boundary-1;index>=0;index--)if(Regex.IsMatch(lines[index],@"^\s*model_reasoning_effort\s*=")){lines.RemoveAt(index);boundary--;}
        }
        var found=new HashSet<string>();for(int i=0;i<boundary;i++){var m=Regex.Match(lines[i],@"^\s*([\w-]+)\s*=");if(m.Success&&replacements.ContainsKey(m.Groups[1].Value)){string field=m.Groups[1].Value;lines[i]=field+" = "+replacements[field];found.Add(field);}}
        lines.InsertRange(boundary,replacements.Where(x=>!found.Contains(x.Key)).Select(x=>x.Key+" = "+x.Value));
        var names=new List<string>{Regex.Escape(J.Encode(provider)),Regex.Escape("'"+provider+"'")};
        bool bare=Regex.IsMatch(provider,@"^[a-zA-Z0-9_-]+$");if(bare)names.Add(Regex.Escape(provider));
        string header=@"^\s*\[\s*(?:model_providers|""model_providers""|'model_providers')\s*\.\s*(?:"+string.Join("|",names)+@")\s*\]\s*(?:#.*)?$";
        int start=lines.FindIndex(line=>Regex.IsMatch(line,header));
        if(start>=0){int end=start+1;while(end<lines.Count&&!Regex.IsMatch(lines[end],@"^\s*\["))end++;lines.RemoveRange(start,end-start);}
        lines.AddRange(new[]{"","[model_providers."+(bare?provider:J.Encode(provider))+"]","name = "+J.Encode(J.Str(p,"name")),"base_url = "+J.Encode(J.Str(p,"baseUrl")),"wire_api = \"responses\"","requires_openai_auth = false","experimental_bearer_token = "+J.Encode(key),""});
        string output=string.Join(nl,lines);var parsed=ParseToml(output);var entry=J.Obj(J.Get(J.Get(parsed,"model_providers"),provider));
        if(J.Str(parsed,"model_provider")!=provider||J.Str(entry,"experimental_bearer_token")!=key||J.Str(entry,"base_url")!=J.Str(p,"baseUrl"))throw J.Error("无法安全更新当前 TOML 配置。");
        return output;
    }
    public object Switch(string id){
        var p=Find(id);ModelProtocol.Protocol(p);ModelProtocol.ValidateAddress(J.Str(p,"baseUrl"));string file=Path.Combine(ConfigDir,"config.toml");if(System.IO.File.Exists(file)&&new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint))throw J.Error("配置文件是链接，请先检查配置目录。");
        bool exists=System.IO.File.Exists(file);string original=exists?System.IO.File.ReadAllText(file):"",key=Key(p);object applied=p;
        if(J.Str(p,"model")!=""){var catalogued=new Dictionary<string,object>(J.Obj(applied));catalogued["modelCatalogPath"]=ModelCatalog.Write(Dir,p);applied=catalogued;}
        string next=Patch(original,applied,key),backup=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+"-"+Guid.NewGuid().ToString("N").Substring(0,8);string backupDir=Path.Combine(Dir,"backups");Directory.CreateDirectory(backupDir);
        J.Atomic(Path.Combine(backupDir,backup+".json"),J.Encode(J.Map("id",backup,"at",J.Now(),"name",J.Str(p,"name"),"providerId",id,"existed",exists,"configDir",ConfigDir,"contentEncrypted",Secure.Encrypt(original))));WriteConfig(ConfigDir,next);Data["activeId"]=id;Data["providers"]=Providers.OrderBy(x=>J.Str(x,"id")==id?0:1).Cast<object>().ToList();var history=J.Arr(J.Get(Data,"history"));history.Insert(0,J.Map("id",backup,"at",J.Now(),"name",J.Str(p,"name"),"action","switch"));Data["history"]=history.Take(100).ToList();Save();return List();
    }
    public object Restore(string id){if(id==""||id.Any(c=>!char.IsDigit(c)&&c!='-'&&!(c>='a'&&c<='f')))throw J.Error("备份 ID 无效。");var b=J.Obj(J.Read(Path.Combine(Dir,"backups",id+".json"),null));if(b.Count==0)throw J.Error("备份不存在。");if(J.Str(b,"configDir")!=ConfigDir)throw J.Error("备份属于另一个配置目录。");string file=Path.Combine(ConfigDir,"config.toml"),old=System.IO.File.Exists(file)?System.IO.File.ReadAllText(file):"",previous=Secure.Decrypt(J.Str(b,"contentEncrypted"));if(J.True(J.Get(b,"existed")))WriteConfig(ConfigDir,previous);else if(System.IO.File.Exists(file))System.IO.File.Delete(file);string recovery=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+"-"+Guid.NewGuid().ToString("N").Substring(0,8);J.Atomic(Path.Combine(Dir,"backups",recovery+".json"),J.Encode(J.Map("id",recovery,"at",J.Now(),"name","恢复前配置","existed",old!="","configDir",ConfigDir,"contentEncrypted",Secure.Encrypt(old))));Data["activeId"]=null;var h=J.Arr(J.Get(Data,"history"));h.Insert(0,J.Map("id",recovery,"at",J.Now(),"name",J.Str(b,"name"),"action","restore"));Data["history"]=h.Take(100).ToList();Save();return List();}
    public object Import(){var doc=ParseToml(System.IO.File.ReadAllText(Path.Combine(ConfigDir,"config.toml")));string name=J.Str(doc,"model_provider","openai");var providers=J.Obj(J.Get(doc,"model_providers"));var p=J.Obj(J.Get(providers,name));string baseUrl=J.Str(p,"base_url"),key=J.Str(p,"experimental_bearer_token");if(baseUrl==""||key=="")throw J.Error("当前配置没有可导入的中转密钥，请手动添加。");ModelProtocol.Protocol(J.Map("protocol",J.Str(p,"wire_api","responses")));ModelProtocol.ValidateAddress(baseUrl);if(Providers.Any(x=>SameBaseUrl(J.Str(x,"baseUrl"),baseUrl)))throw J.Error("列表中已存在这个地址，请直接编辑。");return J.Map("name",J.Str(p,"name",name),"baseUrl",baseUrl,"siteUrl",baseUrl,"key",key,"adapter","auto","modelFamily","codex","protocol","responses","model",J.Str(doc,"model"),"reasoning",J.Str(doc,"model_reasoning_effort"));}
    public object Snapshots(string id){Find(id);return SnapshotsData.Where(s=>J.Str(s,"id")==id).Take(200).Cast<object>().ToList();}
    public void Record(string id,object snapshot){var list=J.Arr(J.Get(Data,"snapshots"));var x=J.Obj(J.Clone(snapshot));x["id"]=id;list.Insert(0,x);Data["snapshots"]=list.Take(1000).ToList();Save();}
    public string Secret(string id,string field="keyEncrypted"){return Key(Find(id),field);}
    public int JCount(){return Providers.Count;}
    public void SetDetection(string id,string revision,string adapter,bool save=true){var p=Find(id);if(J.Str(p,"revision")!=revision)return;p["detectedAdapter"]=adapter;p["detectedAt"]=J.Now();if(save)Save();}
    public void RequireRevision(string id,string revision){if(J.Str(Find(id),"revision")!=revision)throw J.Error("中转已修改或删除，旧查询结果已丢弃，请重新刷新。");}
}
}
