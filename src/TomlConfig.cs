using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RelayLite {
// Nett validates the document; these spans let us edit individual values without
// reserializing unrelated settings, comments, arrays or multiline strings.
internal static class TomlConfig {
    sealed class Span {
        internal int Start,End,ValueStart,ValueEnd;
        internal string[] Path;
        internal bool Header;
    }
    internal static Dictionary<string,object> Parse(string text){return Nett.Toml.ReadString(text).ToDictionary();}
    internal static string Normalize(string text){
        var spans=Scan(text);var parent=new[]{"model_providers"};
        foreach(var header in spans.Where(span=>span.Header&&span.Path.SequenceEqual(parent)).OrderByDescending(span=>span.Start)){
            bool implicitParent=spans.Any(span=>span.Start<header.Start&&span.Path.Length>1&&Prefix(parent,span.Path));
            var next=spans.FirstOrDefault(span=>span.Header&&span.Start>header.Start);
            bool empty=!spans.Any(span=>!span.Header&&span.Start>header.Start&&(next==null||span.Start<next.Start));
            if(implicitParent&&empty)text=text.Remove(header.Start,header.End-header.Start);
        }
        return text;
    }
    static bool Prefix(string[] left,string[] right){return left.Length<=right.Length&&left.SequenceEqual(right.Take(left.Length));}
    static string[] KeyPath(string key){
        var path=new List<string>();object value=Parse(key+" = true\n");
        while(value is Dictionary<string,object>){var row=J.Obj(value).Single();path.Add(row.Key);value=row.Value;}
        return path.ToArray();
    }
    static List<Span> Scan(string text){
        var spans=new List<Span>();string[] section=new string[0];int start=0,depth=0;char quote='\0';bool triple=false,comment=false;
        for(int i=0;i<=text.Length;i++){
            char c=i<text.Length?text[i]:'\n';
            if(comment){if(c!='\n')continue;comment=false;}
            else if(quote!='\0'){
                if(c=='\\'&&quote=='"'){i++;continue;}
                if(c==quote){
                    if(triple){if(i+2>=text.Length||text[i+1]!=quote||text[i+2]!=quote)continue;i+=2;while(i+1<text.Length&&text[i+1]==quote)i++;}
                    quote='\0';triple=false;
                }
                continue;
            }else if(c=='#'){comment=true;continue;}
            else if(c=='"'||c=='\''){quote=c;triple=i+2<text.Length&&text[i+1]==c&&text[i+2]==c;if(triple)i+=2;continue;}
            else if(c=='['||c=='{')depth++;
            else if(c==']'||c=='}')depth--;
            if(c!='\n'||depth!=0)continue;
            int end=Math.Min(i+1,text.Length),first=start;while(first<i&&char.IsWhiteSpace(text[first]))first++;
            if(first<i&&text[first]!='#'){
                if(text[first]=='['){
                    int close=first+1;char keyQuote='\0';
                    for(;close<i;close++){
                        char k=text[close];if(keyQuote=='"'&&k=='\\'){close++;continue;}
                        if(keyQuote!='\0'){if(k==keyQuote)keyQuote='\0';}
                        else if(k=='"'||k=='\'')keyQuote=k;else if(k==']')break;
                    }
                    bool array=text[first+1]=='[';
                    section=KeyPath(text.Substring(first+(array?2:1),close-first-(array?2:1)));
                    spans.Add(new Span{Start=start,End=end,Path=section,Header=true});
                }else{
                    int equals=first;char keyQuote='\0';
                    for(;equals<i;equals++){
                        char k=text[equals];if(keyQuote=='"'&&k=='\\'){equals++;continue;}
                        if(keyQuote!='\0'){if(k==keyQuote)keyQuote='\0';}
                        else if(k=='"'||k=='\'')keyQuote=k;else if(k=='=')break;
                    }
                    int valueStart=equals+1;while(valueStart<i&&char.IsWhiteSpace(text[valueStart]))valueStart++;
                    int valueEnd=ValueEnd(text,valueStart,i);
                    spans.Add(new Span{Start=start,End=end,ValueStart=valueStart,ValueEnd=valueEnd,Path=section.Concat(KeyPath(text.Substring(first,equals-first))).ToArray()});
                }
            }
            start=end;
        }
        return spans;
    }
    static int ValueEnd(string text,int start,int end){
        char quote='\0';bool triple=false,comment=false;int depth=0,last=start;
        for(int i=start;i<end;i++){
            char c=text[i];
            if(comment){if(c=='\n')comment=false;continue;}
            if(quote!='\0'){
                if(c=='\\'&&quote=='"'){last=Math.Min(end,i+2);i++;continue;}
                if(c==quote){if(triple){if(i+2>=end||text[i+1]!=quote||text[i+2]!=quote){last=i+1;continue;}i+=2;while(i+1<end&&text[i+1]==quote)i++;}quote='\0';triple=false;}
                last=i+1;continue;
            }
            if(c=='#'){if(depth==0)break;comment=true;continue;}
            if(c=='"'||c=='\''){quote=c;triple=i+2<end&&text[i+1]==c&&text[i+2]==c;if(triple)i+=2;}
            else if(c=='['||c=='{')depth++;else if(c==']'||c=='}')depth--;
            if(!char.IsWhiteSpace(c))last=i+1;
        }
        return last;
    }
    internal static string Literal(object value){
        if(value is string)return J.Encode(value);
        if(value is bool)return (bool)value?"true":"false";
        var map=value as Dictionary<string,object>;
        if(map!=null)return "{ "+string.Join(", ",map.Select(pair=>J.Encode(pair.Key)+" = "+Literal(pair.Value)))+" }";
        if(value is IList)return "["+string.Join(", ",J.Arr(value).Select(Literal))+"]";
        if(value is DateTimeOffset)return ((DateTimeOffset)value).ToString("o",CultureInfo.InvariantCulture);
        if(value is DateTime)return ((DateTime)value).ToString("o",CultureInfo.InvariantCulture);
        if(value==null)throw J.Error("TOML 设置不能写入空值。");
        return Convert.ToString(value,CultureInfo.InvariantCulture);
    }
    static void Mutate(Dictionary<string,object> map,string[] path,object value,bool remove){
        for(int i=0;i<path.Length-1;i++){if(!J.Has(map,path[i]))map[path[i]]=new Dictionary<string,object>();map=J.Obj(map[path[i]]);}
        if(remove)map.Remove(path.Last());else map[path.Last()]=value;
    }
    internal static string Set(string text,string[] path,object value,bool remove=false){
        var spans=Scan(text);
        var ancestor=spans.FirstOrDefault(span=>!span.Header&&Prefix(span.Path,path));
        if(ancestor!=null){
            if(ancestor.Path.Length==path.Length){
                if(remove)return text.Remove(ancestor.Start,ancestor.End-ancestor.Start);
                return text.Substring(0,ancestor.ValueStart)+Literal(value)+text.Substring(ancestor.ValueEnd);
            }
            object existing=Parse(text);foreach(string part in ancestor.Path)existing=J.Get(existing,part);
            var changed=J.Obj(existing);Mutate(changed,path.Skip(ancestor.Path.Length).ToArray(),value,remove);
            return text.Substring(0,ancestor.ValueStart)+Literal(changed)+text.Substring(ancestor.ValueEnd);
        }
        if(remove){
            foreach(var span in spans.Where(span=>Prefix(path,span.Path)).OrderByDescending(span=>span.Start))text=text.Remove(span.Start,span.End-span.Start);
            return text;
        }
        if(value is Dictionary<string,object>&&spans.Any(span=>span.Header&&span.Path.SequenceEqual(path))){
            object previous=Parse(text);foreach(string part in path)previous=J.Get(previous,part);
            var values=J.Obj(value);
            foreach(string field in J.Obj(previous).Keys.Where(field=>!values.ContainsKey(field)).ToArray())text=Set(text,path.Concat(new[]{field}).ToArray(),null,true);
            foreach(var field in values)text=Set(text,path.Concat(new[]{field.Key}).ToArray(),field.Value);
            return text;
        }
        // Reuse the nearest explicit parent table. Dotted keys also work when
        // the parent is implicit, so no duplicate table is introduced.
        var parent=spans.Where(span=>span.Header&&span.Path.Length<path.Length&&Prefix(span.Path,path)).OrderByDescending(span=>span.Path.Length).FirstOrDefault();
        int at=parent==null?(spans.FirstOrDefault(span=>span.Header)?.Start??text.Length):(spans.FirstOrDefault(span=>span.Header&&span.Start>parent.Start)?.Start??text.Length);
        string newline=text.Contains("\r\n")?"\r\n":"\n";
        string key=string.Join(".",path.Skip(parent?.Path.Length??0).Select(part=>System.Text.RegularExpressions.Regex.IsMatch(part,@"^[A-Za-z0-9_-]+$")?part:J.Encode(part)));
        return text.Insert(at,(at>0&&text[at-1]!='\n'?newline:"")+key+" = "+Literal(value)+newline);
    }
    internal static string Table(string text,string[] path,Dictionary<string,object> values){
        // Inline/dotted parents must be updated in place instead of redeclared.
        if(Scan(text).Any(span=>!span.Header&&Prefix(span.Path,path)))return Set(text,path,values);
        text=Set(text,path,null,true);string nl=text.Contains("\r\n")?"\r\n":"\n";
        return text+nl+"["+string.Join(".",path.Select(part=>System.Text.RegularExpressions.Regex.IsMatch(part,@"^[A-Za-z0-9_-]+$")?part:J.Encode(part)))+"]"+nl+string.Join(nl,values.Select(pair=>(System.Text.RegularExpressions.Regex.IsMatch(pair.Key,@"^[A-Za-z0-9_-]+$")?pair.Key:J.Encode(pair.Key))+" = "+Literal(pair.Value)))+nl;
    }
}
}
