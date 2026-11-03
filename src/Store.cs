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
    public bool CatalogUpgraded {get;private set;}
    public Store(string dir,string configDir,Secure secure) {
        Dir=dir;Secure=secure;File=Path.Combine(dir,"providers.json");
        Data=J.Obj(J.Read(File,J.Map("version",1,"providers",new List<object>(),"history",new List<object>(),"snapshots",new List<object>(),"appSettings",J.Map("launchAtLogin",false,"silentStartup",false),"configDir",configDir)));
        if(J.Str(Data,"configDir")=="")Data["configDir"]=configDir;
        if(J.Get(Data,"appSettings")==null)Data["appSettings"]=J.Map("launchAtLogin",false,"silentStartup",false);
        bool changed=J.Obj(J.Get(Data,"appSettings")).Remove("autoRefresh");
        foreach(var p in Providers)if(ModelProtocol.IsCodex(p)&&!J.Has(p,"apiPathVersion")){
            // Previous versions stored the site root, while discovery always
            // appended /v1. Recover that API path without changing live config.
            string previous=J.Str(p,"baseUrl"),root=BaseUrl(previous);p["baseUrl"]=root.EndsWith("/v1",StringComparison.OrdinalIgnoreCase)?root:root+"/v1";p["legacyBaseUrl"]=previous;p["apiPathVersion"]=1;changed=true;
        }
        foreach(var p in Providers)if(!new[]{"newapi","sub2api","none"}.Contains(J.Str(p,"adapter"))){p["adapter"]="none";p.Remove("detectedAdapter");p.Remove("detectedAt");p["revision"]=Guid.NewGuid().ToString();changed=true;}
        if(changed)Save();
        foreach(var p in Providers)foreach(string key in new[]{"keyEncrypted","accountEncrypted"}){var value=J.Str(p,key);if(value.StartsWith("v10",StringComparison.Ordinal)||value.Length>0&&!value.StartsWith("dpapi1:",StringComparison.Ordinal)){p[key]=Secure.Encrypt(Secure.Decrypt(value));Save();}}
    }
    List<Dictionary<string,object>> Providers {get{return J.Arr(J.Get(Data,"providers")).Select(J.Obj).ToList();}}
    List<Dictionary<string,object>> History {get{return J.Arr(J.Get(Data,"history")).Select(J.Obj).ToList();}}
    List<Dictionary<string,object>> SnapshotsData {get{return J.Arr(J.Get(Data,"snapshots")).Select(J.Obj).ToList();}}
    public void Save(){J.Atomic(File,J.Encode(Data));}
    public void SetAppSetting(string key,bool value){if(key!="launchAtLogin"&&key!="silentStartup"&&key!="autoCheckUpdates")throw J.Error("未知应用设置。");var settings=J.Obj(J.Get(Data,"appSettings"));settings[key]=value;if(key=="launchAtLogin"&&!value)settings["silentStartup"]=false;Save();}
    Dictionary<string,object> Find(string id){var p=Providers.FirstOrDefault(x=>J.Str(x,"id")==id);if(p==null)throw J.Error("中转不存在。");return p;}
    string Key(object p,string name="keyEncrypted"){return Secure.Decrypt(J.Str(p,name));}
    Dictionary<string,object> Pub(Dictionary<string,object> p){var x=new Dictionary<string,object>(p);x.Remove("keyEncrypted");x.Remove("accountEncrypted");x.Remove("providerSettingsEncrypted");x.Remove("bridgeUsage");x["hasKey"]=J.Str(p,"keyEncrypted")!="";x["hasAccountKey"]=J.Str(p,"accountEncrypted")!="";x["requiresCodexSetup"]=!ModelProtocol.IsCodex(p);return x;}
    public object Details(string id){var p=Find(id);var x=Pub(p);x["key"]=Key(p);x["accountKey"]=Key(p,"accountEncrypted");return x;}
    Dictionary<string,object> ReadConfig(){string file=Path.Combine(ConfigDir,"config.toml");return System.IO.File.Exists(file)?ParseToml(System.IO.File.ReadAllText(file)):null;}
    string ActiveId(object doc,object info,List<Dictionary<string,object>> providers){
        if(J.Str(info,"provider")=="openai")return "";
        string currentKey=J.Str(J.Get(J.Get(doc,"model_providers"),J.Str(info,"provider")),"experimental_bearer_token"),saved=J.Str(Data,"activeId");
        var matching=providers.Where(p=>SameBaseUrl(J.Str(info,"baseUrl"),J.Str(p,"baseUrl"))).ToList();
        if(matching.Count==0)matching=providers.Where(p=>J.Str(p,"legacyBaseUrl")!=""&&SameBaseUrl(J.Str(info,"baseUrl"),J.Str(p,"legacyBaseUrl"))).ToList();
        var exact=currentKey==""?new List<Dictionary<string,object>>():matching.Where(p=>Key(p)==currentKey).ToList();
        if(exact.Count>0)return J.Str(exact.FirstOrDefault(p=>J.Str(p,"id")==saved)??exact[0],"id");
        if(matching.Count==1)return J.Str(matching[0],"id");
        return currentKey==""?J.Str(matching.FirstOrDefault(p=>J.Str(p,"id")==saved),"id"):"";
    }
    internal string ActiveProviderId(){var doc=ReadConfig();return ActiveId(doc,ConfigInfo(doc),Providers);}
    internal IEnumerable<string> ProviderIds {get{return Providers.Select(p=>J.Str(p,"id"));}}
    public object List(){var doc=ReadConfig();var info=ConfigInfo(doc);var providers=Providers;string active=ActiveId(doc,info,providers);
        return J.Map("providers",providers.Select(p=>{var x=Pub(p);x["active"]=J.Str(p,"id")==active;return x;}).ToList(),"usageCache",UsageCache.Load(this),"appSettings",J.Get(Data,"appSettings"),"config",info,"history",History.Take(100).Cast<object>().ToList(),"dataDir",Dir,"catalogUpgraded",CatalogUpgraded);}
    static bool SameBaseUrl(string left,string right){try{return BaseUrl(left)==BaseUrl(right);}catch{return left==right;}}
    public object ConfigInfo(){return ConfigInfo(ReadConfig());}
    object ConfigInfo(object doc){if(doc==null)return J.Map("exists",false,"official",false,"dir",ConfigDir,"model","","reasoning","","provider","","baseUrl","");var profile=J.Get(J.Get(doc,"profiles"),J.Str(doc,"profile"));string id=J.Str(profile,"model_provider",J.Str(doc,"model_provider","openai"));var providers=J.Obj(J.Get(doc,"model_providers"));var p=J.Obj(J.Get(providers,id));bool official=id=="openai"&&J.Str(profile,"openai_base_url",J.Str(doc,"openai_base_url"))==""&&J.Str(profile,"chatgpt_base_url",J.Str(doc,"chatgpt_base_url"))==""&&!J.Has(providers,"openai");return J.Map("exists",true,"official",official,"dir",ConfigDir,"provider",id,"model",J.Str(profile,"model",J.Str(doc,"model")),"reasoning",J.Str(profile,"model_reasoning_effort",J.Str(doc,"model_reasoning_effort")),"baseUrl",J.Str(p,"base_url"),"hasKey",J.Str(p,"experimental_bearer_token")!="");}
    static Dictionary<string,object> ParseToml(string source){try{return TomlConfig.Parse(TomlConfig.Normalize(source));}catch{throw J.Error("Codex 的 config.toml 格式无效；请先修复，或在设置中选择正确目录。");}}
    internal static string BaseUrl(string raw){var s=(raw??"").Trim();if(s=="")throw J.Error("请填写中转地址。");if(!s.Contains("://"))s="https://"+s;Uri u;if(!Uri.TryCreate(s,UriKind.Absolute,out u)||(u.Scheme!="https"&&!(u.Scheme=="http"&&(u.Host=="localhost"||u.Host=="127.0.0.1"))))throw J.Error("中转地址格式不正确，请使用 HTTPS 地址。");if(u.UserInfo!=""||u.Query!=""||u.Fragment!="")throw J.Error("地址请勿包含账号、查询参数或片段。");return u.GetLeftPart(UriPartial.Authority)+u.AbsolutePath.TrimEnd('/');}
    internal static string SiteRoot(string raw){string value=BaseUrl(raw);foreach(string suffix in new[]{"/api/log/token","/v1/usage","/v1"})if(value.EndsWith(suffix,StringComparison.OrdinalIgnoreCase)){value=value.Substring(0,value.Length-suffix.Length);break;}return value;}
    public object Upsert(object input,bool requireSelections=false,bool requireDiscovery=false){
        var id=J.Str(input,"id");var old=id==""?null:Find(id);string name=J.Str(input,"name").Trim();if(name.Length<1||name.Length>80)throw J.Error("名称需为 1–80 个字符。");
        string protocol=ModelProtocol.Protocol(input),model=J.Str(input,"model").Trim(),reasoning=J.Str(input,"reasoning").Trim();
        bool builtin=J.Str(input,"modelSelection")=="builtin"||(model==""&&J.Str(input,"modelSelection")!="catalog"&&J.Str(input,"discoveryToken")=="");
        if(builtin)model="";
        if(reasoning=="")reasoning="default";
        if(requireSelections){
            if(!builtin&&model=="")throw J.Error("请从获取到的模型列表选择默认模型，或使用 Codex 内置默认。");
            if(!ModelProtocol.ValidReasoning(reasoning))throw J.Error("请选择有效的推理强度。");
        }
        string baseUrl=BaseUrl(J.Str(input,"baseUrl")),siteRaw=J.Str(input,"siteUrl").Trim(),adapter=J.Str(input,"adapter","none");
        ModelProtocol.ValidateAddress(baseUrl);
        if(!new[]{"newapi","sub2api","none"}.Contains(adapter))throw J.Error("开启余额查询后，请选择 NewAPI 或 Sub2API 中转类型。");
        string site=SiteRoot(siteRaw==""?baseUrl:siteRaw);
        double? divisor=J.N(J.Get(input,"quotaPerUnit"))??500000;if(adapter=="newapi"&&divisor<=0)throw J.Error("额度换算值必须大于 0。");
        string key=ModelProtocol.NormalizeKey(J.Str(input,"key")),account=ModelProtocol.NormalizeKey(J.Str(input,"accountKey"));
        if(key.IndexOfAny(new[]{'\r','\n'})>=0||account.IndexOfAny(new[]{'\r','\n'})>=0||key.Length>4096||account.Length>16384)throw J.Error("密钥格式无效。");
        if(key==""&&old==null)throw J.Error("请填写 API 调用密钥。");
        string effectiveAccount=J.True(J.Get(input,"clearAccount"))?"":account!=""?account:old==null?"":Key(old,"accountEncrypted");
        if(adapter=="newapi"&&effectiveAccount!=""&&!Regex.IsMatch(J.Str(input,"accountUserId").Trim(),@"^\d+$"))throw J.Error("填写账户访问令牌后，还需要填写纯数字的 NewAPI 用户 ID。");
        List<string> models=null;
        if(requireDiscovery&&!builtin){
            string effectiveKey=key!=""?key:Key(old),discovery=J.Str(input,"discoveryToken");
            if(discovery!="")models=Models.Resolve(discovery,input,effectiveKey);
            else if(ModelProtocol.IsCodex(old)&&J.Str(old,"modelSelection")=="catalog"&&Models.Fingerprint(old,Key(old))==Models.Fingerprint(input,effectiveKey))models=Models.Saved(old);
            else throw J.Error("请先获取中转模型列表，再选择默认模型。");
            if(!models.Contains(model))throw J.Error("默认模型必须从获取到的模型列表中选择，请重新选择或获取列表。");
        }else if(!builtin&&J.Str(input,"modelSelection")=="catalog")models=Models.Saved(input);
        var p=old==null?new Dictionary<string,object>():new Dictionary<string,object>(old);p["id"]=id==""?Guid.NewGuid().ToString():id;p["name"]=name;p["baseUrl"]=baseUrl;p["siteUrl"]=site;p["adapter"]=adapter;p["protocol"]=protocol;p["model"]=model;p["reasoning"]=reasoning;p["accountUserId"]=J.Str(input,"accountUserId").Trim();p["quotaPerUnit"]=divisor.Value;p["notes"]=J.Str(input,"notes");
        p["apiPathVersion"]=1;if(old!=null&&!SameBaseUrl(J.Str(old,"baseUrl"),baseUrl)){p.Remove("providerSettingsEncrypted");p.Remove("legacyBaseUrl");}
        if(models!=null){p["models"]=models;p["modelSelection"]="catalog";}else {p.Remove("models");if(builtin)p["modelSelection"]="builtin";else p.Remove("modelSelection");}
        p["modelFamily"]="codex";p.Remove("bridgeUsage");p.Remove("modelMetadata");
        p.Remove("contextPreset");
        if(requireSelections&&model!="")ModelCatalog.Create(p,ConfigDir);
        if(key!="")p["keyEncrypted"]=Secure.Encrypt(key);if(J.True(J.Get(input,"clearAccount")))p["accountEncrypted"]="";else if(account!="")p["accountEncrypted"]=Secure.Encrypt(account);
        if(old==null||J.Str(old,"siteUrl")!=site||J.Str(old,"adapter")!=adapter||key!=""&&key!=Key(old)){p.Remove("detectedAdapter");p.Remove("detectedAt");}
        p["updatedAt"]=J.Now();p["revision"]=Guid.NewGuid().ToString();var list=J.Arr(J.Get(Data,"providers"));if(old==null)list.Add(p);else list[list.IndexOf(old)]=p;Data["providers"]=list;Save();return Pub(p);
    }
    public void Delete(string id){if(ActiveProviderId()==id)throw J.Error("请先切换到其他中转，再删除当前中转。");var p=Find(id);Data["providers"]=Providers.Where(x=>!ReferenceEquals(x,p)).Cast<object>().ToList();Data["snapshots"]=SnapshotsData.Where(x=>J.Str(x,"id")!=id).Cast<object>().ToList();Save();}
    public object Reorder(object ids){var input=J.Arr(ids).Select(J.S).ToList();var old=Providers;if(input.Count!=old.Count||input.Distinct().Count()!=input.Count||input.Any(id=>!old.Any(p=>J.Str(p,"id")==id)))throw J.Error("中转列表已变化，请刷新后重试。");Data["providers"]=input.Select(id=>old.First(p=>J.Str(p,"id")==id)).Cast<object>().ToList();Save();return List();}
    internal static Action BeforeConfigCommit;
    static void WriteConfig(string dir,string body,string expected,bool existed){
        string file=Path.Combine(dir,"config.toml"),tmp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        Directory.CreateDirectory(dir);
        try{
            System.IO.File.WriteAllText(tmp,body,new UTF8Encoding(false));BeforeConfigCommit?.Invoke();
            if(ModelCatalog.LinkedPath(file)||System.IO.File.Exists(file)!=existed||(existed&&System.IO.File.ReadAllText(file)!=expected))throw J.Error("Codex 配置已被其他程序修改，本次未覆盖，请刷新后重试。");
            if(existed)System.IO.File.Replace(tmp,file,null);else System.IO.File.Move(tmp,file);
        }finally{if(System.IO.File.Exists(tmp))System.IO.File.Delete(tmp);}
    }
    static readonly string[] RootKeys={"model_provider","model","model_reasoning_effort","model_catalog_json"};
    internal bool UpgradeManagedModelCatalog() {
        string file=Path.Combine(ConfigDir,"config.toml");
        if(!System.IO.File.Exists(file)||ModelCatalog.LinkedPath(file))return false;
        string original=System.IO.File.ReadAllText(file);var doc=ParseToml(original);
        var provider=J.Get(J.Get(doc,"model_providers"),J.Str(doc,"model_provider","openai"));
        if(J.Str(provider,"wire_api","responses")!="responses")return false;
        // Never relabel an old local bridge as a direct Responses provider at startup.
        try{ModelProtocol.ValidateAddress(J.Str(provider,"base_url"));}catch{return false;}
        string oldPath=J.Str(doc,"model_catalog_json");var catalog=ModelCatalog.ReadManaged(Dir,oldPath);
        if(catalog==null)return false;
        string nextPath=ModelCatalog.Upgrade(Dir,catalog,"responses",ConfigDir,J.Str(doc,"model"));if(nextPath=="")return false;
        var root=Regex.Match(original,@"(?m)^(?<prefix>[ \t]*model_catalog_json[ \t]*=[ \t]*)(?:""(?:\\.|[^""\\])*""|'[^']*')");
        if(!root.Success)return false;
        string next=original.Substring(0,root.Index)+root.Groups["prefix"].Value+J.Encode(nextPath)+original.Substring(root.Index+root.Length);
        if(J.Str(ParseToml(next),"model_catalog_json")!=nextPath)throw J.Error("无法安全升级模型目录。");
        string backup=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+"-"+Guid.NewGuid().ToString("N").Substring(0,8);
        J.Atomic(Path.Combine(Dir,"backups",backup+".json"),J.Encode(J.Map("id",backup,"at",J.Now(),"name","升级模型目录前配置","existed",true,"configDir",ConfigDir,"contentEncrypted",Secure.Encrypt(original))));
        if(ModelCatalog.LinkedPath(file)||System.IO.File.ReadAllText(file)!=original)throw J.Error("Codex 配置已被其他程序修改，请重新打开以升级模型目录。");
        WriteConfig(ConfigDir,next,original,true);
        var history=J.Arr(J.Get(Data,"history"));history.Insert(0,J.Map("id",backup,"at",J.Now(),"name","模型目录升级","action","catalog-upgrade"));Data["history"]=history.Take(100).ToList();Save();CatalogUpgraded=true;return true;
    }
    static string Patch(string source,object p,string key,bool login,Dictionary<string,object> advanced,bool keepTable){
        source=TomlConfig.Normalize(source);var doc=ParseToml(source==""?"# Codex configuration\n":source);
        string profile=J.Str(doc,"profile");var selectedProfile=J.Get(J.Get(doc,"profiles"),profile);
        string provider=J.Str(selectedProfile,"model_provider",J.Str(doc,"model_provider"));if(new[]{"","openai","ollama","lmstudio","amazon-bedrock","amazon-bedrock-runtime"}.Contains(provider))provider="custom";
        if(profile!=""&&selectedProfile!=null)foreach(string field in RootKeys)source=TomlConfig.Set(source,new[]{"profiles",profile,field},null,true);
        source=TomlConfig.Set(source,new[]{"model_provider"},provider);
        if(J.Str(p,"modelSelection")=="builtin"){
            source=TomlConfig.Set(source,new[]{"model"},null,true);source=TomlConfig.Set(source,new[]{"model_catalog_json"},null,true);
        }
        if(J.Str(p,"model")!="")source=TomlConfig.Set(source,new[]{"model"},J.Str(p,"model"));
        if(J.Str(p,"modelCatalogPath")!="")source=TomlConfig.Set(source,new[]{"model_catalog_json"},J.Str(p,"modelCatalogPath"));
        string reasoning=J.Str(p,"reasoning");
        source=TomlConfig.Set(source,new[]{"model_reasoning_effort"},reasoning,reasoning==""||reasoning=="default");
        var entry=new Dictionary<string,object>(advanced);
        entry["name"]=J.Str(p,"name");entry["base_url"]=J.Str(p,"baseUrl");entry["wire_api"]="responses";entry["requires_openai_auth"]=login;entry["experimental_bearer_token"]=key;
        var path=new[]{"model_providers",provider};
        if(keepTable){
            var old=J.Obj(J.Get(J.Get(doc,"model_providers"),provider));
            foreach(string field in old.Keys.Where(field=>!entry.ContainsKey(field)).ToArray())source=TomlConfig.Set(source,path.Concat(new[]{field}).ToArray(),null,true);
            foreach(var field in entry)if(!J.Has(old,field.Key)||J.Encode(old[field.Key])!=J.Encode(field.Value))source=TomlConfig.Set(source,path.Concat(new[]{field.Key}).ToArray(),field.Value);
        }else source=TomlConfig.Table(source,path,entry);
        string output=TomlWhitespace.Compact(source);var parsed=ParseToml(output);var applied=J.Obj(J.Get(J.Get(parsed,"model_providers"),provider));
        if(J.Str(parsed,"model_provider")!=provider||J.Str(applied,"experimental_bearer_token")!=key||J.Str(applied,"base_url")!=J.Str(p,"baseUrl")||J.Has(applied,"env_key"))throw J.Error("无法安全更新当前 TOML 配置。");
        return output;
    }
    static Dictionary<string,object> Advanced(object entry){
        var fields=new Dictionary<string,object>(J.Obj(entry));
        foreach(string managed in new[]{"name","base_url","wire_api","requires_openai_auth","experimental_bearer_token","env_key","env_key_instructions","auth","aws"})fields.Remove(managed);
        foreach(string kind in new[]{"http_headers","env_http_headers"})if(J.Has(fields,kind)){
            var headers=new Dictionary<string,object>(J.Obj(fields[kind]));foreach(string name in headers.Keys.Where(name=>name.Equals("Authorization",StringComparison.OrdinalIgnoreCase)).ToArray())headers.Remove(name);
            if(headers.Count==0)fields.Remove(kind);else fields[kind]=headers;
        }
        return fields;
    }
    Dictionary<string,object> SavedAdvanced(object p){
        string encrypted=J.Str(p,"providerSettingsEncrypted");if(encrypted=="")return new Dictionary<string,object>();
        var saved=J.Decode(Secure.Decrypt(encrypted));return SameBaseUrl(J.Str(saved,"baseUrl"),J.Str(p,"baseUrl"))?Advanced(TomlConfig.Parse(J.Str(saved,"toml"))):new Dictionary<string,object>();
    }
    static string AuthText(string dir){string file=Path.Combine(dir,"auth.json");try{return System.IO.File.Exists(file)?System.IO.File.ReadAllText(file):"";}catch{return "";}}
    internal static bool OfficialLogin(string text){
        try{var auth=J.Decode(text);return J.Str(auth,"auth_mode")!="api_key"&&J.Str(J.Get(auth,"tokens"),"access_token")!=""&&J.Str(J.Get(auth,"tokens"),"id_token")!="";}catch{return false;}
    }
    public object SwitchOfficial(){
        string file=Path.Combine(ConfigDir,"config.toml");if(ModelCatalog.LinkedPath(file))throw J.Error("配置文件是链接，请先检查配置目录。");
        bool existed=System.IO.File.Exists(file);string original=existed?System.IO.File.ReadAllText(file):"",auth=AuthText(ConfigDir);
        var doc=ParseToml(original);var info=ConfigInfo(doc);string previousId=ActiveId(doc,info,Providers),profile=J.Str(doc,"profile");
        var current=J.Get(J.Get(doc,"model_providers"),J.Str(info,"provider"));string next=TomlConfig.Normalize(original);
        foreach(string field in RootKeys.Concat(new[]{"openai_base_url","chatgpt_base_url"})){
            next=TomlConfig.Set(next,new[]{field},null,true);
            if(profile!=""&&J.Get(J.Get(doc,"profiles"),profile)!=null)next=TomlConfig.Set(next,new[]{"profiles",profile,field},null,true);
        }
        next=TomlConfig.Set(next,new[]{"model_providers","openai"},null,true);
        next=TomlWhitespace.Compact(TomlConfig.Set(next,new[]{"model_provider"},"openai"));
        if(!J.True(J.Get(ConfigInfo(ParseToml(next)),"official")))throw J.Error("无法安全切换官方配置。");
        string backup=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+"-"+Guid.NewGuid().ToString("N").Substring(0,8);
        J.Atomic(Path.Combine(Dir,"backups",backup+".json"),J.Encode(J.Map("id",backup,"at",J.Now(),"name","切换官方前配置","existed",existed,"configDir",ConfigDir,"contentEncrypted",Secure.Encrypt(original))));
        if(AuthText(ConfigDir)!=auth)throw J.Error("Codex 登录已发生变化，本次未切换，请重试。");WriteConfig(ConfigDir,next,original,existed);
        if(previousId!="")Find(previousId)["providerSettingsEncrypted"]=Secure.Encrypt(J.Encode(J.Map("baseUrl",J.Str(current,"base_url"),"toml",string.Join("\n",Advanced(current).Select(pair=>J.Encode(pair.Key)+" = "+TomlConfig.Literal(pair.Value))))));
        Data["activeId"]=null;var history=J.Arr(J.Get(Data,"history"));history.Insert(0,J.Map("id",backup,"at",J.Now(),"name","OpenAI 官方","action","switch-official"));Data["history"]=history.Take(100).ToList();Save();return List();
    }
    public object Switch(string id){
        var p=Find(id);ModelProtocol.Protocol(p);ModelProtocol.ValidateAddress(J.Str(p,"baseUrl"));string file=Path.Combine(ConfigDir,"config.toml");if(ModelCatalog.LinkedPath(file))throw J.Error("配置文件是链接，请先检查配置目录。");
        bool exists=System.IO.File.Exists(file);string original=exists?System.IO.File.ReadAllText(file):"",key=Key(p);object applied=p;
        var doc=ParseToml(original);var info=ConfigInfo(doc);var current=J.Obj(J.Get(J.Get(doc,"model_providers"),J.Str(info,"provider")));string previousId=ActiveId(doc,info,Providers);
        bool same=previousId==id&&(SameBaseUrl(J.Str(current,"base_url"),J.Str(p,"baseUrl"))||J.Str(p,"legacyBaseUrl")!=""&&SameBaseUrl(J.Str(current,"base_url"),J.Str(p,"legacyBaseUrl")));
        var advanced=same?Advanced(current):SavedAdvanced(p);string auth=AuthText(ConfigDir);
        if(J.Str(p,"model")!=""){var catalogued=new Dictionary<string,object>(J.Obj(applied));catalogued["modelCatalogPath"]=ModelCatalog.Write(Dir,p,ConfigDir);applied=catalogued;}
        string next=Patch(original,applied,key,OfficialLogin(auth),advanced,same),backup=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+"-"+Guid.NewGuid().ToString("N").Substring(0,8);string backupDir=Path.Combine(Dir,"backups");Directory.CreateDirectory(backupDir);
        J.Atomic(Path.Combine(backupDir,backup+".json"),J.Encode(J.Map("id",backup,"at",J.Now(),"name",J.Str(p,"name"),"providerId",id,"existed",exists,"configDir",ConfigDir,"contentEncrypted",Secure.Encrypt(original))));
        if(AuthText(ConfigDir)!=auth)throw J.Error("Codex 登录已发生变化，本次未切换，请重试。");WriteConfig(ConfigDir,next,original,exists);
        if(previousId!="")Find(previousId)["providerSettingsEncrypted"]=Secure.Encrypt(J.Encode(J.Map("baseUrl",J.Str(current,"base_url"),"toml",string.Join("\n",Advanced(current).Select(pair=>J.Encode(pair.Key)+" = "+TomlConfig.Literal(pair.Value))))));
        p.Remove("legacyBaseUrl");Data["activeId"]=id;Data["providers"]=Providers.OrderBy(x=>J.Str(x,"id")==id?0:1).Cast<object>().ToList();var history=J.Arr(J.Get(Data,"history"));history.Insert(0,J.Map("id",backup,"at",J.Now(),"name",J.Str(p,"name"),"action","switch"));Data["history"]=history.Take(100).ToList();Save();return List();
    }
    public object Restore(string id){if(id==""||id.Any(c=>!char.IsDigit(c)&&c!='-'&&!(c>='a'&&c<='f')))throw J.Error("备份 ID 无效。");var b=J.Obj(J.Read(Path.Combine(Dir,"backups",id+".json"),null));if(b.Count==0)throw J.Error("备份不存在。");if(J.Str(b,"configDir")!=ConfigDir)throw J.Error("备份属于另一个配置目录。");string file=Path.Combine(ConfigDir,"config.toml");bool existed=System.IO.File.Exists(file);string old=existed?System.IO.File.ReadAllText(file):"",previous=Secure.Decrypt(J.Str(b,"contentEncrypted"));string recovery=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+"-"+Guid.NewGuid().ToString("N").Substring(0,8);J.Atomic(Path.Combine(Dir,"backups",recovery+".json"),J.Encode(J.Map("id",recovery,"at",J.Now(),"name","恢复前配置","existed",existed,"configDir",ConfigDir,"contentEncrypted",Secure.Encrypt(old))));if(J.True(J.Get(b,"existed")))WriteConfig(ConfigDir,previous,old,existed);else if(existed){if(ModelCatalog.LinkedPath(file)||System.IO.File.ReadAllText(file)!=old)throw J.Error("Codex 配置已变化，请刷新后重试。");System.IO.File.Delete(file);}Data["activeId"]=null;var h=J.Arr(J.Get(Data,"history"));h.Insert(0,J.Map("id",recovery,"at",J.Now(),"name",J.Str(b,"name"),"action","restore"));Data["history"]=h.Take(100).ToList();Save();return List();}
    public object Import(){var doc=ParseToml(System.IO.File.ReadAllText(Path.Combine(ConfigDir,"config.toml")));string name=J.Str(doc,"model_provider","openai");var providers=J.Obj(J.Get(doc,"model_providers"));var p=J.Obj(J.Get(providers,name));string baseUrl=J.Str(p,"base_url"),key=J.Str(p,"experimental_bearer_token");if(baseUrl==""||key=="")throw J.Error("当前配置没有可导入的中转密钥，请手动添加。");ModelProtocol.Protocol(J.Map("protocol",J.Str(p,"wire_api","responses")));ModelProtocol.ValidateAddress(baseUrl);if(Providers.Any(x=>SameBaseUrl(J.Str(x,"baseUrl"),baseUrl)))throw J.Error("列表中已存在这个地址，请直接编辑。");return J.Map("name",J.Str(p,"name",name),"baseUrl",baseUrl,"siteUrl",baseUrl,"key",key,"adapter","none","modelFamily","codex","protocol","responses","model",J.Str(doc,"model"),"reasoning",J.Str(doc,"model_reasoning_effort"));}
    public object Snapshots(string id){Find(id);return SnapshotsData.Where(s=>J.Str(s,"id")==id).Take(200).Cast<object>().ToList();}
    public void Record(string id,object snapshot){var list=J.Arr(J.Get(Data,"snapshots"));var x=J.Obj(J.Clone(snapshot));x["id"]=id;list.Insert(0,x);Data["snapshots"]=list.Take(1000).ToList();Save();}
    public string Secret(string id,string field="keyEncrypted"){return Key(Find(id),field);}
    public int JCount(){return Providers.Count;}
    public void SetDetection(string id,string revision,string adapter,bool save=true){var p=Find(id);if(J.Str(p,"revision")!=revision)return;p["detectedAdapter"]=adapter;p["detectedAt"]=J.Now();if(save)Save();}
    public void RequireRevision(string id,string revision){if(J.Str(Find(id),"revision")!=revision)throw J.Error("中转已修改或删除，旧查询结果已丢弃，请重新刷新。");}
}
}
