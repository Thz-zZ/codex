'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs');
const http=require('node:http');
const path=require('node:path');
const {spawn}=require('node:child_process');
const {once}=require('node:events');

(async()=>{
  const hostPath=path.resolve(process.argv[2]||'build/model-bridge/ModelBridgeTestHost.exe');
  const codexPath=process.argv[3]||'codex';
  const model=process.argv[4]||'deepseek-fixture';
  const protocol=process.argv[5]||'chat-completions',claude=protocol==='anthropic';
  const clientModel=process.argv[6]||model;
  const imageTool=process.argv[7]==='image';
  const catalog=process.argv[7]==='catalog';
  const configuredEffort=process.argv[8]||'high',clientEffort=process.argv[9];
  const expectedEffort=clientEffort||(configuredEffort==='default'?'medium':configuredEffort);
  const directory=path.resolve('build/model-bridge/cli-fixture-'+Date.now());
  const workspace=path.join(directory,'workspace');fs.mkdirSync(workspace,{recursive:true});
  const imagePath=path.join(workspace,'fixture.png');
  if(imageTool)fs.copyFileSync(path.resolve('ui-src/icon.png'),imagePath);
  const requests=[];let host,cli,stdout='',stderr='',hostError='',fixtureError,customExercised=false;
  const server=http.createServer(async(request,response)=>{
    try{
      assert.equal(request.url,claude?'/v1/messages':'/v1/chat/completions');
      if(claude){assert.equal(request.headers['x-api-key'],'fake-bridge-cli-secret');assert.equal(request.headers['anthropic-version'],'2023-06-01');assert.equal(request.headers.authorization,undefined);}
      else assert.equal(request.headers.authorization,'Bearer fake-bridge-cli-secret');
      let body='';for await(const chunk of request)body+=chunk;
      const payload=JSON.parse(body);requests.push(payload);assert.equal(payload.model,catalog?clientModel:model,catalog?'Codex can switch to a non-default catalog model':'the legacy applied relay model wins over the Codex session model');
      assert.equal(payload.cache_control,undefined,'all bridged protocols rely on upstream caching without injected vendor flags');
      if(claude){assert.equal(payload.output_config?.effort,expectedEffort,'native Claude receives the Codex selection or the configured default');assert.equal(payload.reasoning_effort,undefined);}
      else assert.equal(payload.reasoning_effort,expectedEffort,'compatible model receives the Codex selection or the configured default');
      if(claude)for(const message of payload.messages.filter(message=>message.role==='assistant')){
        const calls=message.content.filter(block=>block.type==='tool_use');
        if(calls.length){
          const turn=calls.some(block=>block.id==='call_cli_fixture')?1:2;
          assert.deepEqual(message.content.slice(0,2),[{type:'thinking',thinking:'private native fixture '+turn,signature:'native-signature-'+turn},{type:'redacted_thinking',data:'native-redacted-'+turn}],'native thinking and signatures survive each Codex tool continuation');
        }
      }
      let delta,reason;
      const tools=(payload.tools||[]).map(tool=>claude?tool:tool.function);
      const results=claude?payload.messages.flatMap(message=>Array.isArray(message.content)?message.content:[]).filter(block=>block.type==='tool_result').map(block=>({role:'tool',tool_call_id:block.tool_use_id,content:block.content})):payload.messages;
      const firstResult=results.find(message=>message.role==='tool'&&message.tool_call_id==='call_cli_fixture');
      const customResult=results.find(message=>message.role==='tool'&&message.tool_call_id==='custom_cli_fixture');
      if(!firstResult){
        const safe=imageTool?tools.find(tool=>/(^|__)view_image$/.test(tool.name)):tools.find(tool=>tool.name==='get_goal')||tools.find(tool=>tool.name==='clock__sleep');
        assert.ok(safe,'Codex exposes a harmless test function: '+tools.map(tool=>tool.name).join(', '));
        const parameters=imageTool?{path:imagePath}:safe.name==='clock__sleep'?{duration_ms:1}:{};
        delta={reasoning_content:'fixture reasoning',tool_calls:[{index:0,id:'call_cli_fixture',type:'function',function:{name:safe.name,arguments:JSON.stringify(parameters)}}]};reason='tool_calls';
      }else{
        if(imageTool){
          if(claude)assert.ok(Array.isArray(firstResult.content)&&firstResult.content.some(block=>block.type==='image'&&block.source?.type==='base64'),'Claude retains view_image output inside its tool result');
          else{assert.equal(typeof firstResult.content,'string','Chat tool messages remain text-only');assert.ok(payload.messages.some(message=>message.role==='user'&&Array.isArray(message.content)&&message.content.some(block=>block.type==='image_url'&&block.image_url?.url.startsWith('data:image/'))),'Chat receives view_image output as an attached image message');}
        }
        if(!claude)assert.ok(payload.messages.some(message=>message.role==='assistant'&&message.reasoning_content==='fixture reasoning'),'reasoning survives the Codex continuation');
        const custom=imageTool?null:tools.find(tool=>tool.name==='functions__exec');
        if(custom&&!customResult){customExercised=true;delta={tool_calls:[{index:0,id:'custom_cli_fixture',type:'function',function:{name:custom.name,arguments:JSON.stringify({input:'text("bridge-cli-fixture");'})}}]};reason='tool_calls';}
        else{if(customExercised)assert.ok(customResult&&customResult.content.includes('bridge-cli-fixture'),'custom tool result returns through the bridge');delta={content:'bridge-cli-fixture complete'};reason='stop';}
      }
      response.writeHead(200,{'Content-Type':'text/event-stream'});
      if(claude){
        const emit=value=>response.write('event: '+value.type+'\ndata: '+JSON.stringify(value)+'\n\n');
        emit({type:'message_start',message:{type:'message',role:'assistant',model,content:[],usage:{input_tokens:50,output_tokens:0,cache_creation_input_tokens:requests.length===1?80:0,cache_read_input_tokens:requests.length===1?0:80}}});
        emit({type:'content_block_start',index:0,content_block:{type:'thinking',thinking:'',signature:''}});
        emit({type:'content_block_delta',index:0,delta:{type:'thinking_delta',thinking:'private native fixture '+requests.length}});
        for(const signature of ['native-signature-',String(requests.length)])emit({type:'content_block_delta',index:0,delta:{type:'signature_delta',signature}});
        emit({type:'content_block_stop',index:0});
        emit({type:'content_block_start',index:1,content_block:{type:'redacted_thinking',data:'native-redacted-'+requests.length}});emit({type:'content_block_stop',index:1});
        if(delta.content){emit({type:'content_block_start',index:2,content_block:{type:'text',text:''}});emit({type:'content_block_delta',index:2,delta:{type:'text_delta',text:delta.content}});emit({type:'content_block_stop',index:2});}
        for(const [toolIndex,tool] of (delta.tool_calls||[]).entries()){
          const index=toolIndex+2;
          emit({type:'content_block_start',index,content_block:{type:'tool_use',id:tool.id,name:tool.function.name,input:{}}});
          const args=tool.function.arguments,split=Math.floor(args.length/2);
          for(const fragment of [args.slice(0,split),args.slice(split)])emit({type:'content_block_delta',index,delta:{type:'input_json_delta',partial_json:fragment}});
          emit({type:'content_block_stop',index});
        }
        emit({type:'message_delta',delta:{stop_reason:reason==='tool_calls'?'tool_use':'end_turn'},usage:{output_tokens:10}});
        emit({type:'message_stop'});response.end();return;
      }
      response.write('data: '+JSON.stringify({choices:[{index:0,delta,finish_reason:null}]})+'\n\n');
      response.write('data: '+JSON.stringify({choices:[{index:0,delta:{},finish_reason:reason}],usage:{prompt_tokens:50,completion_tokens:10,total_tokens:60,prompt_tokens_details:{cached_tokens:requests.length===1?0:30}}})+'\n\n');
      response.end('data: [DONE]\n\n');
    }catch(error){fixtureError??=error;response.writeHead(500,{'Content-Type':'application/json'});response.end(JSON.stringify({error:{message:error.message}}));}
  });
  try{
    server.listen(0,'127.0.0.1');await once(server,'listening');
    host=spawn(hostPath,[directory,'http://127.0.0.1:'+server.address().port+'/v1',model,protocol,configuredEffort,...(catalog?[JSON.stringify([...new Set([model,clientModel])])]:[])],{windowsHide:true,stdio:['pipe','pipe','pipe']});
    host.stderr.on('data',data=>hostError+=data);
    await new Promise((resolve,reject)=>{host.once('error',reject);host.once('exit',code=>reject(Error('Bridge fixture exited '+code+': '+hostError)));host.stdout.once('data',()=>resolve());});
    cli=spawn(codexPath,['exec','--strict-config','--ephemeral','--skip-git-repo-check','--sandbox','read-only','--json','-C',workspace,'-m',clientModel,'-c','approval_policy="never"',...(clientEffort?['-c','model_reasoning_effort='+JSON.stringify(clientEffort)]:[]),'Read the current goal with get_goal, then report completion.'],{windowsHide:true,env:{...process.env,CODEX_HOME:path.join(directory,'config')},stdio:['ignore','pipe','pipe']});
    cli.stdout.on('data',data=>stdout+=data);cli.stderr.on('data',data=>stderr+=data);
    const timeout=setTimeout(()=>{cli.kill();},40000);
    const [code]=await once(cli,'exit');clearTimeout(timeout);
    fs.writeFileSync(path.join(directory,'result.json'),JSON.stringify({code,requests,stdout,stderr,fixtureError:fixtureError?.message},null,2));
    if(fixtureError)throw fixtureError;
    assert.equal(code,0,stderr+'\n'+stdout);
    assert.equal(requests.length,customExercised?3:2,'Codex completes all advertised tool round trips');
    assert.ok(stdout.includes('bridge-cli-fixture complete'),'Codex displays the bridged assistant response');
    if(claude)assert.ok(!stdout.includes('private native fixture')&&!stdout.includes('native-signature-')&&!stdout.includes('native-redacted-'),'native thinking is not exposed as assistant text');
    console.log(JSON.stringify({ok:true,model,clientModel,protocol,configuredEffort,clientEffort,expectedEffort,imageTool,catalog,customExercised,requests:requests.length,fixture:directory}));
  }finally{
    if(cli&&cli.exitCode===null)cli.kill();
    if(host&&host.exitCode===null){host.stdin.end('\n');await once(host,'exit').catch(()=>{});}
    server.close();server.closeAllConnections();
  }
})().catch(error=>{console.error(error);process.exitCode=1;});
