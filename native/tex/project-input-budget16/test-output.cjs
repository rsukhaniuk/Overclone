'use strict';
// Test binary invokes actual wrapper callbacks with a controlled stub producer.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{spawnSync}=require('node:child_process');
const [directory,out]=process.argv.slice(2),cases=[];
for(const mode of [0,1,2,3]){const r=spawnSync(process.execPath,[path.join(directory,'unit.js'),String(mode)],{encoding:'utf8',timeout:30000});assert.equal(r.status,0,r.stdout+r.stderr);assert.match(r.stdout,new RegExp('actual callback boundary mode'+mode+' passed'));cases.push({mode,passed:true,stdout:r.stdout});}
fs.writeFileSync(out,JSON.stringify({status:'pass',cases,scope:'actual wrapper callback unit witness with controlled stub; not genuine PDF conversion'},null,2)+'\n');console.log('8MiB output write/putc/overflow/sticky +16MiB stream read callbacks pass');
