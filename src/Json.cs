using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace RelayLite {
internal static class J {
    public static string Encode(object value) { return new JavaScriptSerializer { MaxJsonLength=16*1024*1024, RecursionLimit=100 }.Serialize(value); }
    public static object Decode(string value) { return new JavaScriptSerializer { MaxJsonLength=16*1024*1024, RecursionLimit=100 }.DeserializeObject(value.TrimStart('\uFEFF')); }
    public static Dictionary<string,object> Obj(object v) { return v as Dictionary<string,object> ?? new Dictionary<string,object>(); }
    public static Dictionary<string,object> Map(params object[] pairs) { var d=new Dictionary<string,object>(); for(int i=0;i<pairs.Length;i+=2)d[(string)pairs[i]]=pairs[i+1];return d; }
    public static object Get(object v, string k, object fallback=null) { object r;return Obj(v).TryGetValue(k,out r)?r:fallback; }
    public static string S(object v) { return v==null?"":Convert.ToString(v,CultureInfo.InvariantCulture); }
    public static string Str(object v,string k,string fallback="") { return S(Get(v,k,fallback)); }
    public static List<object> Arr(object v) { var a=v as IList;return a==null?new List<object>():a.Cast<object>().ToList(); }
    public static bool Has(object v,string k) { return Obj(v).ContainsKey(k); }
    public static bool True(object v) { return v is bool && (bool)v; }
    public static double? N(object v) { double n;if(v==null||v is bool||string.IsNullOrWhiteSpace(S(v))||!double.TryParse(S(v),NumberStyles.Float,CultureInfo.InvariantCulture,out n)||double.IsNaN(n)||double.IsInfinity(n))return null;return n; }
    public static double? Num(object v,string k) { return N(Get(v,k)); }
    public static double? Count(object v) { var n=N(v);return n.HasValue&&n.Value>=0&&n.Value<=9007199254740991d&&Math.Floor(n.Value)==n.Value?n:null; }
    public static double? Nonnegative(object v) { var n=N(v);return n.HasValue&&n>=0?n:null; }
    public static object Clone(object v) { return Decode(Encode(v)); }
    public static string Now() { return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",CultureInfo.InvariantCulture); }
    public static long Unix() { return (long)(DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds; }
    public static void Atomic(string file,string content) {
        Directory.CreateDirectory(Path.GetDirectoryName(file));string tmp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {File.WriteAllText(tmp,content,new UTF8Encoding(false));if(File.Exists(file))File.Replace(tmp,file,null);else File.Move(tmp,file);}finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    public static object Read(string file,object fallback) { return File.Exists(file)?Decode(File.ReadAllText(file,Encoding.UTF8)):fallback; }
    public static Exception Error(string s) { return new InvalidOperationException(s); }
}
}
