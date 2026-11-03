(function(root,factory){const api=factory();if(typeof module==='object'&&module.exports)module.exports=api;else root.RelayAdapters=api;})(globalThis,()=>{
  'use strict';
  const day=seconds=>new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date(seconds*1000));
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
  return {day,range};
});
