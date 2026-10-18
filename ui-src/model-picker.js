'use strict';
class RelayModelPicker {
  constructor({fetch,getCredentials,isOpen}) {
    this.fetch=fetch;this.getCredentials=getCredentials;this.isOpen=isOpen;
    this.input=document.getElementById('modelName');this.menu=document.getElementById('modelMenu');this.button=document.getElementById('fetchModels');this.status=document.getElementById('modelStatus');
    this.models=[];this.epoch=0;this.active=0;this.loaded=false;this.discoveryToken='';
    this.button.addEventListener('click',()=>this.load());
    document.getElementById('toggleModels').addEventListener('click',()=>this.menu.classList.contains('hidden')?this.open(false):this.close());
    this.input.addEventListener('input',()=>{if(this.loaded)this.open(true);});
    this.input.addEventListener('keydown',event=>{
      if(event.key==='Tab'){this.close();return;}
      if(event.key==='Escape'&&!this.menu.classList.contains('hidden')){event.preventDefault();event.stopPropagation();this.close();return;}
      if(event.key==='ArrowDown'||event.key==='ArrowUp'){
        event.preventDefault();if(this.menu.classList.contains('hidden')){this.open(false);return;}
        if(!this.options.length)return;
        this.active=this.active<0?(event.key==='ArrowDown'?0:this.options.length-1):(this.active+(event.key==='ArrowDown'?1:-1)+this.options.length)%this.options.length;this.highlight();
      }else if(event.key==='Enter'&&!this.menu.classList.contains('hidden')){event.preventDefault();if(this.active>=0)this.choose(this.options[this.active].id);else this.close();}
    });
    this.menu.addEventListener('mousedown',event=>event.preventDefault());
    this.menu.addEventListener('click',event=>{const option=event.target.closest('[data-model-index]');if(option)this.choose(this.options[Number(option.dataset.modelIndex)].id);});
    document.addEventListener('pointerdown',event=>{if(!event.target.closest('.model-picker')&&event.target!==this.button)this.close();});
    document.addEventListener('relay-dropdown-open',event=>{if(event.detail!=='modelName')this.close();});
    this.input.closest('.model-picker').addEventListener('focusout',event=>{if(!event.currentTarget.contains(event.relatedTarget))this.close();});
    for(const id of ['baseUrl','apiKey'])document.getElementById(id).addEventListener('input',()=>this.reset('地址或密钥已变更，请重新获取模型。'));
  }
  reset(message='填写地址和密钥后获取全部模型，并选择默认模型。') {
    this.epoch++;this.models=[];this.loaded=false;this.discoveryToken='';this.button.disabled=false;this.button.textContent='↻ 获取模型';this.status.textContent=message;this.status.classList.remove('error');this.close();
  }
  restore(provider){if(provider?.modelSelection==='catalog'&&provider.models?.length){this.models=[...provider.models];this.loaded=true;this.status.textContent='已保存 '+this.models.length+' 个模型，默认项可更改；列表有变化时请重新获取。';}}
  validate(){if(!this.loaded||this.button.disabled)throw Error('请先获取完整模型列表，再选择默认模型。');if(!this.models.includes(this.input.value.trim()))throw Error('默认模型必须从获取到的模型列表中选择。');}
  close(){this.menu.classList.add('hidden');this.input.setAttribute('aria-expanded','false');this.input.removeAttribute('aria-activedescendant');}
  open(filter=false){
    document.dispatchEvent(new CustomEvent('relay-dropdown-open',{detail:'modelName'}));
    const query=filter?this.input.value.trim().toLowerCase():'';
    const matches=this.models.filter(id=>id.toLowerCase().includes(query));
    this.options=matches.slice(0,200).map(id=>({id,label:id}));
    this.menu.replaceChildren();
    for(const [index,option] of this.options.entries()){
      const button=document.createElement('button');button.type='button';button.tabIndex=-1;button.role='option';button.id='model-option-'+index;button.dataset.modelIndex=String(index);
      const text=document.createElement('span');text.className='model-option-label';text.textContent=option.label;button.append(text);
      const check=document.createElement('span');check.className='dropdown-check';check.setAttribute('aria-hidden','true');check.innerHTML='<svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8"><path d="m3 8 3 3 7-7"/></svg>';button.append(check);
      button.setAttribute('aria-selected',String(option.id===this.input.value));this.menu.append(button);
    }
    if(!this.loaded||!matches.length||matches.length>200){const note=document.createElement('p');note.className='model-menu-note';note.textContent=!this.loaded?'点击「获取模型」加载中转提供的列表。':!matches.length?'没有匹配项，请选择列表中的完整模型 ID，或重新获取。':'仅展示前 200 项，输入名称可搜索更多模型；保存和同步包含全部模型。';this.menu.append(note);}
    this.active=this.options.findIndex(option=>option.id===this.input.value);if(this.active<0&&matches.length)this.active=0;this.menu.classList.remove('hidden');this.input.setAttribute('aria-expanded','true');this.highlight();this.input.focus({preventScroll:true});
  }
  highlight(){for(const [index,element] of [...this.menu.querySelectorAll('[role=option]')].entries())element.classList.toggle('focused',index===this.active);if(this.active>=0)this.input.setAttribute('aria-activedescendant','model-option-'+this.active);else this.input.removeAttribute('aria-activedescendant');this.menu.querySelector('#model-option-'+this.active)?.scrollIntoView({block:'nearest'});}
  choose(id){this.input.value=id;this.close();this.input.dispatchEvent(new Event('change',{bubbles:true}));this.input.focus({preventScroll:true});}
  async load(){
    if(!this.isOpen()||this.button.disabled)return;
    const input=this.getCredentials(),signature=JSON.stringify(input),epoch=++this.epoch;
    if(!input.protocol){this.status.textContent='请先选择模型调用协议，再获取模型。';this.status.classList.add('error');return;}
    if(!input.baseUrl||!input.key){this.status.textContent='请先填写 API 地址和 API 调用密钥。';this.status.classList.add('error');return;}
    this.close();this.models=[];this.loaded=false;this.discoveryToken='';this.button.disabled=true;this.button.textContent='获取中…';this.status.classList.remove('error');this.status.textContent='正在读取该 API 密钥可见的模型列表…';
    const current=()=>this.isOpen()&&epoch===this.epoch&&signature===JSON.stringify(this.getCredentials());
    try{
      const result=await this.fetch(input);if(!current())return;
      this.models=result.models||[];this.discoveryToken=result.discoveryToken||'';this.loaded=this.models.length>0;this.status.textContent=this.models.length?'已获取 '+this.models.length+' 个模型，请选择默认模型；全部模型均同步到 Codex。':'该密钥未返回可用模型，请检查中转权限。';
      if(this.models.length)this.open(false);
    }catch(error){if(current()){this.status.textContent=error.message;this.status.classList.add('error');}}
    finally{if(current()){this.button.disabled=false;this.button.textContent='↻ 重新获取';}}
  }
}
