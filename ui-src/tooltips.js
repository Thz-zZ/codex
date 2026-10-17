(function(root){
  'use strict';
  class RelayTooltips {
    constructor(){
      this.panel=document.createElement('div');this.panel.id='relayTooltip';this.panel.className='relay-tooltip';this.panel.setAttribute('role','tooltip');this.panel.setAttribute('popover','manual');this.panel.setAttribute('aria-hidden','true');document.body.appendChild(this.panel);
      const hover=event=>{if(document.body.classList.contains('sorting-providers')){this.hide();return;}const target=event.target.closest('[data-tooltip],button[aria-label]');if(!target||target.disabled){this.hide();return;}this.show(target);};
      document.addEventListener('pointerover',hover);document.addEventListener('pointermove',hover);
      document.addEventListener('pointerout',event=>{if(this.target&&!this.target.contains(event.relatedTarget))this.hide();});
      document.addEventListener('pointerdown',()=>this.hide(),true);
      document.addEventListener('focusin',event=>{if(event.target.matches(':focus-visible')&&(event.target.hasAttribute('data-tooltip')||event.target.matches('button[aria-label]')))this.show(event.target);});
      document.addEventListener('focusout',()=>this.hide());
      document.addEventListener('keydown',event=>{if(event.key==='Escape')this.hide();});
      window.addEventListener('scroll',()=>this.hide(),true);window.addEventListener('resize',()=>this.hide());window.addEventListener('blur',()=>this.hide());
      document.addEventListener('visibilitychange',()=>{if(document.hidden)this.hide();});
      // Future controls may still use title; migrate them before the OS tooltip
      // can open so that there is only one tooltip system throughout the app.
      const migrate=element=>{if(element.nodeType!==1)return;if(element.hasAttribute('title')){element.dataset.tooltip=element.getAttribute('title');element.removeAttribute('title');}for(const node of element.querySelectorAll('[title]')){node.dataset.tooltip=node.getAttribute('title');node.removeAttribute('title');}};
      migrate(document.body);
      this.observer=new MutationObserver(records=>{for(const record of records){if(record.type==='attributes')migrate(record.target);else for(const node of record.addedNodes)migrate(node);}if(this.target&&!this.target.isConnected)this.hide();});
      this.observer.observe(document.body,{subtree:true,childList:true,attributes:true,attributeFilter:['title']});
    }
    show(target){
      const text=target.dataset.tooltip||target.getAttribute('aria-label');if(!text?.trim()){this.hide();return;}
      if(this.target===target&&this.panel.textContent===text&&this.panel.matches(':popover-open'))return;
      this.hide();this.target=target;this.panel.textContent=text;this.panel.setAttribute('aria-hidden','false');
      const described=(target.getAttribute('aria-describedby')||'').split(/\s+/).filter(Boolean);if(!described.includes(this.panel.id))described.push(this.panel.id);target.setAttribute('aria-describedby',described.join(' '));
      this.panel.showPopover();
      const anchor=target.getBoundingClientRect(),width=this.panel.offsetWidth,height=this.panel.offsetHeight,gap=9;
      const left=Math.max(10,Math.min(anchor.left+(anchor.width-width)/2,innerWidth-width-10));
      let top=anchor.bottom+gap;if(top+height>innerHeight-10)top=anchor.top-height-gap;
      this.panel.style.left=left+'px';this.panel.style.top=Math.max(10,top)+'px';
    }
    hide(){
      if(this.panel.matches(':popover-open'))this.panel.hidePopover();this.panel.setAttribute('aria-hidden','true');
      if(this.target){const ids=(this.target.getAttribute('aria-describedby')||'').split(/\s+/).filter(id=>id&&id!==this.panel.id);if(ids.length)this.target.setAttribute('aria-describedby',ids.join(' '));else this.target.removeAttribute('aria-describedby');}
      this.target=null;
    }
  }
  root.RelayTooltips=RelayTooltips;
})(globalThis);
