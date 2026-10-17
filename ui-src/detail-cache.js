(function(root,factory){
  const DetailCache=factory();
  if(typeof module==='object'&&module.exports)module.exports=DetailCache;
  else root.RelayDetailCache=DetailCache;
})(typeof globalThis==='object'?globalThis:this,function(){
  'use strict';
  // Bound retained responses by count and serialized UTF-16 size, not measured heap size.
  return class DetailCache extends Map {
    static merge(previous,result){
      const sameProvider=previous&&previous.id===result.id&&previous.revision===result.revision;
      const sameQuery=sameProvider&&previous.period===result.period&&(previous.model||'all')===(result.model||'all')&&previous.range?.start===result.range?.start&&previous.range?.end===result.range?.end;
      const retainedUsage=!result.usage&&sameQuery&&previous.usage;
      const hasBalance=Number.isFinite(result.balance?.value),oldBalance=sameProvider&&Number.isFinite(previous.balance?.value)?previous:null;
      const summary=result.summary||result;
      const savedBalance=Number.isFinite(summary.balance?.value)&&(!oldBalance||Date.parse(summary.balanceAt||summary.at)>=Date.parse(oldBalance.balanceAt||oldBalance.at))?summary:oldBalance;
      return {...result,
        usage:result.usage||retainedUsage||null,
        usageAt:result.usage?(result.usageAt||result.at):retainedUsage?(previous.usageAt||previous.at):summary.usageAt,
        usageStale:Boolean(retainedUsage||result.usageStale),
        usageError:result.usageError||'',
        balance:hasBalance?result.balance:savedBalance?.balance||result.balance,
        balanceAt:hasBalance?(result.balanceAt||result.at):savedBalance?.balanceAt||savedBalance?.at,
        balanceError:result.balanceError||summary.balanceError||(!hasBalance?result.balance?.message||'接口未返回账户余额。':'')
      };
    }
    constructor({maxEntries=24,maxBytes=4*1024*1024,maxEntryBytes=1024*1024}={}){
      super();this.maxEntries=maxEntries;this.maxBytes=maxBytes;this.maxEntryBytes=maxEntryBytes;
      this.serializedBytes=0;this.sizes=new Map();
    }
    peek(key){return super.get(key);}
    byteSize(key){return this.sizes.get(key)||0;}
    get(key){
      if(!super.has(key))return undefined;
      const value=super.get(key);super.delete(key);super.set(key,value);return value;
    }
    set(key,value){
      this.delete(key);
      let bytes;
      try{bytes=JSON.stringify(value).length*2;}catch{return this;}
      if(bytes>this.maxEntryBytes||bytes>this.maxBytes||this.maxEntries<1)return this;
      while(this.size&&(this.size>=this.maxEntries||this.serializedBytes+bytes>this.maxBytes))this.delete(this.keys().next().value);
      super.set(key,value);this.sizes.set(key,bytes);this.serializedBytes+=bytes;return this;
    }
    delete(key){
      if(!super.delete(key))return false;
      this.serializedBytes-=this.sizes.get(key);this.sizes.delete(key);return true;
    }
    clear(){super.clear();this.sizes.clear();this.serializedBytes=0;}
  };
});
