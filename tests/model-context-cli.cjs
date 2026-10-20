'use strict';
// Exercise the native app-server notification consumed by the context indicator.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const http=require('node:http');
const {spawn}=require('node:child_process');
const {once}=require('node:events');
const hostPath=path.resolve(process.argv[2]||'build/codex-provider-tests/CodexProviderTestHost.exe');
const codexPath=process.argv[3]||'codex';

async function check(effort,requestEffort,withUsage=true,contextOverride,contextPreset='default',model='relay-context-fixture'){
  const protocol='responses';
  const directory=path.resolve('build/model-context/'+protocol+'-'+effort+'-'+Date.now());
  const workspace=path.join(directory,'workspace');fs.mkdirSync(workspace,{recursive:true});
  let host,app,hostExit,fixtureError,requests=0;
  const upstream=http.createServer(async(request,response)=>{
    try{
      let body='';for await(const chunk of request)body+=chunk;
      const input=JSON.parse(body),expectedEffort=requestEffort||(effort==='default'?undefined:effort);requests++;fs.writeFileSync(path.join(directory,'upstream-request.json'),JSON.stringify(input,null,2));
      assert.equal(input.model,model,'a display preset never changes the upstream model ID');
      response.writeHead(200,{'Content-Type':'text/event-stream'});
      {
        assert.equal(input.reasoning?.effort,expectedEffort);
        const part={type:'output_text',text:'context fixture complete',annotations:[]};
        const item={id:'msg_context_fixture',type:'message',role:'assistant',status:'completed',content:[part]};
        const result={id:'resp_context_fixture',object:'response',created_at:Math.floor(Date.now()/1000),model:input.model,status:'completed',output:[item],usage:{input_tokens:50,output_tokens:10,total_tokens:60,input_tokens_details:{cached_tokens:0},output_tokens_details:{reasoning_tokens:0}}};
        if(!withUsage)delete result.usage;
        const events=[{type:'response.created',response:{...result,status:'in_progress',output:[]}},{type:'response.output_item.added',output_index:0,item:{...item,status:'in_progress',content:[]}},{type:'response.content_part.added',output_index:0,content_index:0,item_id:item.id,part:{...part,text:''}},{type:'response.output_text.delta',output_index:0,content_index:0,item_id:item.id,delta:part.text},{type:'response.output_text.done',output_index:0,content_index:0,item_id:item.id,text:part.text},{type:'response.content_part.done',output_index:0,content_index:0,item_id:item.id,part},{type:'response.output_item.done',output_index:0,item},{type:'response.completed',response:result}];
        response.end(events.map((value,index)=>'event: '+value.type+'\ndata: '+JSON.stringify({...value,sequence_number:index})+'\n\n').join(''));
      }
    }catch(error){fixtureError=error;response.end();}
  });
  try{
    upstream.listen(0,'127.0.0.1');await once(upstream,'listening');
    host=spawn(hostPath,[directory,'http://127.0.0.1:'+upstream.address().port+'/v1',model,protocol,effort,JSON.stringify([model,'other-model']),contextPreset],{windowsHide:true,stdio:['pipe','pipe','pipe']});hostExit=once(host,'exit');
    let hostError='';host.stderr.on('data',chunk=>hostError+=chunk);
    await Promise.race([once(host.stdout,'data'),hostExit.then(([code])=>{throw Error('Fixture failed '+code+': '+hostError);})]);
    if(contextOverride){const file=path.join(directory,'config/config.toml');fs.writeFileSync(file,'model_context_window = '+contextOverride+'\n'+fs.readFileSync(file,'utf8'));}
    const usage=await new Promise((resolve,reject)=>{
      app=spawn(codexPath,['app-server','--strict-config','--stdio'],{windowsHide:true,env:{...process.env,CODEX_HOME:path.join(directory,'config')},stdio:['pipe','pipe','pipe']});
      let buffer='',stderr='',usage=null;const notifications=[];
      const timer=setTimeout(()=>{app.kill();reject(Error('Context notification timeout: '+stderr));},30000);
      const send=value=>app.stdin.write(JSON.stringify(value)+'\n');
      app.on('error',error=>{clearTimeout(timer);reject(error);});app.stdin.on('error',()=>{});app.stderr.on('data',chunk=>stderr+=chunk);
      app.stdout.on('data',chunk=>{
        buffer+=chunk;let newline;
        while((newline=buffer.indexOf('\n'))>=0){const line=buffer.slice(0,newline);buffer=buffer.slice(newline+1);
          try{
            const message=JSON.parse(line);if(message.error)throw Error(JSON.stringify(message.error));
            if(message.id===1){send({method:'initialized'});send({id:2,method:'thread/start',params:{cwd:workspace,model,approvalPolicy:'never',sandbox:'read-only',ephemeral:true}});}
            if(message.id===2){assert.equal(requests,0,'an empty thread makes no generation request');assert.ok(!usage||usage.last.totalTokens===0,'empty threads have no invented positive usage');send({id:3,method:'turn/start',params:{threadId:message.result.thread.id,input:[{type:'text',text:'Return a short fixture response.'}],...(requestEffort?{effort:requestEffort}:{})}});}
            if(message.method){notifications.push(message);if(message.method==='thread/tokenUsage/updated')usage=message.params.tokenUsage;}
            if(message.method==='turn/completed'){
              clearTimeout(timer);fs.writeFileSync(path.join(directory,'result.json'),JSON.stringify({protocol,effort,requestEffort,withUsage,contextOverride,usage,notifications,stderr},null,2));
              if(message.params.turn.status!=='completed')throw fixtureError||Error(JSON.stringify(message.params));
              resolve(usage);
            }
          }catch(error){clearTimeout(timer);app.kill();reject(error);}
        }
      });
      app.on('exit',code=>{clearTimeout(timer);reject(Error('App-server exited early '+code+': '+stderr));});
      send({id:1,method:'initialize',params:{clientInfo:{name:'relay-context-check',version:'1.0.0'},capabilities:{experimentalApi:true}}});
    });
    if(fixtureError)throw fixtureError;
    assert.ok(requests>0,'native Codex completed a real fixture request');
    if(withUsage){
      assert.equal(usage?.modelContextWindow,(contextOverride||(contextPreset==='sol-500k'?500000:272000))*(contextPreset==='sol-500k'?1:0.95),'context notification respects the selected preset and explicit overrides');
      assert.equal(usage.last.totalTokens,60,'context indicator uses real upstream usage, not a fabricated counter');
    }else assert.ok(!usage||usage.last.totalTokens===0,'missing upstream usage is not replaced with an invented positive count');
    console.log(JSON.stringify({ok:true,protocol,effort,requestEffort,withUsage,contextOverride,contextPreset,model,contextWindow:usage?.modelContextWindow,usedTokens:usage?.last.totalTokens,fixture:directory}));
  }finally{
    if(app&&app.exitCode===null)app.kill();
    if(host&&host.exitCode===null){host.stdin.end('\n');await hostExit;}
    upstream.close();upstream.closeAllConnections();
  }
}
(async()=>{
  await check('default');await check('high');await check('high','low');await check('default',undefined,true,200000);await check('default',undefined,false);
  await check('high',undefined,true,undefined,'sol-500k','gpt-6.1-sol');
  await check('default',undefined,true,undefined,'default','gpt-6.1-sol');
})().catch(error=>{console.error(error);process.exitCode=1;});
