using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
                if(J.Str(info,"provider")!=expected||J.Str(info,"baseUrl")!="https://"+suffix+".example/v1")throw J.Error("provider ID must remain stable across relays");
                var rows=J.Arr(J.Get(result,"providers"));if(rows.Count(x=>J.True(J.Get(x,"active")))!=1||!J.True(J.Get(rows.First(x=>J.Str(x,"id")==pid),"active"))||store.ActiveProviderId()!=pid)throw J.Error("stable provider active state");
            }
        }
        checks.Add("stable provider identity preserves custom, codex_relay and quoted IDs across relay switches");
        foreach(string newline in new[]{"\n","\r\n"}){
            string scopePath=Path.Combine(root,"provider-spacing-"+(newline.Length==1?"lf":"crlf")),spacingConfig=Path.Combine(scopePath,"config");Directory.CreateDirectory(spacingConfig);
            string basic="First line\n\n\nLast line",literal="Literal first\n\n\nLiteral last";
            string original=string.Join(newline,new[]{"model_provider = \"custom\"","# quotes in comments do not open strings: \"\"\" '''","[features]","keep = true","text = \"\"\""+basic.Replace("\n",newline)+"\"\"\"","literal = '''"+literal.Replace("\n",newline)+"'''","","","","[agents]","","","[model_providers.custom]","base_url = \"https://old.example/v1\"","experimental_bearer_token = \"fake-old-key\"",""});
            string spacingFile=Path.Combine(spacingConfig,"config.toml");File.WriteAllText(spacingFile,original);
            var spacingStore=new Store(Path.Combine(scopePath,"store"),spacingConfig,new Secure());
            var spacingProvider=spacingStore.Upsert(J.Map("name","spacing","baseUrl","https://spacing.example/v1","key","fake-spacing-key"));
            string firstWritten=null;
            for(int repeat=0;repeat<20;repeat++){
                spacingStore.Switch(J.Str(spacingProvider,"id"));string written=File.ReadAllText(spacingFile);
                if(firstWritten==null)firstWritten=written;else if(written!=firstWritten)throw J.Error("Repeated relay application must not accumulate blank lines.");
                var parsed=Nett.Toml.ReadString(written).ToDictionary();var features=J.Get(parsed,"features");
                if(!J.True(J.Get(features,"keep"))||J.Str(features,"text").Replace("\r\n","\n")!=basic||J.Str(features,"literal").Replace("\r\n","\n")!=literal||!J.Has(parsed,"agents"))throw J.Error("Whitespace cleanup must preserve unrelated settings, empty tables and multiline text.");
                if(!written.Contains("# quotes in comments do not open strings: \"\"\" '''")||written.Contains(newline+newline+newline+"[agents]")||Regex.Matches(written,@"(?m)^\[model_providers\.custom\]").Count!=1)throw J.Error("Provider spacing cleanup must keep comments and a single provider section.");
            }
            if(!firstWritten.Contains("[agents]"+newline+newline+"[model_providers.custom]"))throw J.Error("Configuration sections must have one separating blank line.");
            string backup=J.Str(J.Arr(J.Get(spacingStore.List(),"history")).Last(),"id");spacingStore.Restore(backup);
            if(File.ReadAllText(spacingFile)!=original)throw J.Error("Whitespace cleanup backup must restore the exact original configuration.");
        }
        checks.Add("repeated relay application has stable LF/CRLF spacing while preserving multiline strings, comments, empty tables and exact backups");
        string scope=Path.Combine(root,"active-endpoint"),configDir=Path.Combine(scope,"config");Directory.CreateDirectory(configDir);
        var endpointStore=new Store(Path.Combine(scope,"store"),configDir,new Secure());
        var first=endpointStore.Upsert(J.Map("name","first endpoint","baseUrl","https://active.example/v1","key","fake-first-key"));
        var second=endpointStore.Upsert(J.Map("name","other endpoint","baseUrl","https://other.example/v1","key","fake-other-key"));
        endpointStore.Switch(J.Str(second,"id"));
        string activeConfig="model_provider = \"External Provider\"\n[model_providers.\"External Provider\"]\nbase_url = \"https://active.example/v1\"\nexperimental_bearer_token = \"fake-external-key\"\n";
        File.WriteAllText(Path.Combine(configDir,"config.toml"),activeConfig);
        if(endpointStore.ActiveProviderId()!=J.Str(first,"id"))throw J.Error("unique configured endpoint remains active when credentials are managed externally");
        bool protectedActive=false;try{endpointStore.Delete(J.Str(first,"id"));}catch(Exception error){protectedActive=error.Message.Contains("先切换");}
        if(!protectedActive)throw J.Error("active endpoint deletion must use the current config instead of stale saved activeId");
        endpointStore.Delete(J.Str(second,"id"));
        var duplicate=endpointStore.Upsert(J.Map("name","same endpoint","baseUrl","https://active.example/v1","key","fake-second-key"));
        if(endpointStore.ActiveProviderId()!="")throw J.Error("multiple endpoint matches with unknown credentials remain ambiguous");
        File.WriteAllText(Path.Combine(configDir,"config.toml"),activeConfig.Replace("fake-external-key","fake-second-key"));
        if(endpointStore.ActiveProviderId()!=J.Str(duplicate,"id"))throw J.Error("exact credential match distinguishes relays sharing an endpoint");
        checks.Add("active endpoint fallback for external credentials, ambiguity protection and current-config deletion guard");
    }
}
}
