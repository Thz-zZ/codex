using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace RelayLite {
internal static class ProviderIdChecks {
    public static void Run(string root,List<string> checks){
        foreach(string id in new[]{"custom","codex_relay","Relay Custom",""}){
            string dir=Path.Combine(root,"provider-id-"+Guid.NewGuid().ToString("N")),config=Path.Combine(dir,"config");Directory.CreateDirectory(config);
            string original=id==""?"# initial\n": "model_provider = "+J.Encode(id)+"\n[model_providers."+J.Encode(id)+"]\nbase_url = \"https://old.example/v1\"\nexperimental_bearer_token = \"fake-old-key\"\n";
            File.WriteAllText(Path.Combine(config,"config.toml"),original);
            var store=new Store(Path.Combine(dir,"store"),config,new Secure());string expected=id==""?"custom":id;
            foreach(string suffix in new[]{"one","two"}){
                var p=store.Upsert(J.Map("name",suffix,"baseUrl","https://"+suffix+".example/v1","siteUrl","","key","fake-key-"+suffix));
                string pid=J.Str(p,"id");var result=store.Switch(pid);var info=J.Get(result,"config");
                if(J.Str(info,"provider")!=expected||J.Str(info,"baseUrl")!="https://"+suffix+".example")throw J.Error("provider ID must remain stable across relays");
                var rows=J.Arr(J.Get(result,"providers"));if(rows.Count(x=>J.True(J.Get(x,"active")))!=1||!J.True(J.Get(rows.First(x=>J.Str(x,"id")==pid),"active"))||store.ActiveProviderId()!=pid)throw J.Error("stable provider active state");
            }
        }
        checks.Add("stable provider identity preserves custom, codex_relay and quoted IDs across relay switches");
    }
}
}
