using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
namespace RelayLite {
internal static class CompatibilityTests {
    static string Canonical(object value) {
        var obj=value as Dictionary<string,object>;if(obj!=null)return "{"+string.Join(",",obj.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>J.Encode(x.Key)+":"+Canonical(x.Value)))+"}";
        if(value is System.Collections.IList)return "["+string.Join(",",J.Arr(value).Select(Canonical))+"]";
        var number=J.N(value);if(number.HasValue)return number.Value.ToString("G9",System.Globalization.CultureInfo.InvariantCulture);
        return J.Encode(value);
    }
    public static void Run(List<string> checks,string root) {
        WindowPlacementChecks.Run(root,checks);
        if(MainForm.BalanceRefreshInterval!=5*60*1000)throw J.Error("Tray balance refresh must run every five minutes.");
        if(MainForm.LocalRefreshInterval!=60*1000)throw J.Error("Tray local records must refresh every minute.");
        checks.Add("independent five-minute balance and one-minute local refresh cadences");
        string refreshDirectory=Path.Combine(root,"refresh-preference");var refreshStore=new Store(refreshDirectory,Path.Combine(root,"fake-codex"),new Secure());
        J.Obj(J.Get(refreshStore.Data,"appSettings"))["autoRefresh"]=false;refreshStore.Save();
        refreshStore=new Store(refreshDirectory,refreshStore.ConfigDir,new Secure());
        if(J.Has(J.Get(refreshStore.Data,"appSettings"),"autoRefresh"))throw J.Error("Removed pause preference must not disable ongoing automatic refresh.");
        var legacyBalance=refreshStore.Upsert(J.Map("name","legacy balance","baseUrl","https://legacy-balance.example","key","fixture-legacy-balance"));
        if(J.Str(legacyBalance,"adapter")!="none")throw J.Error("New providers must default to disabled balance queries.");
        var legacyProvider=J.Obj(J.Arr(J.Get(refreshStore.Data,"providers"))[0]);legacyProvider["adapter"]="auto";legacyProvider["detectedAdapter"]="newapi";string previousRevision=J.Str(legacyProvider,"revision");refreshStore.Save();
        refreshStore=new Store(refreshDirectory,refreshStore.ConfigDir,new Secure());legacyBalance=refreshStore.Details(J.Str(legacyBalance,"id"));
        if(J.Str(legacyBalance,"adapter")!="none"||J.Has(legacyBalance,"detectedAdapter")||J.Str(legacyBalance,"revision")==previousRevision)throw J.Error("Legacy automatic balance queries must be disabled and cached balances invalidated.");
        bool rejectedAuto=false;try{refreshStore.Upsert(J.Map("name","invalid automatic type","baseUrl","https://invalid-balance.example","key","fixture-invalid-balance","adapter","auto"));}catch{rejectedAuto=true;}
        if(!rejectedAuto)throw J.Error("Saving an automatic balance adapter must be rejected.");
        checks.Add("removed pause setting migration, default-off balance queries, explicit types and legacy auto migration");
        string fixtureFile=Path.Combine(Program.BaseDir,"usage-fixtures.json");if(!File.Exists(fixtureFile))throw J.Error("Run tests/generate-fixtures.cjs before testing.");
        var fixtures=J.Arr(J.Read(fixtureFile,null));foreach(var fixture in fixtures){string adapter=J.Str(fixture,"adapter"),period=J.Str(fixture,"period"),model=J.Str(fixture,"model");object actual=adapter=="newapi"?Usage.NewUsage(J.Get(fixture,"payload"),period,model,J.Num(fixture,"divisor")??500000):Usage.SubUsage(J.Get(fixture,"payload"),period,model);object expected=J.Get(fixture,"expected");if(Canonical(actual)!=Canonical(expected)){J.Atomic(Path.Combine(Program.BaseDir,"compatibility-diff.json"),J.Encode(J.Map("name",J.Str(fixture,"name"),"actual",actual,"expected",expected)));throw J.Error("Statistics parity: "+J.Str(fixture,"name"));}}
        checks.Add(fixtures.Count+" statistics parity fixtures");
        var dates=Usage.Range("today",new DateTime(2026,10,16,16,1,0,DateTimeKind.Utc));if(J.Str(dates,"start")!="2026-10-17")throw J.Error("Beijing midnight");checks.Add("Beijing date boundaries");
        var plain=new byte[16];var key=new byte[16];var nonce=new byte[12];var cipher=Hex("0388dace60b6a392f328c2b971b2fe78");var tag=Hex("ab6e47d42cec13bdf53a67b21257bddf");if(!Secure.GcmDecrypt(key,nonce,cipher,tag).SequenceEqual(plain))throw J.Error("AES-GCM compatibility");checks.Add("legacy AES-GCM authentication");
        byte[] encryptedKey=ProtectedData.Protect(key,null,DataProtectionScope.CurrentUser);byte[] prefixed=Encoding.ASCII.GetBytes("DPAPI").Concat(encryptedKey).ToArray();var state=J.Map("os_crypt",J.Map("encrypted_key",Convert.ToBase64String(prefixed)));J.Atomic(Path.Combine(root,"Local State"),J.Encode(state));var electron=new Secure(Path.Combine(root,"Local State"));byte[] legacyCipher=Encoding.ASCII.GetBytes("v10").Concat(nonce).Concat(cipher).Concat(tag).ToArray();string legacyValue=Convert.ToBase64String(legacyCipher);if(electron.Decrypt(legacyValue)!=new string('\0',16))throw J.Error("Electron v10 AES-GCM and Local State key");checks.Add("Electron v10 secret migration");
        string legacy=Path.Combine(root,"legacy");Directory.CreateDirectory(legacy);var secure=new Secure();string secret="fake-migration-key-only";J.Atomic(Path.Combine(legacy,"providers.json"),J.Encode(J.Map("version",1,"providers",new[]{J.Map("id","fixture","name","legacy","baseUrl","https://legacy.example/v1","siteUrl","https://legacy.example","keyEncrypted",Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret),null,DataProtectionScope.CurrentUser))),},"history",new object[0],"snapshots",new object[0],"configDir",Path.Combine(root,"fake-codex"))));var migrated=new Store(legacy,Path.Combine(root,"fake-codex"),secure);if(migrated.Secret("fixture")!=secret||!File.ReadAllText(migrated.File).Contains("dpapi1:"))throw J.Error("legacy DPAPI migration");checks.Add("legacy DPAPI migration");
        NetworkChecks(root,checks).GetAwaiter().GetResult();
        NetworkFailureChecks.Run(root,checks).GetAwaiter().GetResult();
        DraftChecks.Run(root,checks).GetAwaiter().GetResult();
        ProviderIdChecks.Run(root,checks);
        ConfigChecks.Run(root,checks);
        UsageCacheChecks.Run(root,checks);
        BalanceChecks.Run(root,checks).GetAwaiter().GetResult();
        ConnectionChecks.Run(checks).GetAwaiter().GetResult();
        GenerationChecks.Run(checks).GetAwaiter().GetResult();
        ModelsChecks.Run(root,checks).GetAwaiter().GetResult();
        ModelCatalogChecks.Run(root,checks);
        CodexOnlyChecks.Run(root,checks).GetAwaiter().GetResult();
        UpdateChecks.Run(root,checks).GetAwaiter().GetResult();
        if(System.Net.ServicePointManager.SecurityProtocol!=System.Net.SecurityProtocolType.Tls12)throw J.Error("Modern TLS not initialized");
        string networkError=Usage.ErrorText(new System.Net.Http.HttpRequestException("发送请求时出错。",new System.Net.WebException("certificate fixture-secret",null,System.Net.WebExceptionStatus.TrustFailure,null)),"fixture-secret");
        if(!networkError.Contains("HTTPS")||!networkError.Contains("certificate")||networkError.Contains("fixture-secret"))throw J.Error("Network causes must be visible and redacted");
        if(!Usage.ErrorText(new System.Threading.Tasks.TaskCanceledException()).Contains("连接超时"))throw J.Error("Network timeout hint");
        checks.Add("TLS 1.2 and redacted nested network errors");
    }
    static byte[] Hex(string s){return Enumerable.Range(0,s.Length/2).Select(i=>Convert.ToByte(s.Substring(i*2,2),16)).ToArray();}
    static async Task NetworkChecks(string root,List<string> checks){
        string config=Path.Combine(root,"fake-codex");Directory.CreateDirectory(config);var store=new Store(Path.Combine(root,"network-store"),config,new Secure());var p=store.Upsert(J.Map("name","explicit fixture","baseUrl","https://arbitrary.example/v1","siteUrl","https://arbitrary.example","key","fixture-secret-key","adapter","sub2api"));string id=J.Str(p,"id");var paths=new List<string>();
        Usage.FetchOverride=(url,key,user)=>{paths.Add(new Uri(url).AbsolutePath);if(url.Contains("/api/log/token"))throw J.Error("fixture 404");return Task.FromResult<object>(J.Map("mode","unrestricted","balance",0,"model_stats",new object[0],"daily_usage",new object[0]));};
        try {var result=await Usage.Inspect(store,id,"today");if(J.Has(result,"history")||J.Arr(store.Snapshots(id)).Count==0)throw J.Error("inspect excludes history while snapshots remain available");checks.Add("lean inspection responses preserve stored snapshots");if(J.Str(result,"adapter")!="sub2api"||J.Num(J.Get(result,"balance"),"value")!=0)throw J.Error("explicit type and zero balance");paths.Clear();await Usage.Inspect(store,id,"today");if(paths.Count!=1||paths[0]!="/v1/usage")throw J.Error("explicit adapter routing");checks.Add("explicit adapter routing without detection");
            store.Upsert(J.Map("id",id,"name","new","baseUrl","https://new.example/v1","siteUrl","https://new.example","key","fixture-new-key","adapter","newapi"));Usage.FetchOverride=(url,key,user)=>Task.FromResult<object>(url.EndsWith("/api/log/token")?J.Map("success",true,"data",new[]{J.Map("type",2,"model_name","m","prompt_tokens",1000,"completion_tokens",30,"quota",100,"created_at",J.Unix(),"other",J.Map("cache_tokens",800))}):J.Map("success",true,"data",J.Map("quota_per_unit",250000,"user_quota",J.Map("total_available",500000))));result=await Usage.Inspect(store,id,"today");if(J.Num(J.Get(result,"balance"),"value")!=2||J.Num(J.Get(J.Get(result,"usage"),"totals"),"tokenRate")!=.8)throw J.Error("NewAPI balance and quota conversion");checks.Add("NewAPI account balance and quota conversion");
            store.Upsert(J.Map("id",id,"name","unknown","baseUrl","https://unknown.example/v1","siteUrl","https://unknown.example","key","fixture-unknown-key","adapter","sub2api"));Usage.FetchOverride=(url,key,user)=>{throw J.Error("denied fixture-unknown-key");};result=await Usage.Inspect(store,id,"today");if(J.Str(result,"adapter")!="sub2api"||J.Encode(result).Contains("fixture-unknown-key"))throw J.Error("failed request preserves the selected type");checks.Add("failed authentication keeps selected type and redacts credentials");
        }finally{Usage.FetchOverride=null;}
    }
}
}
