using System;
using System.Collections.Generic;
using System.IO;

namespace RelayLite {
internal static class CodexProviderTestHost {
    static int Main(string[] args) {
        try {
            string directory=Path.GetFullPath(args[0]);Directory.CreateDirectory(directory);
            if(args.Length==1){var checks=new List<string>();ModelCatalogChecks.Run(directory,checks);ModelsChecks.Run(directory,checks).GetAwaiter().GetResult();DraftChecks.Run(directory,checks).GetAwaiter().GetResult();CodexOnlyChecks.Run(directory,checks).GetAwaiter().GetResult();Console.WriteLine(J.Encode(J.Map("ok",true,"checks",checks)));return 0;}
            string config=Path.Combine(directory,"config");Directory.CreateDirectory(config);
            {
                var store=new Store(directory,config,new Secure());
                var input=J.Map("name","fixture provider","baseUrl",args[1],"protocol",args.Length>3?args[3]:"responses","adapter","none","model",args.Length>2?args[2]:"gpt-codex-fixture","reasoning",args.Length>4?args[4]:"high","key","fake-codex-cli-secret");
                if(args.Length>5){input["modelSelection"]="catalog";input["models"]=J.Decode(args[5]);}
                input["modelFamily"]="codex";
                var provider=store.Upsert(input,true);
                store.Switch(J.Str(provider,"id"));Console.WriteLine(J.Encode(store.ConfigInfo()));Console.Out.Flush();Console.ReadLine();
            }
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
}
