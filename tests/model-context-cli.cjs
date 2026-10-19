'use strict';
// Exercise the native app-server notification consumed by the context indicator.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const http=require('node:http');
const {spawn}=require('node:child_process');
const {once}=require('node:events');
const hostPath=path.resolve(process.argv[2]||'build/model-bridge-tests/ModelBridgeTestHost.exe');
const codexPath=process.argv[3]||'codex';

async function check(protocol,effort){
  const directory=path.resolve('build/model-context/'+protocol+'-'+effort+'-'+Date.now());
  const workspace=path.join(directory,'workspace');fs.mkdirSync(workspace,{recursive:true});
  let host,app,hostExit,fixtureError,requests=0;
  const upstream=http.createServer(async(request,response)=>{
    try{
      let body='';for await(const chunk of request)body+=chunk;
      const input=JSON.parse(body),expectedEffort=effort==='default'?undefined:effort;requests++;fs.writeFileSync(path.join(directory,'upstream-request.json'),JSON.stringify(input,null,2));
      response.writeHead(200,{'Content-Type':'text/event-stream'});
      if(protocol==='chat-completions'){
        assert.equal(input.reasoning_effort,expectedEffort);
        response.end('data: '+JSON.stringify({choices:[{index:0,delta:{content:'context fixture complete'},finish_reason:'stop'}],usage:{prompt_tokens:50,completion_tokens:10,total_tokens:60}})+'\n\ndata: [DONE]\n\n');
      }else if(protocol==='anthropic'){
        assert.equal(input.output_config?.effort,expectedEffort);
        const events=[{type:'message_start',message:{type:'message',role:'assistant',content:[],usage:{input_tokens:50,output_tokens:0}}},{type:'content_block_start',index:0,content_block:{type:'text',text:''}},{type:'content_block_delta',index:0,delta:{type:'text_delta',text:'context fixture complete'}},{type:'content_block_stop',index:0},{type:'message_delta',delta:{stop_reason:'end_turn'},usage:{output_tokens:10}},{type:'message_stop'}];
        response.end(events.map(value=>'event: '+value.type+'\ndata: '+JSON.stringify(value)+'\n\n').join(''));
      }else{
        assert.equal(input.reasoning?.effort,expectedEffort);
        const part={type:'output_text',text:'context fixture complete',annotations:[]};
        const item={id:'msg_context_fixture',type:'message',role:'assistant',status:'completed',content:[part]};
        const result={id:'resp_context_fixture',object:'response',created_at:Math.floor(Date.now()/1000),model:input.model,status:'completed',output:[item],usage:{input_tokens:50,output_tokens:10,total_tokens:60,input_tokens_details:{cached_tokens:0},output_tokens_details:{reasoning_tokens:0}}};
        const events=[{type:'response.created',response:{...result,status:'in_progress',output:[]}},{type:'response.output_item.added',output_index:0,item:{...item,status:'in_progress',content:[]}},{type:'response.content_part.added',output_index:0,content_index:0,item_id:item.id,part:{...part,text:''}},{type:'response.output_text.delta',output_index:0,content_index:0,item_id:item.id,delta:part.text},{type:'response.output_text.done',output_index:0,content_index:0,item_id:item.id,text:part.text},{type:'response.content_part.done',output_index:0,content_index:0,item_id:item.id,part},{type:'response.output_item.done',output_index:0,item},{type:'response.completed',response:result}];
        response.end(events.map((value,index)=>'event: '+value.type+'\ndata: '+JSON.stringify({...value,sequence_number:index})+'\n\n').join(''));
      }
    }catch(error){fixtureError=error;response.end();}
  });
  try{
    upstream.listen(0,'127.0.0.1');await once(upstream,'listening');
    host=spawn(hostPath,[directory,'http://127.0.0.1:'+upstream.address().port+'/v1','relay-context-fixture',protocol,effort],{windowsHide:true,stdio:['pipe','pipe','pipe']});hostExit=once(host,'exit');
    let hostError='';host.stderr.on('data',chunk=>hostError+=chunk);
    await Promise.race([once(host.stdout,'data'),hostExit.then(([code])=>{throw Error('Fixture failed '+code+': '+hostError);})]);
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
            if(message.id===1){send({method:'initialized'});send({id:2,method:'thread/start',params:{cwd:workspace,model:'relay-context-fixture',approvalPolicy:'never',sandbox:'read-only',ephemeral:true}});}
            if(message.id===2)send({id:3,method:'turn/start',params:{threadId:message.result.thread.id,input:[{type:'text',text:'Return a short fixture response.'}]}});
            if(message.method){notifications.push(message);if(message.method==='thread/tokenUsage/updated')usage=message.params.tokenUsage;}
            if(message.method==='turn/completed'){
              clearTimeout(timer);fs.writeFileSync(path.join(directory,'result.json'),JSON.stringify({protocol,effort,usage,notifications,stderr},null,2));
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
    assert.equal(usage?.modelContextWindow,258400,'context notification includes the 272k client window at 95% effective budget');
    assert.equal(usage.last.totalTokens,60,'context indicator uses real upstream usage, not a fabricated counter');
    console.log(JSON.stringify({ok:true,protocol,effort,contextWindow:usage.modelContextWindow,usedTokens:usage.last.totalTokens,fixture:directory}));
  }finally{
    if(app&&app.exitCode===null)app.kill();
    if(host&&host.exitCode===null){host.stdin.end('\n');await hostExit;}
    upstream.close();upstream.closeAllConnections();
  }
}
(async()=>{for(const protocol of ['responses','chat-completions','anthropic'])for(const effort of ['default','high'])await check(protocol,effort);})().catch(error=>{console.error(error);process.exitCode=1;});
