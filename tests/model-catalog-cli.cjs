'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {spawn}=require('node:child_process');
const {once}=require('node:events');

const hostPath=path.resolve(process.argv[2]||'build/codex-provider-tests/CodexProviderTestHost.exe');
const codexPath=process.argv[3]||'codex';

async function checkModel(selection){
  const directory=path.resolve('build/model-catalog/fixture-'+Date.now()+'-'+selection.effort);
  fs.mkdirSync(directory,{recursive:true});
  const host=spawn(hostPath,[directory,'http://127.0.0.1:1/v1',selection.model,selection.protocol,selection.effort,...(selection.models||selection.contextPreset?[JSON.stringify(selection.models||[selection.model]),selection.contextPreset||'default']:[])],{windowsHide:true,stdio:['pipe','pipe','pipe']});
  const hostExit=once(host,'exit');let hostError='',server;
  host.stderr.on('data',chunk=>hostError+=chunk);
  try{
    await Promise.race([once(host.stdout,'data'),hostExit.then(([code])=>{throw Error('Catalog fixture exited '+code+': '+hostError);})]);
    const models=await new Promise((resolve,reject)=>{
      server=spawn(codexPath,['app-server','--strict-config','--stdio'],{windowsHide:true,env:{...process.env,CODEX_HOME:path.join(directory,'config')},stdio:['pipe','pipe','pipe']});
      let buffer='',stderr='',result;
      const timer=setTimeout(()=>{server.kill();reject(Error('Model discovery timed out'));},15000);
      server.once('error',error=>{clearTimeout(timer);reject(error);});
      server.stdin.on('error',()=>{});
      server.stderr.on('data',chunk=>stderr+=chunk);
      server.stdout.on('data',chunk=>{
        buffer+=chunk;let newline;
        while((newline=buffer.indexOf('\n'))>=0){
          const line=buffer.slice(0,newline);buffer=buffer.slice(newline+1);
          try{
            const message=JSON.parse(line);
            if(message.error)throw Error(JSON.stringify(message.error));
            if(message.id===1){
              server.stdin.write(JSON.stringify({method:'initialized'})+'\n');
              server.stdin.write(JSON.stringify({id:2,method:'model/list',params:{includeHidden:false}})+'\n');
            }
            if(message.id===2){result=message.result;server.stdin.end();}
          }catch(error){clearTimeout(timer);server.kill();reject(error);}
        }
      });
      server.once('exit',code=>{clearTimeout(timer);if(code!==0||!result)reject(Error('Model discovery failed: '+stderr));else resolve(result.data);});
      server.stdin.write(JSON.stringify({id:1,method:'initialize',params:{clientInfo:{name:'relay-catalog-check',version:'1.0.0'},capabilities:{experimentalApi:true}}})+'\n');
    });
    assert.equal(models.length,selection.models?.length||1,'Codex lists the complete applied catalog');
    assert.deepEqual(models.map(value=>value.model).sort(),[...(selection.models||[selection.model])].sort());
    const model=models.find(value=>value.model===selection.model);
    assert.equal(model.id,selection.model);assert.equal(model.model,selection.model);assert.equal(model.displayName,selection.contextPreset==='sol-500k'?'6.1 Sol · 500k':selection.model);
    assert.equal(model.isDefault,true);assert.equal(model.hidden,false);
    assert.equal(models.filter(value=>value.isDefault).length,1,'only the configured model is the default');
    for(const entry of models)assert.deepEqual(entry.supportedReasoningEfforts.map(value=>value.reasoningEffort),['low','medium','high','xhigh','max','ultra'],'a configured default leaves all Responses effort choices available');
    if(selection.effort!=='default')assert.equal(model.defaultReasoningEffort,selection.effort);
    fs.writeFileSync(path.join(directory,'result.json'),JSON.stringify({ok:true,...selection,models},null,2));
    console.log(JSON.stringify({ok:true,...selection,displayName:model.displayName,visibleModels:models.length,fixture:directory}));
  }finally{
    if(server&&server.exitCode===null)server.kill();
    if(host.exitCode===null){host.stdin.end('\n');await hostExit;}
  }
}

(async()=>{
  for(const selection of [
    {model:'gpt-codex-fixture',protocol:'responses',effort:'default'},
    {model:'gpt-codex-fixture',protocol:'responses',effort:'high'},
    {model:'gpt-codex-mini-fixture',protocol:'responses',effort:'medium'},
    {model:'gpt-6.1-sol',protocol:'responses',effort:'xhigh'},
    {model:'gpt-6.1-sol',protocol:'responses',effort:'high',contextPreset:'sol-500k',models:['gpt-6.1-sol','gpt-5.6-sol']},
    {model:'relay-default',protocol:'responses',effort:'default',models:['gpt-codex-fixture','gpt-codex-mini-fixture','relay-default']}
  ])await checkModel(selection);
})().catch(error=>{console.error(error);process.exitCode=1;});
