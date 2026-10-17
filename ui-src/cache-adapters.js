(function(root,factory){const api=factory();if(typeof module==='object'&&module.exports)module.exports=api;else root.RelayAdapters=api;})(globalThis,()=>{
  'use strict';
  const own=(o,k)=>Object.prototype.hasOwnProperty.call(o,k);
  const number=v=>(typeof v==='number'||typeof v==='string'&&v.trim()!=='')&&Number.isSafeInteger(Number(v))&&Number(v)>=0?Number(v):null;
  const day=seconds=>new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date(seconds*1000));
  function base(value){
    let raw=String(value||'').trim();if(!raw)throw new Error('请填写中转地址。');
    if(!/^[a-z][a-z\d+.-]*:\/\//i.test(raw))raw='https://'+raw;
    let url;try{url=new URL(raw);}catch{throw new Error('中转地址格式不正确，例如 https://example.com。');}
    const local=['localhost','127.0.0.1','[::1]'].includes(url.hostname);
    if(url.protocol!=='https:'&&!(url.protocol==='http:'&&local))throw new Error('请使用 HTTPS 中转地址；本机 localhost 可以使用 HTTP。');
    if(url.username||url.password||url.search||url.hash)throw new Error('地址只需域名和必要路径，请去掉账号、查询参数或 # 后的内容。');
    url.pathname=url.pathname.replace(/\/+$/,'').replace(/\/(?:api\/log\/token|v1\/usage)$/i,'').replace(/\/v1$/i,'').replace(/\/+$/,'');
    return url.href.replace(/\/$/,'');
  }
  function kind(value,choice='auto'){
    if(['newapi','sub2api'].includes(choice))return choice;
    if(choice!=='auto')throw new Error('未知的接口类型。');
    return /(^|\.)kuaiapi\.net$/i.test(new URL(base(value)).hostname)?'sub2api':'newapi';
  }
  function range(period='today',now=Date.now()){
    const today=day(now/1000);let start,end=today;
    if(typeof period==='string'&&period.startsWith('range:')){
      const parts=period.split(':');start=parts[1];end=parts[2];
      const valid=value=>/^\d{4}-\d{2}-\d{2}$/.test(value||'')&&Number.isFinite(Date.parse(value+'T00:00:00Z'))&&new Date(value+'T00:00:00Z').toISOString().slice(0,10)===value;
      if(parts.length!==3||!valid(start)||!valid(end)||start>end||end>today)throw new Error('请选择有效日期，结束日期不能早于开始日期或晚于今天。');
    }else{
      if(!['all','today','yesterday','7','30'].includes(period))throw new Error('未知的时间范围。');
      const days=period==='all'?90:period==='today'||period==='yesterday'?1:Number(period);
      end=period==='yesterday'?day(now/1000-86400):today;start=day(Date.parse(end+'T00:00:00+08:00')/1000-(days-1)*86400);
    }
    const days=Math.round((Date.parse(end)-Date.parse(start))/86400000)+1;
    if(days>90)throw new Error('单次查询最多选择 90 天。');
    return {period,days,start,end,timezone:'Asia/Shanghai'};
  }
  function endpoint(value,choice='newapi',period=null,now=Date.now()){
    const type=kind(value,choice),url=new URL(base(value)+(type==='sub2api'?'/v1/usage':'/api/log/token'));
    if(type==='sub2api'&&period!==null){const r=range(period,now);url.search=new URLSearchParams({start_date:r.start,end_date:r.end,days:String(r.days),timezone:r.timezone}).toString();}
    return url.href;
  }
  function normalizeAggregate(value,name=''){
    const v=value&&typeof value==='object'?value:{},plain=number(v.input_tokens),read=number(v.cache_read_tokens);
    const write=own(v,'cache_creation_tokens')?number(v.cache_creation_tokens):number(v.cache_write_tokens);
    const money=value=>(typeof value==='number'||typeof value==='string'&&value.trim()!=='')&&Number.isFinite(Number(value))&&Number(value)>=0?Number(value):null;
    const requests=number(v.requests),row={name,requests,read,write,output:number(v.output_tokens),cost:money(v.actual_cost),standardCost:money(v.total_cost??v.standard_cost),input:null,plain,valid:false,reason:''};
    if(plain===null||read===null||write===null){row.reason='缺少有效输入或缓存字段';return row;}
    const total=plain+read+write;if(!Number.isSafeInteger(total)){row.reason='Token 数量超出精确范围';return row;}
    if(own(v,'total_tokens')&&own(v,'output_tokens')){
      const full=number(v.total_tokens),out=number(v.output_tokens);
      if(full===null||out===null||full!==total+out){row.reason='总 Token 与分项数量不一致';return row;}
    }
    row.input=total;row.totalTokens=row.output===null?null:total+row.output;row.tokenRate=total>0?read/total:null;row.valid=true;return row;
  }
  function aggregate(groups){
    let input=0,read=0,write=0,requests=0,unknownRequests=false,excluded=0;
    for(const row of groups){if(row.requests===null)unknownRequests=true;else requests+=row.requests;if(row.valid){input+=row.input;read+=row.read;write+=row.write;}else excluded++;}
    const sum=field=>groups.some(row=>!Number.isFinite(row[field]))?null:groups.reduce((n,row)=>n+row[field],0),output=sum('output');
    return {input,read,write,output,plain:excluded?null:input-read-write,totalTokens:excluded||output===null?null:input+output,cost:sum('cost'),standardCost:sum('standardCost'),requests:unknownRequests?null:requests,excluded,tokenRate:input>0?read/input:null,requestRate:null};
  }
  function summary(payload,queryRange){
    const p=payload?.data&&typeof payload.data==='object'?payload.data:payload;
    if(!p||typeof p!=='object'||Array.isArray(p)||(!own(p,'usage')&&!own(p,'daily_usage')&&!own(p,'model_stats')))throw new Error('该接口没有返回缓存用量汇总，无法计算缓存率。');
    const models=Array.isArray(p.model_stats)?p.model_stats.map(v=>normalizeAggregate(v,String(v.model||'未记录模型'))):[];
    const daily=Array.isArray(p.daily_usage)?p.daily_usage.filter(v=>typeof v?.date==='string'&&/^\d{4}-\d{2}-\d{2}$/.test(v.date)&&v.date>=queryRange.start&&v.date<=queryRange.end).map(v=>normalizeAggregate(v,v.date)):[];
    let totals;
    if(models.length)totals=aggregate(models);
    else if(daily.length)totals=aggregate(daily);
    else if(queryRange.period==='today'&&p.usage?.today)totals=aggregate([normalizeAggregate(p.usage.today)]);
    else totals={input:null,read:null,write:null,requests:null,excluded:0,tokenRate:null,requestRate:null};
    return {models,daily,totals,range:queryRange,hasModels:Array.isArray(p.model_stats),hasDaily:Array.isArray(p.daily_usage)};
  }
  return {base,kind,day,range,endpoint,normalizeAggregate,aggregate,summary};
});
