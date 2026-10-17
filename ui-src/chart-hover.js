(function(root){
  'use strict';
  const escape=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const exact=value=>Number.isFinite(value)?value.toLocaleString('en-US'):'未提供';
  const money=value=>Number.isFinite(value)?'$'+value.toLocaleString('en-US',{minimumFractionDigits:4,maximumFractionDigits:4}):'未提供';
  const labelDate=value=>{const match=String(value).match(/^(\d{4})-(\d{2})-(\d{2})(.*)$/);return match?`${match[1]}年${Number(match[2])}月${Number(match[3])}日${match[4]}`:String(value);};
  class RelayChartHover {
    constructor(container,{getUsage,getRows,disabled=()=>false}){
      this.container=container;this.getUsage=getUsage;this.getRows=getRows;this.disabled=disabled;
      this.panel=document.createElement('div');this.panel.id='chartHoverPanel';this.panel.className='chart-hover-panel';this.panel.setAttribute('role','tooltip');this.panel.setAttribute('aria-hidden','true');document.body.appendChild(this.panel);
      const pointer=event=>this.pointer(event);
      container.addEventListener('pointerover',pointer);container.addEventListener('pointermove',pointer);
      container.addEventListener('pointerleave',()=>this.hide());
      container.addEventListener('focusin',event=>{const target=event.target.closest('[data-chart-index],[data-model-name]');if(!target)return;const rect=target.getBoundingClientRect();this.showTarget(target,rect.right,rect.top);});
      container.addEventListener('focusout',()=>this.hide());
      document.addEventListener('keydown',event=>{if(event.key==='Escape')this.hide();});
      window.addEventListener('scroll',()=>this.hide(),true);window.addEventListener('resize',()=>this.hide());window.addEventListener('blur',()=>this.hide());
      document.addEventListener('visibilitychange',()=>{if(document.hidden)this.hide();});
    }
    pointer(event){
      if(this.disabled()){this.hide();return;}
      const target=event.target.closest('[data-chart-index],[data-model-name]');if(!target||!this.container.contains(target)){this.hide();return;}
      this.showTarget(target,event.clientX,event.clientY);
    }
    showTarget(target,x,y){
      let row,title;
      if(target.hasAttribute('data-chart-index')){row=this.getRows()[Number(target.dataset.chartIndex)];title=row?labelDate(row.name)+'（北京时间）':'';}
      else{row=this.getUsage()?.models.find(row=>row.name===target.dataset.modelName);title=row?.name;}
      if(!row){this.hide();return;}
      if(this.target!==target){
        this.target?.removeAttribute('aria-describedby');this.target=target;target.setAttribute('aria-describedby',this.panel.id);
        const line=(label,value,color='',extra='')=>`<div class="chart-hover-row ${extra}"><span class="chart-hover-label">${color?`<span class="chart-hover-dot series-${color}"></span>`:''}${escape(label)}</span><span class="chart-hover-number${value==='未提供'?' chart-hover-unavailable':''}">${escape(value)}</span></div>`;
        this.panel.innerHTML=`<div class="chart-hover-date">${escape(title)}</div>`+line('输入 Token',exact(row.plain),'plain')+line('输出 Token',exact(row.output),'output')+line('缓存写入',exact(row.write),'write')+line('缓存读取',exact(row.read),'read')+line('缓存命中率',Number.isFinite(row.tokenRate)?(row.tokenRate*100).toFixed(2)+'%':'未提供','tokenRate')+line('请求次数',exact(row.requests))+line('实际消费',money(row.cost),'','chart-hover-cost')+line('标准费用',money(row.standardCost));
        this.container.querySelectorAll('.trend-point.is-hovered').forEach(point=>point.classList.remove('is-hovered'));
        const guide=this.container.querySelector('.trend-guide');if(guide){const trend=target.hasAttribute('data-chart-index');guide.classList.toggle('visible',trend);if(trend){guide.setAttribute('x1',target.dataset.x);guide.setAttribute('x2',target.dataset.x);for(const point of this.container.querySelectorAll('.trend-point'))point.classList.toggle('is-hovered',point.dataset.pointIndex===target.dataset.chartIndex);}}
      }
      this.panel.classList.add('visible');this.panel.setAttribute('aria-hidden','false');
      const width=this.panel.offsetWidth,height=this.panel.offsetHeight,gap=16;
      let left=x+gap,top=y+gap;if(left+width>innerWidth-12)left=x-width-gap;if(top+height>innerHeight-12)top=y-height-gap;
      this.panel.style.left=Math.max(12,Math.min(left,innerWidth-width-12))+'px';this.panel.style.top=Math.max(12,Math.min(top,innerHeight-height-12))+'px';
    }
    hide(){this.panel.classList.remove('visible');this.panel.setAttribute('aria-hidden','true');this.target?.removeAttribute('aria-describedby');this.target=null;this.container.querySelector('.trend-guide')?.classList.remove('visible');this.container.querySelectorAll('.trend-point.is-hovered').forEach(point=>point.classList.remove('is-hovered'));}
  }
  root.RelayChartHover=RelayChartHover;
})(globalThis);
