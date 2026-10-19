using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace RelayLite {
internal static class CodexOnlyChecks {
    static void Check(bool condition,string message){if(!condition)throw J.Error("Codex only: "+message);}
    internal static async Task Run(string root,List<string> checks) {
        Check(typeof(Store).Assembly.GetType("RelayLite.ModelBridge")==null&&typeof(Store).Assembly.GetType("RelayLite.ClaudeProtocol")==null,"compiled application contains no removed model bridge implementations");
        string directory=Path.Combine(root,"codex-only"),config=Path.Combine(directory,"config");
        Directory.CreateDirectory(config);string file=Path.Combine(config,"config.toml");
        string original="model = \"previous-model\"\n[features]\nkeep = true\n";File.WriteAllText(file,original);
        var store=new Store(directory,config,new Secure());
        var valid=J.Map("name","Codex fixture","baseUrl","https://codex.example/proxy/v1","key","fake-codex-only-key","model","gpt-codex-fixture","reasoning","default","adapter","none");
        var saved=store.Upsert(valid,true);
        Check(J.Str(saved,"modelFamily")=="codex"&&J.Str(saved,"protocol")=="responses","single product and protocol default without obsolete selections");
        string before=File.ReadAllText(store.File);int requests=0;
        Usage.FetchOverride=(url,key,user)=>{requests++;return Task.FromResult<object>(J.Map("data",new[]{J.Map("id","gpt-codex-fixture")}));};
        try{
            foreach(string family in new[]{"claude","grok","deepseek","qwen","glm","gemini","invalid"})foreach(string protocol in new[]{"responses","chat-completions","anthropic"}) {
                var invalid=J.Obj(J.Clone(valid));invalid["modelFamily"]=family;invalid["protocol"]=protocol;
                foreach(bool strict in new[]{false,true}){bool rejected=false;try{store.Upsert(invalid,strict);}catch(Exception error){rejected=error.Message.Contains("仅支持 Codex");}Check(rejected&&File.ReadAllText(store.File)==before,"non-Codex saves are rejected without mutation");}
                bool blocked=false;try{await Models.List(invalid);}catch{blocked=true;}Check(blocked,"non-Codex model discovery is rejected");
                blocked=false;try{await ConnectionTest.Run(invalid);}catch{blocked=true;}Check(blocked,"non-Codex connection tests are rejected");
            }
            foreach(string protocol in new[]{"chat-completions","anthropic","invalid"}) {
                var invalid=J.Obj(J.Clone(valid));invalid["modelFamily"]="codex";invalid["protocol"]=protocol;
                bool blocked=false;try{store.Upsert(invalid,true);}catch{blocked=true;}Check(blocked&&File.ReadAllText(store.File)==before,"Codex cannot opt into another wire protocol");
                blocked=false;try{await Models.List(invalid);}catch{blocked=true;}Check(blocked,"bridge protocol cannot discover models");
                blocked=false;try{await ConnectionTest.Run(invalid);}catch{blocked=true;}Check(blocked,"bridge protocol cannot test connections");
            }
            Check(requests==0,"removed protocols never send HTTP requests");
            // Seed historical records directly: new saves intentionally no longer accept them.
            var legacy=J.Obj(J.Clone(J.Arr(J.Get(store.Data,"providers"))[0]));legacy["id"]="legacy-provider";legacy["modelFamily"]="claude";legacy["protocol"]="anthropic";legacy["modelSelection"]="catalog";legacy["models"]=new[]{"gpt-codex-fixture"};
            var providers=J.Arr(J.Get(store.Data,"providers"));providers.Add(legacy);store.Data["providers"]=providers;store.Save();
            string legacyFile=File.ReadAllText(store.File);store=new Store(directory,config,new Secure());
            legacy=J.Obj(J.Arr(J.Get(store.Data,"providers"))[1]);var details=store.Details("legacy-provider");
            Check(File.ReadAllText(store.File)==legacyFile&&J.Str(details,"key")=="fake-codex-only-key"&&J.Str(details,"protocol")=="anthropic"&&J.True(J.Get(details,"requiresCodexSetup")),"reading legacy records preserves data, encrypted credentials and migration status");
            foreach(string family in new[]{"codex","claude"})foreach(string protocol in new[]{"anthropic","chat-completions","responses"}){
                if(family=="codex"&&protocol=="responses")continue;
                legacy["modelFamily"]=family;legacy["protocol"]=protocol;
                bool blocked=false;try{store.Switch("legacy-provider");}catch{blocked=true;}
                Check(blocked&&File.ReadAllText(file)==original&&!Directory.Exists(Path.Combine(directory,"backups"))&&!Directory.Exists(Path.Combine(directory,"model-catalogs")),"legacy apply is rejected before config, backup or catalog writes");
            }
            legacy["modelFamily"]="claude";legacy["protocol"]="anthropic";
            var conversion=J.Obj(J.Clone(valid));conversion["id"]="legacy-provider";conversion["modelFamily"]="codex";conversion["protocol"]="responses";
            bool needsDiscovery=false;try{store.Upsert(conversion,true,true);}catch(Exception error){needsDiscovery=error.Message.Contains("获取");}
            Check(needsDiscovery&&File.ReadAllText(store.File)==legacyFile,"legacy model lists cannot be reused for a different product or protocol");
            conversion["discoveryToken"]=J.Str(await Models.List(conversion),"discoveryToken");
            var converted=store.Upsert(conversion,true,true);store.Switch("legacy-provider");
            Check(J.Str(converted,"modelFamily")=="codex"&&J.Str(converted,"protocol")=="responses"&&!J.True(J.Get(converted,"requiresCodexSetup"))&&store.Secret("legacy-provider")=="fake-codex-only-key","explicit edit plus fresh discovery converts a legacy record without losing credentials");
            string applied=File.ReadAllText(file);
            Check(applied.Contains("wire_api = \"responses\"")&&applied.Contains("https://codex.example/proxy")&&!applied.Contains("127.0.0.1")&&applied.Contains("keep = true"),"apply uses the upstream directly and preserves unrelated Codex settings");
            Check(!File.Exists(Path.Combine(directory,"model-bridge.json")),"direct apply creates no bridge state");
            var importer=new Store(Path.Combine(root,"codex-import"),config,new Secure());
            var imported=importer.Import();Check(J.Str(imported,"modelFamily")=="codex"&&J.Str(imported,"protocol")=="responses"&&importer.JCount()==0,"import is a Codex-only unsaved draft");
            foreach(string protocol in new[]{"chat","anthropic"}){
                File.WriteAllText(file,applied.Replace("wire_api = \"responses\"","wire_api = "+J.Encode(protocol)));
                bool blocked=false;try{importer.Import();}catch{blocked=true;}Check(blocked&&importer.JCount()==0,"import cannot relabel another wire protocol as Responses");
            }
            string bridgeUrl="http://127.0.0.1:12345/relay/old/revision/v1";
            File.WriteAllText(file,applied.Replace("https://codex.example/proxy",bridgeUrl));
            bool bridgeImport=false;try{importer.Import();}catch(Exception error){bridgeImport=error.Message.Contains("本地模型桥接");}Check(bridgeImport,"old loopback bridge credentials cannot be imported as upstream credentials");
            var bridgeInput=J.Obj(J.Clone(valid));bridgeInput["baseUrl"]=bridgeUrl;int beforeBridge=requests;
            bool blockedBridge=false;try{await Models.List(bridgeInput);}catch{blockedBridge=true;}Check(blockedBridge&&requests==beforeBridge,"old bridge endpoints are blocked before discovery HTTP");
            blockedBridge=false;try{await ConnectionTest.Run(bridgeInput);}catch{blockedBridge=true;}Check(blockedBridge&&requests==beforeBridge,"old bridge endpoints are blocked before connection HTTP");
            File.WriteAllText(file,applied);
            checks.Add("Codex-only saves, discovery and connection tests reject removed model families and wire protocols before HTTP");
            checks.Add("legacy records and keys preserved, apply blocked before writes, fresh discovery required for explicit conversion");
            checks.Add("Responses direct apply/import, custom paths, unrelated config preservation and no local model bridge");
        }finally{Usage.FetchOverride=null;}
    }
}
}
