using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RelayLite {
internal static class ConfigChecks {
    static void Check(bool value,string message){if(!value)throw J.Error("Codex config: "+message);}
    internal static void Run(string root,List<string> checks){
        Addresses(root,checks).GetAwaiter().GetResult();
        Settings(root,checks);
        SharedEndpointSettings(root,checks);
        Profiles(root,checks);
        Official(root,checks);
        NativeMetadata(root,checks);
        Watcher(root,checks);
    }
    static async Task Addresses(string root,List<string> checks){
        try{
            int index=0;
            foreach(string api in new[]{"https://api.example/v1","https://api.example/proxy/v2","https://api.example"}){
                var input=J.Map("name","path fixture","baseUrl",api+"/","key","fixture-path-key","adapter","none","modelSelection","builtin");
                var store=new Store(Path.Combine(root,"path-store-"+index),Path.Combine(root,"path-config-"+index),new Secure());index++;
                var provider=store.Upsert(input,true);store.Switch(J.Str(provider,"id"));
                Check(J.Str(store.ConfigInfo(),"baseUrl")==api&&ModelProtocol.ModelRoot(input)==api,"apply and discovery preserve the complete API path, including the root");
                int requests=0;
                Usage.FetchOverride=(url,key,user)=>{Check(url==api+"/models","discovery and connection test use the exact applied prefix");requests++;return Task.FromResult<object>(J.Map("data",new[]{J.Map("id","fixture-model")}));};
                await Models.List(input);await ConnectionTest.Run(input);Check(requests==2,"both API checks hit the applied endpoint");
            }
            string config=Path.Combine(root,"path-migration-config"),dir=Path.Combine(root,"path-migration-store");Directory.CreateDirectory(config);
            var legacy=new Store(dir,config,new Secure());var saved=legacy.Upsert(J.Map("name","old path","baseUrl","https://legacy-path.example/prefix","key","fixture-legacy-key"));
            var record=J.Obj(J.Arr(J.Get(legacy.Data,"providers"))[0]);record.Remove("apiPathVersion");legacy.Save();
            string original="model_provider = \"custom\"\n[model_providers.custom]\nbase_url = \"https://legacy-path.example/prefix\"\nexperimental_bearer_token = \"fixture-legacy-key\"\n";
            File.WriteAllText(Path.Combine(config,"config.toml"),original);legacy=new Store(dir,config,new Secure());
            Check(J.Str(legacy.Details(J.Str(saved,"id")),"baseUrl")=="https://legacy-path.example/prefix/v1"&&legacy.ActiveProviderId()==J.Str(saved,"id")&&File.ReadAllText(Path.Combine(config,"config.toml"))==original,"old normalized records recover /v1 while live configuration remains untouched");
            legacy.Switch(J.Str(saved,"id"));Check(J.Str(legacy.ConfigInfo(),"baseUrl")=="https://legacy-path.example/prefix/v1","explicit apply repairs the old generated API address");
            Check(Store.SiteRoot("https://site.example/prefix/v1")=="https://site.example/prefix","balance site paths remain independent from API paths");
            checks.Add("complete API paths shared by discovery, connection and apply; root/custom prefixes; compatible old-path migration");
        }finally{Usage.FetchOverride=null;}
    }
    static void Settings(string root,List<string> checks){
        string config=Path.Combine(root,"advanced-config"),dir=Path.Combine(root,"advanced-store"),file=Path.Combine(config,"config.toml");Directory.CreateDirectory(config);
        var store=new Store(dir,config,new Secure());
        var a=store.Upsert(J.Map("name","relay A","baseUrl","https://advanced.example/v1","key","fixture-a-key"));
        var b=store.Upsert(J.Map("name","relay B","baseUrl","https://other.example/v2","key","fixture-b-key"));
        string aId=J.Str(a,"id"),bId=J.Str(b,"id");
        string text="model_provider = 'Relay Custom' # route\n[features]\ntext = '''keep first\n\n[model_providers.\"Relay Custom\"]\nmodel = \"this is text\"\nkeep last'''\narr = [\n  'a', # comment\n  'b'\n]\n[agents]\n[model_providers.\"Relay Custom\"] # provider comment\nbase_url = 'https://advanced.example/v1' # api comment\nenv_key = 'OLD_KEY'\nrequest_max_retries = 7 # keep retries\nstream_idle_timeout_ms = 12345\n[model_providers.\"Relay Custom\".http_headers]\nAuthorization = 'Bearer old-key'\nX-Organization = 'fixture-private-header'\n";
        File.WriteAllText(file,text);string auth="{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"fake-official-access\",\"id_token\":\"fake-official-id\"}}";File.WriteAllText(Path.Combine(config,"auth.json"),auth);
        store.Switch(aId);var doc=TomlConfig.Parse(File.ReadAllText(file));var provider=J.Get(J.Get(doc,"model_providers"),"Relay Custom");
        Check(J.True(J.Get(provider,"requires_openai_auth"))&&File.ReadAllText(Path.Combine(config,"auth.json"))==auth,"official login is preserved without writing third-party credentials to auth.json");
        Check(!J.Has(provider,"env_key")&&!J.Has(J.Get(provider,"http_headers"),"Authorization")&&J.Str(provider,"experimental_bearer_token")=="fixture-a-key","old authentication sources cannot override the newly selected API key");
        Check(J.Num(provider,"request_max_retries")==7&&J.Str(J.Get(provider,"http_headers"),"X-Organization")=="fixture-private-header"&&File.ReadAllText(file).Contains("# keep retries"),"advanced provider settings and inline comments survive reapply");
        Check(J.Str(J.Get(doc,"features"),"text")==J.Str(J.Get(TomlConfig.Parse(text),"features"),"text")&&J.Arr(J.Get(J.Get(doc,"features"),"arr")).Count==2,"multiline strings containing fake table headers and multiline arrays stay intact");
        store.Switch(bId);provider=J.Get(J.Get(TomlConfig.Parse(File.ReadAllText(file)),"model_providers"),"Relay Custom");
        Check(!J.Has(provider,"http_headers")&&!J.Has(provider,"request_max_retries"),"the outgoing relay's private headers are never sent to another relay");
        Check(!File.ReadAllText(store.File).Contains("fixture-private-header")&&!J.Encode(store.List()).Contains("providerSettingsEncrypted"),"per-relay settings are encrypted and absent from the public state");
        store=new Store(dir,config,new Secure());store.Switch(aId);provider=J.Get(J.Get(TomlConfig.Parse(File.ReadAllText(file)),"model_providers"),"Relay Custom");
        Check(J.Num(provider,"request_max_retries")==7&&J.Str(J.Get(provider,"http_headers"),"X-Organization")=="fixture-private-header","switching back restores the relay's own settings after restart");
        File.WriteAllText(Path.Combine(config,"auth.json"),"{\"OPENAI_API_KEY\":\"fake-official-api-key\"}");store.Switch(aId);
        provider=J.Get(J.Get(TomlConfig.Parse(File.ReadAllText(file)),"model_providers"),"Relay Custom");Check(!J.True(J.Get(provider,"requires_openai_auth")),"an API-key auth file is not mistaken for an official ChatGPT login");
        File.WriteAllText(file,"'model_provider' = 'Relay Custom'\nmodel_providers = { 'Relay Custom' = { base_url = 'https://advanced.example/v1', request_max_retries = 9 } } # inline provider\n");
        store.Switch(aId);provider=J.Get(J.Get(TomlConfig.Parse(File.ReadAllText(file)),"model_providers"),"Relay Custom");Check(J.Num(provider,"request_max_retries")==9&&File.ReadAllText(file).Contains("# inline provider"),"quoted keys and inline provider tables can be updated without duplicate declarations");
        string external="model_provider = 'external'\n# concurrent client edit\n";string before=File.ReadAllText(store.File);
        try{Store.BeforeConfigCommit=()=>File.WriteAllText(file,external);bool failed=false;try{store.Switch(bId);}catch(Exception error){failed=error.Message.Contains("其他程序修改");}
            Check(failed&&File.ReadAllText(file)==external&&File.ReadAllText(store.File)==before&&Directory.GetFiles(config,"*.tmp").Length==0,"concurrent writes are rejected before overwriting live config or saved active state");
        }finally{Store.BeforeConfigCommit=null;}
        checks.Add("official-login compatibility, scoped API keys, advanced-setting round trips and encrypted relay isolation");
        checks.Add("TOML multiline/array/quoted/inline editing, comment preservation and concurrent-write protection");
    }
    static void SharedEndpointSettings(string root,List<string> checks){
        string config=Path.Combine(root,"shared-endpoint-config"),dir=Path.Combine(root,"shared-endpoint-store"),file=Path.Combine(config,"config.toml");Directory.CreateDirectory(config);
        var store=new Store(dir,config,new Secure());
        var a=store.Upsert(J.Map("name","shared A","baseUrl","https://shared-settings.example/v1","key","shared-key-a"));
        var b=store.Upsert(J.Map("name","shared B","baseUrl","https://shared-settings.example/v1","key","shared-key-b"));
        string aId=J.Str(a,"id"),bId=J.Str(b,"id");
        File.WriteAllText(file,"model_provider = 'custom'\n[model_providers.custom]\nbase_url = 'https://shared-settings.example/v1'\nexperimental_bearer_token = 'shared-key-a'\nrequest_max_retries = 7\n[model_providers.custom.http_headers]\nX-Organization = 'private-org-a'\n");
        store.Switch(aId);store.Switch(bId);
        var provider=J.Get(J.Get(TomlConfig.Parse(File.ReadAllText(file)),"model_providers"),"custom");
        Check(J.Str(provider,"experimental_bearer_token")=="shared-key-b"&&!J.Has(provider,"http_headers")&&!J.Has(provider,"request_max_retries"),"same-endpoint credentials identify distinct relays and cannot inherit outgoing private headers or retries");
        string text=TomlConfig.Set(File.ReadAllText(file),new[]{"model_providers","custom","http_headers"},J.Map("X-Organization","private-org-b"));
        File.WriteAllText(file,TomlConfig.Set(text,new[]{"model_providers","custom","request_max_retries"},2));
        store.Switch(aId);provider=J.Get(J.Get(TomlConfig.Parse(File.ReadAllText(file)),"model_providers"),"custom");
        Check(J.Str(J.Get(provider,"http_headers"),"X-Organization")=="private-org-a"&&J.Num(provider,"request_max_retries")==7,"switching back to a shared endpoint restores A's own settings");
        store=new Store(dir,config,new Secure());store.Switch(bId);store.Switch(bId);
        provider=J.Get(J.Get(TomlConfig.Parse(File.ReadAllText(file)),"model_providers"),"custom");
        Check(J.Str(J.Get(provider,"http_headers"),"X-Organization")=="private-org-b"&&J.Num(provider,"request_max_retries")==2,"B's settings survive restart and reapply without being replaced by A's settings");
        Check(!File.ReadAllText(store.File).Contains("private-org-")&&!J.Encode(store.List()).Contains("providerSettingsEncrypted"),"shared-endpoint settings remain encrypted and private");
        checks.Add("same-endpoint relay credential isolation, independent private headers/retries and restart/reapply round trips");
    }
    static void Profiles(string root,List<string> checks){
        foreach(bool inline in new[]{false,true}){
            string directory=Path.Combine(root,"profile-switch-"+inline),config=Path.Combine(directory,"config"),file=Path.Combine(config,"config.toml");Directory.CreateDirectory(config);
            var store=new Store(Path.Combine(directory,"store"),config,new Secure());
            var input=J.Map("name","profile target","baseUrl","https://profile-target.example/v1","key","profile-target-key","model","profile-target-fixture","reasoning","low","modelSelection","catalog","models",new[]{"profile-target-fixture"});
            string id=J.Str(store.Upsert(input,true),"id");input["id"]=id;
            string profile=inline?"profiles = { work = { model_provider = 'profile-route', model = 'old-model', model_reasoning_effort = 'high', model_catalog_json = 'old.json', sandbox_mode = 'workspace-write' }, other = { model = 'keep-other' } }\n":"[profiles.work]\nmodel_provider = 'profile-route'\nmodel = 'old-model'\nmodel_reasoning_effort = 'high'\nmodel_catalog_json = 'old.json'\nsandbox_mode = 'workspace-write'\n[profiles.other]\nmodel = 'keep-other'\n";
            string original="profile = 'work'\nmodel_provider = 'root-route'\n"+profile+"[model_providers.root-route]\nbase_url = 'https://root-untouched.example/v1'\n[model_providers.profile-route]\nbase_url = 'https://old-profile.example/v1'\n";
            File.WriteAllText(file,original);store.Switch(id);
            var document=TomlConfig.Parse(File.ReadAllText(file));var work=J.Get(J.Get(document,"profiles"),"work");var info=store.ConfigInfo();
            Check(store.ActiveProviderId()==id&&J.Str(info,"baseUrl")=="https://profile-target.example/v1"&&J.Str(info,"model")=="profile-target-fixture"&&J.Str(info,"reasoning")=="low","effective profile routing/model/effort follow the newly applied relay");
            Check(new[]{"model_provider","model","model_reasoning_effort","model_catalog_json"}.All(field=>!J.Has(work,field))&&J.Str(document,"model_catalog_json")!="","active profile overrides cannot shadow the selected custom catalog");
            Check(J.Str(info,"provider")=="profile-route"&&J.Str(J.Get(J.Get(document,"model_providers"),"root-route"),"base_url")=="https://root-untouched.example/v1"&&J.Str(work,"sandbox_mode")=="workspace-write"&&J.Str(J.Get(J.Get(document,"profiles"),"other"),"model")=="keep-other","effective provider identity, unrelated root provider, profile preferences and other profiles are preserved");
            string backup=J.Str(J.Arr(J.Get(store.Data,"history"))[0],"id");store.Restore(backup);Check(File.ReadAllText(file)==original,"profile switch backup restores the original text exactly");
            input["modelSelection"]="builtin";input["model"]="";input["reasoning"]="default";store.Upsert(input,true,true);store.Switch(id);
            document=TomlConfig.Parse(File.ReadAllText(file));work=J.Get(J.Get(document,"profiles"),"work");info=store.ConfigInfo();
            Check(store.ActiveProviderId()==id&&J.Str(info,"model")==""&&J.Str(info,"reasoning")==""&&!J.Has(document,"model_catalog_json")&&new[]{"model_provider","model","model_reasoning_effort","model_catalog_json"}.All(field=>!J.Has(work,field)),"built-in defaults clear both root and active-profile model/catalog/effort overrides");
        }
        checks.Add("active explicit/inline profile relay switching, custom and built-in models, unrelated-profile preservation and exact restore");
    }
    static void NativeMetadata(string root,List<string> checks){
        try{
            var native=J.Map("slug","native-fixture","display_name","Native","description","Native description","visibility","list","priority",7,"supported_in_api",true,
                "base_instructions","native coding instructions","model_messages",J.Map("instructions_template","native tool prompt"),"context_window",64000,"effective_context_window_percent",90,
                "shell_type","unified_exec","input_modalities",new[]{"text","image"},"default_reasoning_level","high","supported_reasoning_levels",new[]{J.Map("effort","low"),J.Map("effort","high")},"apply_patch_tool_type","freeform");
            var other=J.Obj(J.Clone(native));other["slug"]="native-other";other["default_reasoning_level"]="low";
            NativeModelCatalog.ReadOverride=()=>J.Map("models",new[]{native,other});
            var input=J.Map("model","native-fixture","modelSelection","catalog","models",new[]{"native-fixture","native-other","native-fixture-alias"},"reasoning","low");
            var entries=J.Arr(J.Get(ModelCatalog.Create(input),"models"));var selected=entries.First(value=>J.Str(value,"slug")=="native-fixture");var unknown=entries.First(value=>J.Str(value,"slug")=="native-fixture-alias");
            Check(J.Str(selected,"base_instructions")=="native coding instructions"&&J.Encode(J.Get(selected,"model_messages"))==J.Encode(J.Get(native,"model_messages"))&&J.Num(selected,"context_window")==64000&&J.Str(selected,"shell_type")=="unified_exec","known models retain the native prompt, tools and exact context metadata");
            Check(J.Str(selected,"default_reasoning_level")=="low"&&J.Str(entries.First(value=>J.Str(value,"slug")=="native-other"),"default_reasoning_level")=="low"&&J.Arr(J.Get(selected,"supported_reasoning_levels")).Count==2,"only the selected default is changed and native supported efforts are preserved");
            Check(!J.Has(unknown,"context_window")&&!J.Has(unknown,"apply_patch_tool_type")&&J.Arr(J.Get(unknown,"supported_reasoning_levels")).Count==0&&J.Arr(J.Get(unknown,"input_modalities")).SequenceEqual(new object[]{"text"}),"unknown aliases do not inherit fabricated context, reasoning, image or tool capabilities");
            input["reasoning"]="ultra";bool rejected=false;try{ModelCatalog.Create(input);}catch(Exception error){rejected=error.Message.Contains("未声明");}Check(rejected,"unsupported effort defaults for known models are rejected");
            var old=J.Map("relay_catalog_version",3,"models",new[]{J.Map("slug","native-fixture","default_reasoning_level","high","context_window",272000,"effective_context_window_percent",95),J.Map("slug","native-other","default_reasoning_level","high","context_window",272000,"effective_context_window_percent",95)});
            var upgraded=J.Arr(J.Get(J.Read(ModelCatalog.Upgrade(Path.Combine(root,"native-metadata-upgrade"),old,"responses",null,"native-fixture"),null),"models"));
            Check(J.Num(upgraded[0],"context_window")==64000&&J.Str(upgraded[0],"base_instructions")=="native coding instructions"&&J.Str(upgraded[1],"default_reasoning_level")=="low","startup migration replaces old synthetic metadata and stops propagating the selected effort to other models");
            checks.Add("native model prompts/tools/context/efforts, selected-only defaults and conservative unknown-alias metadata");
        }finally{NativeModelCatalog.ReadOverride=null;}
    }
    static void Official(string root,List<string> checks){
        string config=Path.Combine(root,"official-config"),dir=Path.Combine(root,"official-store"),file=Path.Combine(config,"config.toml");Directory.CreateDirectory(config);
        var store=new Store(dir,config,new Secure());var relay=store.Upsert(J.Map("name","official roundtrip","baseUrl","https://official-roundtrip.example/v1","key","fake-relay-official-key"));string id=J.Str(relay,"id");
        string auth="{\"tokens\":{\"access_token\":\"fake-access\",\"id_token\":\"fake-id\"}}";File.WriteAllText(Path.Combine(config,"auth.json"),auth);
        string original="model_provider = 'custom'\nmodel = 'relay-model'\nmodel_catalog_json = 'external-catalog.json'\nmodel_reasoning_effort = 'high'\nopenai_base_url = 'https://old-proxy.example/v1'\nchatgpt_base_url = 'https://old-proxy.example/backend-api'\nprofile = 'work'\n# preserve global preferences\n[features]\nkeep = true\n[profiles.work]\nmodel_provider = 'custom'\nmodel = 'profile-model'\nmodel_reasoning_effort = 'high'\n[profiles.other]\nmodel = 'other-model'\n[model_providers.custom]\nbase_url = 'https://official-roundtrip.example/v1'\nexperimental_bearer_token = 'fake-relay-official-key'\nrequest_max_retries = 7\n[model_providers.custom.http_headers]\nX-Organization = 'private-roundtrip'\n[model_providers.openai]\nbase_url = 'https://old-proxy.example/v1'\n";
        File.WriteAllText(file,original);store.SwitchOfficial();var document=TomlConfig.Parse(File.ReadAllText(file));
        Check(J.True(J.Get(store.ConfigInfo(),"official"))&&store.ActiveProviderId()==""&&!J.Arr(J.Get(store.List(),"providers")).Any(value=>J.True(J.Get(value,"active"))),"official mode has no active relay or stale active marker");
        Check(File.ReadAllText(Path.Combine(config,"auth.json"))==auth&&!J.Has(document,"model")&&!J.Has(document,"model_catalog_json")&&!J.Has(document,"openai_base_url")&&!J.Has(document,"chatgpt_base_url")&&!J.Has(J.Get(document,"model_providers"),"openai"),"official switch preserves login and clears relay model/auth endpoint overrides");
        Check(!J.Has(J.Get(J.Get(document,"profiles"),"work"),"model_provider")&&J.Str(J.Get(J.Get(document,"profiles"),"other"),"model")=="other-model"&&J.True(J.Get(J.Get(document,"features"),"keep"))&&File.ReadAllText(file).Contains("# preserve global preferences"),"selected legacy profile overrides cannot defeat official switching; other profiles and preferences survive");
        string backup=J.Str(J.Arr(J.Get(store.Data,"history"))[0],"id");store.Restore(backup);Check(File.ReadAllText(file)==original,"official-switch backup restores the original text exactly");
        store.SwitchOfficial();store=new Store(dir,config,new Secure());store.Switch(id);document=TomlConfig.Parse(File.ReadAllText(file));
        Check(store.ActiveProviderId()==id&&J.Str(J.Get(J.Get(J.Get(document,"model_providers"),"custom"),"http_headers"),"X-Organization")=="private-roundtrip","switching from official back to the relay restores encrypted advanced settings after restart");
        string external="model_provider = 'external'\n";string before=File.ReadAllText(store.File);
        try{Store.BeforeConfigCommit=()=>File.WriteAllText(file,external);bool rejected=false;try{store.SwitchOfficial();}catch(Exception error){rejected=error.Message.Contains("其他程序修改");}Check(rejected&&File.ReadAllText(file)==external&&File.ReadAllText(store.File)==before,"official switching rejects concurrent edits without changing saved state");}finally{Store.BeforeConfigCommit=null;}
        var fresh=new Store(Path.Combine(root,"official-fresh-store"),Path.Combine(root,"official-fresh-config"),new Secure());fresh.SwitchOfficial();Check(J.True(J.Get(fresh.ConfigInfo(),"official"))&&!File.Exists(Path.Combine(fresh.ConfigDir,"auth.json")),"official configuration can be selected before login without fabricating credentials");
        checks.Add("one-click official mode, unchanged login, scoped profile cleanup, exact backup, advanced-setting roundtrip and write-conflict protection");
    }
    static void Watcher(string root,List<string> checks){
        string directory=Path.Combine(root,"config-watcher");Directory.CreateDirectory(directory);int changes=0;
        using(var owner=new System.Windows.Forms.Control()){
            var handle=owner.Handle;
            using(var watcher=new ConfigWatcher(owner,directory,()=>changes++)){
                File.WriteAllText(Path.Combine(directory,"config.toml"),"model = 'external-client'\n");
                var timeout=System.Diagnostics.Stopwatch.StartNew();
                while(changes==0&&timeout.ElapsedMilliseconds<3000){System.Windows.Forms.Application.DoEvents();System.Threading.Thread.Sleep(10);}
                Check(changes>0,"external creation/changes reach the UI thread without user interaction");
            }
        }
        checks.Add("external config change watcher dispatches a silent refresh to the UI thread");
    }
}
}
