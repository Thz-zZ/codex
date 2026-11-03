(function(root,factory){
  const api=factory();
  if(typeof module==='object'&&module.exports)module.exports=api;
  else root.RelaySyncStatus=api;
})(globalThis,()=>{
  'use strict';
  function describe(result,mode='all',balanceEnabled=true){
    const local=mode!=='balance'&&result.localUsageRequested!==false;
    const balance=mode!=='local'&&result.balanceRequested!==false&&balanceEnabled;
    const localError=local?result.localUsageError||'':'';
    const balanceError=balance?(result.balanceError||(!Number.isFinite(result.balance?.value)?result.balance?.message||'接口未返回账户余额。':'')):'';
    const scan=local&&!localError?result.usage?.sync:null;
    const errors=(scan?.errors||0)+Number(Boolean(localError))+Number(Boolean(balanceError));
    let message;
    if(scan)message=`同步完成${errors?'（有错误）':''}：${scan.initial?'载入':'新增'} ${scan.added} 条，扫描 ${scan.scannedFiles} 个文件，错误 ${errors}`;
    else if(localError)message='本地用量同步失败';
    else if(balanceError)message='余额同步失败';
    else message=mode==='balance'&&balance?'余额同步完成':local?'同步完成':'未启用余额同步';
    const details=[localError?'本地用量同步失败：'+localError:'',balanceError?'余额同步失败：'+balanceError:'',...(scan?.errors?result.usage?.warnings||[]:[])].filter(Boolean).join('；');
    return {message,details,error:errors>0};
  }
  return {describe};
});
