// Synthetic terminal-state guard oracles only; no browser/native acceptance.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {sameHashControl,terminalAcceptance}=require('../../scripts/test-rendered-source-binding-ui.cjs');
const first={hash:'a'.repeat(64),request:'1'.repeat(32)};
const sameBytes={hash:first.hash,request:'2'.repeat(32)};
const differentBytes={hash:'b'.repeat(64),request:sameBytes.request};
const observed=after=>({online:{compile:first,sameSourceCompile:after}});

test('completed remaining checks retain different actual hashes as an unmet mandatory cell',()=>{
 const record=observed(differentBytes),result=terminalAcceptance(record,true);
 assert.equal(result.result,'PARTIAL');
 assert.equal(result.missingAcceptanceCells.length,1);
 const cell=result.missingAcceptanceCells[0];
 assert.equal(cell.id,'same-hash-new-ticket');assert.equal(cell.required,true);assert.equal(cell.status,'failed');
 assert.deepEqual(cell.before,first);assert.deepEqual(cell.after,differentBytes);
 assert.match(cell.reason,/different PDF hashes/);
 assert.deepEqual(record,observed(differentBytes),'evaluation preserves the observed identities');
});

test('missing or malformed control evidence cannot become PASS after other checks complete',()=>{
 for(const after of [undefined,null,{},sameBytes.hash,{...sameBytes,hash:'invalid'},
  {...sameBytes,request:'invalid'},{...sameBytes,hash:[sameBytes.hash]},
  {...sameBytes,request:[sameBytes.request]}]){
  const result=terminalAcceptance(observed(after),true);
  assert.equal(result.result,'PARTIAL');
  assert.equal(result.missingAcceptanceCells[0].status,'not-observed');
 }
 assert.equal(terminalAcceptance({},true).result,'PARTIAL');
});

test('a reused compile ticket does not satisfy the equal-PDF-hash control',()=>{
 const result=terminalAcceptance(observed({...first}),true);
 assert.equal(result.result,'PARTIAL');
 assert.equal(result.missingAcceptanceCells[0].status,'failed');
 assert.match(result.missingAcceptanceCells[0].reason,/new compile ticket/);
});

test('caller booleans and prior PASS labels cannot override retained actual hash evidence',()=>{
 const record={...observed(differentBytes),result:'PASS',missingAcceptanceCells:[],
  acceptanceCells:{'same-hash-new-ticket':{status:'passed'}}};
 record.online.sameHashNewTicket=true;
 const result=terminalAcceptance(record,true);
 assert.equal(result.result,'PARTIAL');
 assert.equal(result.acceptanceCells['same-hash-new-ticket'].status,'failed');
});

test('observed equal hashes on distinct tickets permit PASS only after completion and clean teardown',()=>{
 assert.equal(sameHashControl(first,sameBytes).status,'passed');
 const result=terminalAcceptance(observed(sameBytes),true);
 assert.equal(result.result,'PASS');assert.deepEqual(result.missingAcceptanceCells,[]);
 for(const complete of [false,undefined])assert.equal(terminalAcceptance(observed(sameBytes),complete).result,'FAIL');
 for(const fault of [{failure:'later stale-source check failed'},
  {failure:{step:'cold offline',message:'native failure'}},{cleanupFailure:'browser failed to close'}])
  assert.equal(terminalAcceptance({...observed(sameBytes),...fault},true).result,'FAIL');
});

test('later workflow failures remain FAIL while preserving the unobserved same-hash cell',()=>{
 const result=terminalAcceptance({...observed(differentBytes),failure:'cold compile failed'},false);
 assert.equal(result.result,'FAIL');
 assert.equal(result.missingAcceptanceCells[0].id,'same-hash-new-ticket');
 assert.equal(result.missingAcceptanceCells[0].after.hash,differentBytes.hash);
});
