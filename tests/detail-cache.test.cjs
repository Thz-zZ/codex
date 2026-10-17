'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const DetailCache=require('../ui-src/detail-cache.js');

const successful=()=>({id:'relay',revision:'r1',period:'today',model:'all',range:{start:'2026-10-17',end:'2026-10-17'},at:'2026-10-17T01:00:00Z',usage:{totals:{cost:1},daily:[{name:'2026-10-17',input:10}]},usageError:'',balance:{value:0}});

test('failed refresh retains complete usage by reference, while keeping attempt and success times separate',()=>{
  const original=successful(),failure={...original,at:'2026-10-17T01:02:00Z',usage:null,usageError:'连接超时',balance:{value:null,message:'offline'}};
  const merged=DetailCache.merge(original,failure);
  assert.equal(merged.usage,original.usage);
  assert.equal(merged.usageAt,original.at);assert.equal(merged.at,failure.at);
  assert.equal(merged.usageStale,true);assert.equal(merged.usageError,'连接超时');
  assert.equal(merged.balance.value,0);assert.equal(merged.balanceAt,original.at);assert.equal(merged.balanceError,'offline');
  const again=DetailCache.merge(merged,{...failure,at:'2026-10-17T01:03:00Z'});
  assert.equal(again.usageAt,original.at);assert.equal(again.balanceAt,original.at);
  assert.equal(original.usageAt,undefined,'merging does not mutate an existing response');
});

test('a successful retry replaces retained usage and clears failure state',()=>{
  const original=successful(),failed=DetailCache.merge(original,{...original,usage:null,usageError:'offline',balance:{value:null,message:'offline'}});
  const fresh={...successful(),at:'2026-10-17T01:04:00Z',usage:{totals:{cost:2}}};
  const merged=DetailCache.merge(failed,fresh);
  assert.equal(merged.usage,fresh.usage);assert.equal(merged.usageAt,fresh.at);
  assert.equal(merged.usageStale,false);assert.equal(merged.usageError,'');assert.equal(merged.balanceError,'');
});

test('a recent known balance from the backend summary takes precedence over an older chart balance',()=>{
  const original=successful(),balance={value:2},balanceAt='2026-10-17T01:02:00Z';
  const merged=DetailCache.merge(original,{...original,at:'2026-10-17T01:03:00Z',usage:null,usageError:'offline',balance:{value:null,message:'offline'},summary:{balance,balanceAt}});
  assert.equal(merged.usage,original.usage);assert.equal(merged.balance,balance);assert.equal(merged.balanceAt,balanceAt);
  assert.equal(merged.balanceError,'offline');
});

test('retained charts cannot cross provider, revision, date, period or model boundaries',()=>{
  const original=successful(),failure={...original,usage:null,usageError:'offline',balance:{value:null}};
  for(const changed of [{id:'other'},{revision:'r2'},{period:'7'},{model:'one-model'},{range:{start:'2026-10-16',end:'2026-10-17'}},{range:{start:'2026-10-18',end:'2026-10-18'}}]){
    const result=DetailCache.merge(original,{...failure,...changed});
    assert.equal(result.usage,null,JSON.stringify(changed));assert.equal(result.usageStale,false);
    if(changed.id||changed.revision)assert.equal(result.balance.value,null);
  }
  assert.equal(DetailCache.merge(null,failure).usage,null,'a first failure cannot invent successful usage');
});

test('newer persisted summaries retain an older complete chart and preserve successful timestamps',()=>{
  const original=successful(),summary={id:original.id,revision:original.revision,period:original.period,model:'all',range:original.range,at:'2026-10-17T01:03:00Z',checkedAt:'2026-10-17T01:03:00Z',usageAt:original.at,totals:original.usage.totals,usageError:'offline',balance:original.balance,balanceAt:original.at,balanceError:'offline'};
  const merged=DetailCache.merge(original,{...summary,summary});
  assert.equal(merged.usage,original.usage);assert.equal(merged.usageAt,original.at);assert.equal(merged.balanceAt,original.at);
  assert.equal(merged.usageError,'offline');assert.equal(merged.usageStale,true);
  const rehydrated=DetailCache.merge(original,DetailCache.merge(null,{...summary,summary}));
  assert.equal(rehydrated.usage,original.usage);assert.equal(rehydrated.usageAt,original.at);
  const background=DetailCache.merge(original,{...summary,usageError:'',balanceError:'',usageAt:summary.at});
  assert.equal(background.usageError,'','a successful summary update is not reported as a failed query');
  assert.equal(background.usageAt,original.at,'summary success cannot make an older chart appear freshly loaded');
});

test('selection retains identity and evicts the least recently used response',()=>{
  const cache=new DetailCache({maxEntries:2}),first={id:1};
  cache.set('a',first).set('b',{id:2});
  assert.equal(cache.get('a'),first);
  cache.set('c',{id:3});
  assert.deepEqual([...cache.keys()],['a','c']);
  cache.peek('a');cache.set('d',{id:4});
  assert.deepEqual([...cache.keys()],['c','d'],'sidebar peeks do not promote old entries');
});

test('byte budget, replacement, deletion and clearing stay consistent',()=>{
  const bytes=value=>JSON.stringify(value).length*2;
  const cache=new DetailCache({maxBytes:100,maxEntryBytes:100});
  cache.set('a','x'.repeat(20)).set('b','y'.repeat(20));
  assert.equal(cache.serializedBytes,bytes('x'.repeat(20))+bytes('y'.repeat(20)));
  assert.equal(cache.byteSize('a'),bytes('x'.repeat(20)));
  cache.set('a','z'.repeat(30));
  assert.deepEqual([...cache.keys()],['a']);
  assert.equal(cache.serializedBytes,bytes('z'.repeat(30)));
  assert.equal(cache.delete('missing'),false);
  cache.delete('a');assert.equal(cache.serializedBytes,0);
  assert.equal(cache.byteSize('a'),0);
  cache.set('b',{id:2});cache.clear();
  assert.equal(cache.size,0);assert.equal(cache.serializedBytes,0);
});

test('oversize and unserializable responses cannot leave stale replacements',()=>{
  const cache=new DetailCache({maxEntryBytes:40});
  cache.set('a',{id:1});cache.set('a','x'.repeat(100));
  assert.equal(cache.has('a'),false);assert.equal(cache.serializedBytes,0);
  const cyclic={};cyclic.self=cyclic;
  cache.set('a',cyclic);cache.set('b',undefined);
  assert.equal(cache.size,0);
});

test('thousands of provider, date and model responses stay within both budgets',()=>{
  const cache=new DetailCache();let submitted=0;
  for(let i=0;i<1500;i++){
    const record={id:'provider-'+i%37,period:'range:'+i,model:'model-'+i%11,usage:{logs:[{text:(i+':').repeat(24000)}]}};
    submitted+=JSON.stringify(record).length*2;
    cache.set(i,record);
    assert.ok(cache.size<=24);assert.ok(cache.serializedBytes<=4*1024*1024);
  }
  assert.equal(cache.get(1499).period,'range:1499');
  assert.equal(cache.has(0),false);
  console.log(JSON.stringify({submittedSerializedMiB:+(submitted/1048576).toFixed(1),retainedSerializedMiB:+(cache.serializedBytes/1048576).toFixed(2),retainedResponses:cache.size}));
});
