'use strict';
// Expected statistics were captured from the independent legacy reference.
// Rebase dates only; never calculate expectations from the implementation under test.
const fs=require('node:fs'),path=require('node:path');
const fixtures=JSON.parse(fs.readFileSync(path.join(__dirname,'reference-usage-fixtures.json'),'utf8'));
const today=new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date());
const delta=Date.parse(today)-Date.parse(fixtures[0].expected.range.start);
function rebase(value,key){
  if(typeof value==='number'&&['created_at','timestamp'].includes(key))return value+delta/1000;
  if(typeof value==='string'&&/^\d{4}-\d{2}-\d{2}(?: \d{2}:\d{2})?$/.test(value))return new Date(Date.parse(value.slice(0,10))+delta).toISOString().slice(0,10)+value.slice(10);
  if(Array.isArray(value))return value.map(item=>rebase(item));
  if(value&&typeof value==='object')return Object.fromEntries(Object.entries(value).map(([k,v])=>[k,rebase(v,k)]));
  return value;
}
const destination=path.resolve(process.argv[2]||path.join(__dirname,'../build/usage-fixtures.json'));
fs.mkdirSync(path.dirname(destination),{recursive:true});
fs.writeFileSync(destination,JSON.stringify(rebase(fixtures)));
console.log('Prepared',fixtures.length,'independent statistics fixtures for',today);
