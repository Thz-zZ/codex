(function(root,factory){const api=factory();if(typeof module==='object'&&module.exports)module.exports=api;else root.RelayCharts=api;})(globalThis,()=>{
  'use strict';
  const esc=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const compact=value=>Number.isFinite(value)?Intl.NumberFormat('en-US',{notation:'compact',maximumFractionDigits:2}).format(value):'—';
  const number=value=>Number.isFinite(value)?Number(value).toLocaleString('en-US'):'—';
  const money=value=>Number.isFinite(value)?'$'+value.toFixed(4):'—';
  const colors=['#4386f5','#19b7a0','#9b7bea','#f3ae4f','#ed7f91','#4bb8d0','#7c92b6'];
  const series=[{id:'plain',name:'输入',color:'#4386f5'},{id:'output',name:'输出',color:'#1cba8d'},{id:'write',name:'缓存写入',color:'#f1ae3e'},{id:'read',name:'缓存读取',color:'#14b4cc'},{id:'tokenRate',name:'缓存命中率',color:'#9b73ef',rate:true}];
  function distribution(models){
    const rows=[...models].sort((a,b)=>(b.totalTokens||0)-(a.totalTokens||0)),total=rows.reduce((n,r)=>n+(r.totalTokens||0),0),circumference=2*Math.PI*68;
    let offset=0;
    const arcs=rows.map((row,i)=>{const length=total?(row.totalTokens||0)/total*circumference:0,arc=`<circle class="donut-segment" data-model-name="${esc(row.name)}" cx="90" cy="90" r="68" fill="none" stroke="${colors[i%colors.length]}" stroke-width="27" stroke-dasharray="${length} ${circumference-length}" stroke-dashoffset="${-offset}" transform="rotate(-90 90 90)" tabindex="0"></circle>`;offset+=length;return arc;}).join('');
    return `<div class="model-distribution"><div class="donut-wrap"><svg viewBox="0 0 180 180" role="img" aria-label="按模型的 Token 用量分布"><circle cx="90" cy="90" r="68" fill="none" stroke="#f0f3f8" stroke-width="27"/>${arcs}<text x="90" y="87" text-anchor="middle" class="donut-total">${total?compact(total):'—'}</text><text x="90" y="108" text-anchor="middle" class="donut-caption">总 Token</text></svg></div><div class="model-summary table-scroll">${rows.length?`<table><thead><tr><th>模型</th><th>请求</th><th>Token</th><th>实际消费</th><th>标准费用</th></tr></thead><tbody>${rows.map((row,i)=>`<tr><td data-tooltip="${esc(row.name)}"><span class="model-dot series-color-${i%colors.length}"></span>${esc(row.name)}</td><td>${number(row.requests)}</td><td>${compact(row.totalTokens)}</td><td class="cost-cell">${money(row.cost)}</td><td class="standard-cost">${money(row.standardCost)}</td></tr>`).join('')}</tbody></table>`:'<div class="chart-empty">所选日期没有模型用量</div>'}</div></div><p class="chart-footnote">按输入与输出 Token 分布 · 费用缺少字段时显示 —</p>`;
  }
  function trend(groups,{hidden=[],hourly=false}={}){
    const rows=[...groups].sort((a,b)=>a.name.localeCompare(b.name)),active=series.filter(s=>!hidden.includes(s.id));
    const legend=`<div class="chart-legend">${series.map(s=>`<button type="button" class="chart-toggle${hidden.includes(s.id)?' is-muted':''}" data-series="${s.id}" aria-pressed="${!hidden.includes(s.id)}"><span class="series-${s.id}"></span>${s.name}</button>`).join('')}</div>`;
    if(!rows.length)return legend+'<div class="chart-empty trend-empty">所选日期暂无可绘制的趋势数据</div>';
    const width=650,height=255,left=54,right=48,top=18,bottom=40,plotWidth=width-left-right,plotHeight=height-top-bottom;
    const maximum=Math.max(1,...rows.flatMap(row=>active.filter(s=>!s.rate).map(s=>Number.isFinite(row[s.id])?row[s.id]:0)));
    const power=10**Math.floor(Math.log10(maximum)),step=Math.ceil(maximum/power/4)*power,max=Math.max(step*4,1);
    const x=i=>left+(rows.length===1?plotWidth/2:i/(rows.length-1)*plotWidth),y=(v,isRate)=>top+plotHeight*(1-v/(isRate?1:max));
    const grid=Array.from({length:5},(_,i)=>{const yy=top+i*plotHeight/4;return `<path d="M${left} ${yy}H${width-right}" stroke="#e9edf4" stroke-width="1"/><text x="${left-9}" y="${yy+4}" text-anchor="end" class="chart-axis">${compact(max*(1-i/4))}</text><text x="${width-right+10}" y="${yy+4}" class="chart-axis rate-axis">${100-i*25}%</text>`;}).join('');
    const labelIndices=[...new Set(Array.from({length:Math.min(rows.length,5)},(_,i)=>Math.round(i*(rows.length-1)/Math.max(1,Math.min(rows.length,5)-1))))];
    const labels=labelIndices.map(i=>`<text x="${x(i)}" y="${height-13}" text-anchor="middle" class="chart-axis">${esc(hourly?rows[i].name.slice(11):rows[i].name.slice(5))}</text>`).join('');
    const lines=active.map(s=>{let started=false;const path=rows.map((row,i)=>{if(!Number.isFinite(row[s.id])){started=false;return '';}const segment=(started?'L':'M')+x(i)+' '+y(row[s.id],s.rate);started=true;return segment;}).join(' ');return `<path d="${path}" stroke="${s.color}" stroke-width="2.3" fill="none"${s.rate?' stroke-dasharray="5 4"':''}/>`+rows.map((row,i)=>Number.isFinite(row[s.id])?`<circle class="trend-point" data-point-index="${i}" cx="${x(i)}" cy="${y(row[s.id],s.rate)}" r="3.1" stroke="${s.color}" fill="white" stroke-width="1.8"></circle>`:'').join('');}).join('');
    const hitAreas=rows.map((row,i)=>{const start=i===0?left:(x(i-1)+x(i))/2,end=i===rows.length-1?width-right:(x(i)+x(i+1))/2;return `<rect class="trend-hit-area" data-chart-index="${i}" data-x="${x(i)}" x="${start}" y="${top}" width="${Math.max(1,end-start)}" height="${plotHeight+18}" tabindex="0" role="img" aria-label="${esc(row.name)}，查看当期用量详情"/>`;}).join('');
    return legend+`<svg class="trend-svg" viewBox="0 0 ${width} ${height}" role="img" aria-label="Token 用量与缓存命中率趋势，${hourly?'按小时':'按天'}"><text x="${left}" y="10" class="chart-axis">Token</text>${grid}${labels}<line class="trend-guide" x1="${left}" x2="${left}" y1="${top}" y2="${top+plotHeight}"/>${lines}${hitAreas}</svg><p class="chart-footnote">${hourly?'按小时':'按天'} · 缓存率使用右侧坐标 · 悬停查看数值，点击图例切换</p>`;
  }
  return {distribution,trend,series};
});
