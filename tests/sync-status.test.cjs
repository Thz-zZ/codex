'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {describe}=require('../ui-src/sync-status.js');
const result=()=>({balanceRequested:true,localUsageRequested:true,balance:{value:2},usage:{sync:{added:0,scannedFiles:8,errors:0,initial:false},warnings:[]}});

test('completion shows actual scan counts and distinguishes initial loading',()=>{
  const value=result();
  assert.deepEqual(describe(value),{message:'同步完成：新增 0 条，扫描 8 个文件，错误 0',details:'',error:false});
  value.usage.sync={...value.usage.sync,initial:true,added:350};
  assert.match(describe(value).message,/载入 350 条/);
});

test('a balance failure is counted alongside a successful local scan',()=>{
  const value=result();value.balance={value:null};value.balanceError='HTTP 403';
  const status=describe(value);
  assert.equal(status.error,true);assert.match(status.message,/扫描 8 个文件，错误 1/);
  assert.match(status.details,/余额同步失败：HTTP 403/);
});

test('scan errors stay visible and a total local failure cannot reuse old scan results',()=>{
  const value=result();value.usage.sync.errors=2;value.usage.warnings=['文件无法读取'];
  assert.match(describe(value).message,/错误 2/);assert.equal(describe(value).error,true);
  value.localUsageError='无法打开目录';
  assert.equal(describe(value).message,'本地用量同步失败');
  assert.doesNotMatch(describe(value).message,/扫描/);
});

test('local filters and balance-only sync do not report stale failures from the other source',()=>{
  const value=result();value.balanceError='old balance failure';value.balanceRequested=false;
  assert.equal(describe(value,'local').error,false);
  value.balanceRequested=true;value.balanceError='';value.localUsageError='old local failure';value.localUsageRequested=false;
  assert.deepEqual(describe(value,'balance'),{message:'余额同步完成',details:'',error:false});
  assert.equal(describe(value,'balance',false).message,'未启用余额同步');
});
