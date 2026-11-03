using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RelayLite {
internal static class CodexProviderTestHost {
    static int Main(string[] args) {
        try {
            string directory=Path.GetFullPath(args[0]);Directory.CreateDirectory(directory);
            if(args.Length==2&&args[1]=="--check-links"){var checks=new List<string>();ModelCatalogChecks.RunLinked(directory,checks);Console.WriteLine(J.Encode(J.Map("ok",true,"checks",checks)));return 0;}
            if(args.Length==1){var checks=new List<string>();ModelCatalogChecks.Run(directory,checks);ModelsChecks.Run(directory,checks).GetAwaiter().GetResult();DraftChecks.Run(directory,checks).GetAwaiter().GetResult();CodexOnlyChecks.Run(directory,checks).GetAwaiter().GetResult();Console.WriteLine(J.Encode(J.Map("ok",true,"checks",checks)));return 0;}
            string config=Path.Combine(directory,"config");Directory.CreateDirectory(config);
            {
                var store=new Store(directory,config,new Secure());
                var input=J.Map("name","fixture provider","baseUrl",args[1],"protocol",args.Length>3?args[3]:"responses","adapter","none","model",args.Length>2?args[2]:"gpt-codex-fixture","reasoning",args.Length>4?args[4]:"high","key","fake-codex-cli-secret");
                if(args.Length>5){input["modelSelection"]="catalog";input["models"]=J.Decode(args[5]);}
                if(args.Length>6)input["contextPreset"]=args[6];
                input["modelFamily"]="codex";
                if(args.Length>7&&args[7]=="official"){
                    Func<object,string> segment=value=>Convert.ToBase64String(Encoding.UTF8.GetBytes(J.Encode(value))).TrimEnd('=').Replace('+','-').Replace('/','_');
                    string jwt=segment(J.Map("alg","none","typ","JWT"))+"."+segment(J.Map("exp",J.Unix()+3600,"email","fixture@example.test","sub","fixture-user","https://api.openai.com/auth",J.Map("chatgpt_account_id","fixture-account","chatgpt_plan_type","plus","user_id","fixture-user")))+".fixture";
                    J.Atomic(Path.Combine(config,"auth.json"),J.Encode(J.Map("auth_mode","chatgpt","tokens",J.Map("access_token",jwt,"id_token",jwt,"refresh_token","fake-official-refresh","account_id","fixture-account"),"last_refresh",J.Now())));
                }
                var provider=store.Upsert(input,true);
                store.Switch(J.Str(provider,"id"));Console.WriteLine(J.Encode(store.ConfigInfo()));Console.Out.Flush();Console.ReadLine();
            }
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
}
