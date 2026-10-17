(function(root){
  'use strict';
  // Pointer capture keeps the full card under our control instead of using the
  // platform drag thumbnail. FLIP animation preserves each neighbour's position.
  class AnimatedProviderSort {
    constructor(list,options){
      this.list=list;this.options=options;this.session=null;this.animations=new Map();
      list.addEventListener('pointerdown',event=>this.pointerDown(event));
      list.addEventListener('dragstart',event=>{if(event.target.closest('.drag-handle'))event.preventDefault();});
      document.addEventListener('pointermove',event=>this.pointerMove(event));
      document.addEventListener('pointerup',event=>this.pointerUp(event));
      document.addEventListener('pointercancel',event=>{if(this.session?.pointerId===event.pointerId)this.finish(true);});
      list.addEventListener('lostpointercapture',()=>{if(this.session&&!this.session.finishing)this.finish(true);});
      document.addEventListener('keydown',event=>{
        if(event.key==='Escape'&&this.session){event.preventDefault();this.finish(true);return;}
        const handle=event.target.closest('.drag-handle');
        if(handle&&list.contains(handle)&&event.altKey&&['ArrowUp','ArrowDown'].includes(event.key)){
          event.preventDefault();this.keyboardMove(handle.closest('[data-id]'),event.key==='ArrowUp'?-1:1);
        }
      });
      window.addEventListener('blur',()=>this.finish(true));
      window.addEventListener('resize',()=>this.finish(true));
      document.addEventListener('visibilitychange',()=>{if(document.hidden)this.finish(true);});
    }
    get busy(){return Boolean(this.session);}
    get reduced(){return matchMedia('(prefers-reduced-motion: reduce)').matches;}
    cards(){return [...this.list.children].filter(node=>node.matches('.provider-card[data-id]'));}
    order(){return this.cards().map(card=>card.dataset.id);}
    pointerDown(event){
      const handle=event.target.closest('.drag-handle');
      if(!handle||handle.disabled||event.button!==0||event.isPrimary===false||this.session||this.options.disabled?.())return;
      const card=handle.closest('.provider-card');if(!card)return;
      event.preventDefault();handle.focus({preventScroll:true});
      this.session={card,handle,id:card.dataset.id,pointerId:event.pointerId,startX:event.clientX,startY:event.clientY,x:event.clientX,y:event.clientY,active:false,finishing:false,original:this.cards()};
      try{this.list.setPointerCapture(event.pointerId);}catch{}
    }
    pointerMove(event){
      const s=this.session;if(!s||s.finishing||s.pointerId!==event.pointerId)return;
      if(!s.card.isConnected){this.finish(true);return;}
      s.x=event.clientX;s.y=event.clientY;
      if(!s.active&&Math.hypot(s.x-s.startX,s.y-s.startY)>=6)this.start(s);
      if(s.active){event.preventDefault();s.dirty=true;}
    }
    pointerUp(event){
      const s=this.session;if(!s||s.pointerId!==event.pointerId)return;
      s.x=event.clientX;s.y=event.clientY;if(s.active)this.update(s);
      this.finish(false);
    }
    start(s){
      s.active=true;s.rect=s.card.getBoundingClientRect();
      this.options.onBusy?.(s.id);
      const ghost=s.card.cloneNode(true);ghost.classList.add('sort-ghost');ghost.removeAttribute('data-id');ghost.setAttribute('aria-hidden','true');ghost.inert=true;
      for(const node of ghost.querySelectorAll('[id]'))node.removeAttribute('id');
      Object.assign(ghost.style,{left:s.rect.left+'px',top:s.rect.top+'px',width:s.rect.width+'px',height:s.rect.height+'px'});
      document.body.appendChild(ghost);s.ghost=ghost;s.card.classList.add('sort-placeholder');s.card.inert=true;
      document.body.classList.add('sorting-providers');this.list.classList.add('sort-active');
      if(!this.reduced)ghost.animate([{transform:'scale(1) rotate(0deg)',boxShadow:'0 2px 3px #27324805'},{transform:'scale(1.012) rotate(.25deg)',boxShadow:'0 18px 42px #25364a22'}],{duration:150,easing:'cubic-bezier(.2,.8,.2,1)'});
      s.dirty=true;s.lastFrame=performance.now();
      const frame=now=>{
        if(this.session!==s||s.finishing)return;
        const dt=Math.min(32,now-s.lastFrame);s.lastFrame=now;
        const edge=40,scroller=this.list.scrollHeight>this.list.clientHeight?this.list:document.scrollingElement,bounds=scroller===this.list?this.list.getBoundingClientRect():{top:0,bottom:innerHeight};
        let speed=0;if(s.y<bounds.top+edge)speed=-Math.min(16,(bounds.top+edge-s.y)*.24);else if(s.y>bounds.bottom-edge)speed=Math.min(16,(s.y-bounds.bottom+edge)*.24);
        if(speed&&scroller){const before=scroller.scrollTop;scroller.scrollTop+=speed*dt/16.67;if(scroller.scrollTop!==before)s.dirty=true;}
        if(s.dirty){this.update(s);s.dirty=false;}s.frame=requestAnimationFrame(frame);
      };
      s.frame=requestAnimationFrame(frame);
    }
    animateLayout(mutate,exclude=null){
      const cards=this.cards(),before=new Map(cards.map(card=>[card,card.getBoundingClientRect().top]));
      mutate();const running=[];
      for(const card of cards){
        this.animations.get(card)?.cancel();this.animations.delete(card);
        if(card===exclude||this.reduced)continue;
        const delta=before.get(card)-card.getBoundingClientRect().top;if(Math.abs(delta)<.5)continue;
        const animation=card.animate([{transform:`translateY(${delta}px)`},{transform:'translateY(0)'}],{duration:240,easing:'cubic-bezier(.2,.8,.2,1)'});
        this.animations.set(card,animation);running.push(animation.finished.catch(()=>{}));
        animation.finished.then(()=>{if(this.animations.get(card)===animation)this.animations.delete(card);}).catch(()=>{});
      }
      return Promise.all(running);
    }
    update(s){
      if(!s.active||!s.ghost||s.finishing)return;
      const dx=Math.max(-22,Math.min(22,s.x-s.startX)),dy=s.y-s.startY;
      s.ghost.style.translate=`${dx}px ${dy}px`;
      const centre=s.rect.top+dy+s.rect.height/2,listTop=this.list.getBoundingClientRect().top;
      const before=this.cards().find(card=>card!==s.card&&centre<listTop+card.offsetTop-this.list.scrollTop+card.offsetHeight/2)||null;
      if(s.card.nextElementSibling!==before)this.animateLayout(()=>this.list.insertBefore(s.card,before),s.card);
    }
    async finish(cancelled){
      const s=this.session;if(!s||s.finishing)return;s.finishing=true;
      cancelAnimationFrame(s.frame);try{this.list.releasePointerCapture(s.pointerId);}catch{}
      if(!s.active){this.session=null;return;}
      if(cancelled)this.animateLayout(()=>{for(const card of s.original)if(card.isConnected)this.list.appendChild(card);},s.card);
      try{
        if(s.ghost){const target=s.card.getBoundingClientRect(),landing=s.ghost.animate([
          {translate:s.ghost.style.translate||'0px 0px',transform:this.reduced?'none':'scale(1.012) rotate(.25deg)',boxShadow:'0 18px 42px #25364a22'},
          {translate:`${target.left-s.rect.left}px ${target.top-s.rect.top}px`,transform:'scale(1) rotate(0deg)',boxShadow:'0 2px 4px #25364a06'}
        ],{duration:this.reduced?0:210,easing:'cubic-bezier(.2,.8,.2,1)',fill:'forwards'});await landing.finished.catch(()=>{});}
        if(!cancelled)await this.options.onCommit?.(this.order());
      }catch(error){this.options.onError?.(error);}
      finally{
        for(const animation of this.animations.values())animation.cancel();this.animations.clear();
        s.ghost?.remove();s.card.classList.remove('sort-placeholder');s.card.inert=false;
        this.list.classList.remove('sort-active');document.body.classList.remove('sorting-providers');this.session=null;
        this.options.onBusy?.(null);this.options.onSettled?.(s.id,cancelled,false);
      }
    }
    async keyboardMove(card,offset){
      if(!card||this.session||this.options.disabled?.())return;
      const cards=this.cards(),index=cards.indexOf(card),target=index+offset;if(target<0||target>=cards.length)return;
      const id=card.dataset.id,s={card,id,active:true,finishing:true};this.session=s;this.options.onBusy?.(id);
      try{
        await this.animateLayout(()=>this.list.insertBefore(card,offset<0?cards[target]:cards[target].nextSibling));
        await this.options.onCommit?.(this.order());
      }catch(error){this.options.onError?.(error);}
      finally{this.session=null;this.options.onBusy?.(null);this.options.onSettled?.(id,false,true);}
    }
  }
  root.AnimatedProviderSort=AnimatedProviderSort;
})(globalThis);
