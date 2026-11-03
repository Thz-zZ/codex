using System;
using System.Collections.Generic;

namespace RelayLite {
internal static class TomlWhitespace {
    internal static string Compact(string source){
        string newline=source.Contains("\r\n")?"\r\n":"\n";
        var result=new List<string>();char quote='\0';bool multiline=false;
        foreach(string line in source.Split(new[]{"\r\n","\n"},StringSplitOptions.None)){
            if(!multiline&&string.IsNullOrWhiteSpace(line)){
                if(result.Count>0&&result[result.Count-1]!="")result.Add("");
                continue;
            }
            result.Add(line);
            for(int index=0;index<line.Length;index++){
                char current=line[index];
                if(quote=='\0'){
                    if(current=='#')break;
                    if(current!='"'&&current!='\'')continue;
                    quote=current;
                    multiline=index+2<line.Length&&line[index+1]==quote&&line[index+2]==quote;
                    if(multiline)index+=2;
                }else{
                    if(quote=='"'&&current=='\\'){index++;continue;}
                    if(current!=quote)continue;
                    if(multiline){
                        if(index+2>=line.Length||line[index+1]!=quote||line[index+2]!=quote)continue;
                        index+=2;
                        while(index+1<line.Length&&line[index+1]==quote)index++;
                    }
                    quote='\0';multiline=false;
                }
            }
        }
        while(result.Count>0&&result[result.Count-1]=="")result.RemoveAt(result.Count-1);
        return string.Join(newline,result)+(result.Count>0?newline:"");
    }
}
}
