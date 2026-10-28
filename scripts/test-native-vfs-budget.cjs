#!/usr/bin/env node
'use strict';
// Benign, tiny FS accounting tests. No TeX/WASM execution or confinement corpus.
// Uses exact FS/MEMFS/I/O literals from the pinned loader, not a second quota model.
// Run in the cached test container: node scripts/test-native-vfs-budget.cjs LOADER
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createHash, webcrypto } = require('node:crypto');
const hash = data => createHash('sha256').update(data).digest('hex');
const loader = fs.readFileSync(process.argv[2] || (() => { throw new Error('explicit pinned loader path required'); })());
assert.equal(loader.length, 72275);
assert.equal(hash(loader), '39384532be2e32fd09dba99226db9da7572032c70da2bb2428fc9123c7fd163f');
const source = loader.toString('utf8');
const worker = fs.readFileSync(path.join(__dirname, '../src/Client/wwwroot/js/compile-port-worker.js'), 'utf8');
assert.ok(Buffer.byteLength(worker) <= 32768, 'Blob-worker admission ceiling');
const between = (a, b) => {
  const start = source.indexOf(a), end = source.indexOf(b, start);
  assert.ok(start >= 0 && end > start, `pinned literal markers: ${a}`);
  return source.slice(start, end);
};
const literals = [
  between('var PATH=', 'var initRandomFill='),
  between('var PATH_FS=', 'var FS_stdin_getChar_buffer='),
  between('var MEMFS=', 'var FS_modeStringToFlags='),
  between('var FS_modeStringToFlags=', 'var asyncLoad='),
  between('var FS={', 'var HEAP32;'),
  between('var doWritev=', 'var handleException=')
].join('\n');
const literalManifest = {
  nodeVersion: process.version,
  loaderSha256: hash(loader), workerSha256: hash(worker), workerBytes: Buffer.byteLength(worker),
  fsMemfsIoLiteralSha256: hash(literals),
  scope: 'Exact pinned JS filesystem literals plus product-installed hooks; isolated JS environment, no WASM/native C# proof.'
};
function environment() {
  const messages = [], device = [];
  const ctx = vm.createContext({
    crypto: webcrypto, TextEncoder, performance, console: { log() {}, error() {} },
    postMessage: message => messages.push(message), deviceByte: byte => device.push(byte)
  });
  vm.runInContext(`
    var self=globalThis;
    var allocations=0, allocationBytes=0,failNextAllocation=false,failAfterSet=false;
    var PlainU8=Uint8Array, PlainI8=Int8Array;
    Uint8Array=class extends PlainU8 {
      constructor(...args){if(failNextAllocation){failNextAllocation=false;throw new RangeError('injected tiny allocator failure');}super(...args);allocations++;allocationBytes+=this.byteLength;}
      set(...args){super.set(...args);if(failAfterSet){failAfterSet=false;throw new Error('injected copy completion failure');}}
    };
    Int8Array=class extends PlainI8 { constructor(...args){super(...args);allocations++;allocationBytes+=this.byteLength;} };
    var HEAPU8=new Uint8Array(4096),HEAP8=new Int8Array(HEAPU8.buffer),HEAPU32=new Uint32Array(HEAPU8.buffer);
    var abort=()=>{throw new Error('original unsupported mmap allocator entered');};
    ${literals}
    FS.nameTable=new Array(4096);
    FS.root=MEMFS.mount({});FS.root.mount={type:MEMFS,mountpoint:'/',mounts:[]};
    FS.mkdir('/work');FS.mkdir('/dev');
    FS.createDevice('/dev','stdout',undefined,deviceByte);
    FS.writeFile('/work/input.txt',new Uint8Array([65,66,67]));
    FS.writeFile('/work/carry.aux',new Uint8Array([68,69,70]));
    var baselineDeviceWrite=FS.open('/dev/stdout','w').stream_ops.write;
    var prepareExecutionContext=()=>FS.chdir('/work');
    var mainCalls=0,nativeCalls=0;
    var cwrap=()=>()=>{mainCalls++;};
    var _compileLaTeX=()=>{nativeCalls++;return 0;};
    var _compileBibtex=()=>{nativeCalls++;return 0;};
    self.memlog='';
    ${worker}
  `, ctx, { timeout: 2000 });
  return {
    ctx, messages, device,
    run: text => vm.runInContext(text, ctx, { timeout: 2000 }),
    get fs() { return ctx.FS; }, get mem() { return ctx.MEMFS; },
    bytes: (length, value = 90) => vm.runInContext(`new Uint8Array(${length}).fill(${value})`, ctx),
    install: (maximum, maximumLog = 65536, nodeLimit = 64) => ctx.installNativeVfsBudget(ctx.FS, ctx.MEMFS, maximum, maximumLog, nodeLimit)
  };
}
const records = [], measurements = [];
async function test(name, action) {
  await action(); records.push({ name, passed: true });
}
function node(e, name) { return e.fs.lookupPath('/work/' + name).node; }
function refuse(e, action, existing) {
  const calls = e.run('allocations'), allocated = e.run('allocationBytes');
  const inode = e.fs.nextInode;
  const saved = existing && { contents: existing.contents, used: existing.usedBytes, mtime: existing.mtime, ctime: existing.ctime };
  assert.throws(action, /VFS quota:/);
  assert.equal(e.run('allocations'), calls, 'no backing allocator/slice on refusal');
  assert.equal(e.run('allocationBytes'), allocated, 'no data allocation bytes on refusal');
  assert.equal(e.fs.nextInode, inode, 'no node allocator on refusal');
  if (saved) {
    assert.equal(existing.contents, saved.contents, 'original backing retained');
    assert.equal(existing.usedBytes, saved.used);
    assert.equal(existing.mtime, saved.mtime);
    assert.equal(existing.ctime, saved.ctime);
  }
}
async function main() {
  await test('literal hashes and installed aliases agree with the real pinned loader', async () => {
    const e = environment();
    const shapes=e.run('nativeVfsShapes');assert.equal(Object.keys(shapes).length,33);
    for(const [name,digest] of Object.entries(shapes)){
      const keys=name.split('.');let object=name.startsWith('FS.')?e.fs:e.mem;
      if(keys[0]==='FS')keys.shift();
      for(const key of keys.slice(0,-1))object=object[key];
      assert.equal(hash(Function.prototype.toString.call(object[keys.at(-1)])),digest,`literal hook association: ${name}`);
    }
    const n = node(e, 'carry.aux');
    n.stream_ops = { ...n.stream_ops };n.node_ops = { ...n.node_ops };
    const stream = e.fs.open('/work/carry.aux', 'r+');stream.stream_ops = { ...stream.stream_ops };
    const old = e.mem.stream_ops.write;
    await e.install(512);
    assert.notEqual(e.mem.stream_ops.write, old);
    for (const alias of [e.mem.ops_table.file.stream, n.stream_ops, stream.stream_ops]) assert.equal(alias.write, e.mem.stream_ops.write);
    assert.equal(n.node_ops.setattr, e.mem.node_ops.setattr);
    e.fs.write(stream, e.bytes(1), 0, 1, 0);assert.equal(n.usedBytes, 3);
    assert.equal(e.fs.open('/dev/stdout','w').stream_ops.write, e.run('baselineDeviceWrite'));
  });
  await test('baseline larger than output quota is not charged as generated data', async () => {
    const e = environment();e.fs.writeFile('/work/format.bin', e.bytes(40));
    const b = await e.install(8), s = b.snapshot();
    assert.equal(s.baselineBytes, 46);assert.equal(s.backingBytes, 0);assert.equal(s.growthBytes, 0);
    assert.equal(node(e,'format.bin').usedBytes, 40);
    measurements.push({case:'baseline exclusion',...s});
  });
  await test('exact per-file and aggregate boundary succeeds; unreturned third file refused', async () => {
    const e = environment(), b = await e.install(8), data = e.bytes(8);
    e.fs.writeFile('/work/one.txt', data);e.fs.writeFile('/work/two.txt', data);
    assert.equal(b.snapshot().growthBytes,16);assert.equal(b.snapshot().backingBytes,16);
    measurements.push({case:'exact aggregate boundary',...b.snapshot()});
    e.fs.create('/work/three.txt');const n = node(e,'three.txt');
    refuse(e,()=>e.mem.stream_ops.write({node:n},data,0,1,0,false),n);
    assert.equal(b.failed,true);
  });
  await test('first empty-file slice refused before allocator/metadata mutation', async () => {
    const e = environment();e.fs.create('/work/new.txt');const n=node(e,'new.txt');
    await e.install(8);const data=e.bytes(9);
    refuse(e,()=>e.mem.ops_table.file.stream.write({node:n},data,0,9,0,false),n);
  });
  await test('canOwn reserves full backing buffer, not only small view length', async () => {
    const e = environment();e.fs.create('/work/new.txt');const n=node(e,'new.txt');
    const data=e.bytes(17).subarray(0,1);await e.install(8);
    refuse(e,()=>e.mem.stream_ops.write({node:n},data,0,1,0,true),n);
  });
  await test('canOwn boundary and observed actual backing capacity', async () => {
    const e = environment();e.fs.create('/work/new.txt');const n=node(e,'new.txt');
    const data=e.bytes(16).subarray(0,8);const b=await e.install(8);
    e.mem.stream_ops.write({node:n},data,0,8,0,true);
    assert.equal(n.contents.buffer.byteLength,16);assert.equal(b.snapshot().backingBytes,16);
  });
  await test('native Int8 HEAP write disables ownership and retains signed-byte storage', async () => {
    const e=environment();e.fs.create('/work/new.txt');const b=await e.install(8);
    e.run(`HEAP8[32]=65;MEMFS.stream_ops.write({node:FS.lookupPath('/work/new.txt').node},HEAP8,32,1,0,true);`);
    const n=node(e,'new.txt');assert.equal(n.usedBytes,1);assert.notEqual(n.contents.buffer,e.ctx.HEAPU8.buffer);
    assert.equal(b.snapshot().backingBytes,1);e.fs.truncate('/work/new.txt',2);assert.equal(n.usedBytes,2);
  });
  await test('ordinary append honors pinned minimum256 growth and real alias', async () => {
    const e=environment(),b=await e.install(512);e.fs.writeFile('/work/new.txt',e.bytes(1));
    const stream=e.fs.open('/work/new.txt','a');e.fs.write(stream,e.bytes(1),0,1);
    assert.equal(node(e,'new.txt').usedBytes,2);assert.equal(node(e,'new.txt').contents.length,256);
    assert.equal(b.snapshot().backingBytes,257);
    measurements.push({case:'pinned minimum-growth capacity',...b.snapshot()});
  });
  await test('growth overallocation refused before write timestamp or buffer allocation', async () => {
    const e=environment();e.fs.writeFile('/work/new.txt',e.bytes(1));const n=node(e,'new.txt');
    await e.install(8);const data=e.bytes(1);
    refuse(e,()=>e.mem.stream_ops.write({node:n},data,0,1,1,false),n);
  });
  await test('direct expand route cannot bypass logical or capacity reservation', async () => {
    const e=environment();e.fs.create('/work/new.txt');const n=node(e,'new.txt');await e.install(8);
    refuse(e,()=>e.mem.expandFileStorage(n,9),n);
  });
  await test('truncate and carry rewrite converge; no reservation release', async () => {
    const e=environment(),b=await e.install(64),n=node(e,'carry.aux');
    const data=e.bytes(3,81);e.fs.truncate('/work/carry.aux',0);e.fs.writeFile('/work/carry.aux',data);
    assert.equal(n.usedBytes,3);assert.equal(b.snapshot().growthBytes,0);assert.equal(b.snapshot().backingBytes,3);
    e.fs.truncate('/work/carry.aux',2);assert.equal(b.snapshot().backingBytes,5);
    e.fs.truncate('/work/carry.aux',3);assert.equal(b.snapshot().backingBytes,8);
    assert.equal(n.contents[0],81);
  });
  await test('setattr preflight refuses oversized truncate before changing metadata', async () => {
    const e=environment(),n=node(e,'carry.aux');await e.install(8);
    refuse(e,()=>n.node_ops.setattr(n,{size:9,mtime:1}),n);
  });
  await test('ftruncate uses existing stream/node alias with pre-allocation bound', async () => {
    const e=environment(),n=node(e,'carry.aux'),stream=e.fs.open('/work/carry.aux','r+');
    stream.stream_ops={...stream.stream_ops};await e.install(8);
    refuse(e,()=>e.fs.ftruncate(stream.fd,9),n);
  });
  await test('rename/delete/recreate do not release identity reservations', async () => {
    const e=environment(),b=await e.install(8),data=e.bytes(8);
    e.fs.writeFile('/work/new.txt',data);const n=node(e,'new.txt');
    e.fs.rename('/work/new.txt','/work/renamed.txt');assert.equal(node(e,'renamed.txt'),n);
    e.fs.unlink('/work/renamed.txt');e.fs.writeFile('/work/new.txt',data);
    assert.notEqual(node(e,'new.txt'),n);assert.equal(b.snapshot().backingBytes,16);assert.equal(b.snapshot().growthBytes,16);
    assert.equal(b.snapshot().newNodes,2);
  });
  await test('renaming onto existing node retains both open node identities', async () => {
    const e=environment(),b=await e.install(64),stream=e.fs.open('/work/carry.aux','r+'),old=stream.node;
    e.fs.writeFile('/work/new.txt',e.bytes(4));const fresh=node(e,'new.txt');
    e.fs.rename('/work/new.txt','/work/carry.aux');assert.equal(node(e,'carry.aux'),fresh);
    e.fs.write(stream,e.bytes(1),0,1,0);assert.equal(old.usedBytes,3);assert.equal(b.snapshot().newNodes,1);
  });
  await test('64 lifetime node slots include directories and cannot be recycled', async () => {
    const e=environment(),b=await e.install(8);
    e.fs.mkdir('/work/folder');e.fs.rmdir('/work/folder');
    for(let i=0;i<63;i++){e.fs.create(`/work/n${i}.txt`);e.fs.unlink(`/work/n${i}.txt`);}
    assert.equal(b.snapshot().newNodes,64);
    refuse(e,()=>e.fs.create('/work/last.txt'));
    assert.equal(e.fs.analyzePath('/work/last.txt').exists,false);
  });
  await test('safe integer/range arithmetic refuses without allocator/storage mutation', async () => {
    for(const value of [NaN,Infinity,-1,0.5,Number.MAX_SAFE_INTEGER]){
      const e=environment(),n=node(e,'carry.aux');await e.install(8);const data=e.bytes(1);
      refuse(e,()=>e.mem.stream_ops.write({node:n},data,0,1,value,false),n);
    }
  });
  await test('invalid requested bounds fail setup without touching installed tables', async () => {
    for(const value of [0,-1,0.5,NaN,Infinity,Number.MAX_SAFE_INTEGER]){
      const e=environment(),old=e.mem.stream_ops.write;
      await assert.rejects(e.install(value),/unsupported pinned VFS shape/);assert.equal(e.mem.stream_ops.write,old);
    }
  });
  await test('unknown/missing function or stale table shape fails before hooks/native', async () => {
    for(const change of [
      e=>{delete e.mem.ops_table.file.stream.write;},
      e=>{e.mem.expandFileStorage=function unexpectedApi(){};},
      e=>{node(e,'carry.aux').stream_ops={...node(e,'carry.aux').stream_ops,allocate(){}};},
      e=>{delete e.fs.write;},
      e=>{e.fs.write=function changedDispatcher(){};},
      e=>{e.fs.read=function changedReadDispatcher(){};},
      e=>{e.fs.mkdir('/work/folder');node(e,'folder').mounted={};},
      e=>{e.run('doWritev=()=>0');}
    ]){
      const e=environment(),old=e.mem.stream_ops.write;change(e);
      await assert.rejects(e.install(8),/unsupported pinned VFS shape/);assert.equal(e.mem.stream_ops.write,old);
      assert.equal(e.run('mainCalls+nativeCalls'),0);
    }
  });
  await test('msync calls guarded write; mmap rejected before unsupported allocator', async () => {
    const e=environment(),n=node(e,'carry.aux'),stream=e.fs.open('/work/carry.aux','r+');await e.install(8);
    e.fs.msync(stream,e.bytes(1),0,1,0);assert.equal(n.contents[0],90);
    refuse(e,()=>e.fs.mmap(stream,1,0,1,2),n);
  });
  await test('read copy work bound is independent, exact and pre-allocation', async () => {
    const e=environment();e.fs.writeFile('/work/new.txt',e.bytes(8));const b=await e.install(8);
    e.fs.readFile('/work/new.txt');e.fs.readFile('/work/new.txt');assert.equal(b.snapshot().copyBytes,16);
    refuse(e,()=>e.fs.readFile('/work/new.txt'),node(e,'new.txt'));
  });
  await test('implicit allocating string conversion refused before truncate', async () => {
    const e=environment(),n=node(e,'carry.aux');await e.install(8);
    refuse(e,()=>e.fs.writeFile('/work/carry.aux','hello'),n);
  });
  await test('typed high-level overwrite refuses BEFORE opening/truncating carry', async () => {
    const e=environment(),n=node(e,'carry.aux');await e.install(8);const data=e.bytes(9);
    refuse(e,()=>e.fs.writeFile('/work/carry.aux',data),n);
  });
  await test('high-level append refuses BEFORE existing storage mutation', async () => {
    const e=environment(),n=node(e,'carry.aux');await e.install(8);const data=e.bytes(6);
    refuse(e,()=>e.fs.writeFile('/work/carry.aux',data,{flags:'a'}),n);
  });
  await test('high-level ownership reserves backing BEFORE truncating carry', async () => {
    const e=environment(),n=node(e,'carry.aux'),data=e.bytes(17).subarray(0,1);await e.install(8);
    refuse(e,()=>e.fs.writeFile('/work/carry.aux',data,{canOwn:true}),n);
  });
  await test('createDataFile quota refusal leaves parent/inode/allocator unchanged', async () => {
    const e=environment();await e.install(8);const data=e.bytes(9),parent=node(e,'input.txt').parent;
    const keys=Object.keys(parent.contents);
    refuse(e,()=>e.fs.createDataFile('/work','new.txt',data,true,true));
    assert.deepEqual(Object.keys(parent.contents),keys);
  });
  await test('createDataFile normal tiny byte sequence uses guarded creation/write', async () => {
    const e=environment(),b=await e.install(8);
    e.fs.createDataFile('/work','new.txt',e.bytes(4),true,true);
    assert.equal(node(e,'new.txt').usedBytes,4);assert.equal(b.snapshot().newNodes,1);assert.equal(b.snapshot().backingBytes,4);
  });
  await test('failed tiny allocator keeps reservation and latches failure', async () => {
    const e=environment();e.fs.create('/work/new.txt');const n=node(e,'new.txt'),b=await e.install(8),data=e.bytes(4),old=n.contents;
    e.run('failNextAllocation=true');assert.throws(()=>e.mem.stream_ops.write({node:n},data,0,4,0,false),/injected tiny allocator failure/);
    assert.equal(n.contents,old);assert.equal(b.snapshot().backingBytes,4);assert.equal(b.failed,true);
    refuse(e,()=>e.mem.stream_ops.write({node:n},data,0,1,0,false),n);
  });
  await test('failure after replacement buffer retains full reservation, never refunds', async () => {
    const e=environment(),n=node(e,'carry.aux'),old=n.contents,b=await e.install(512);
    e.run('failAfterSet=true');assert.throws(()=>e.mem.expandFileStorage(n,4),/injected copy completion failure/);
    assert.notEqual(n.contents,old);assert.equal(n.contents.length,256);assert.equal(b.snapshot().backingBytes,256);assert.equal(b.failed,true);
    measurements.push({case:'retained allocation after injected copy failure',...b.snapshot()});
  });
  await test('directory rename retains descendant node accounting and writable carry', async () => {
    const e=environment(),b=await e.install(64);
    e.fs.mkdir('/work/folder');e.fs.writeFile('/work/folder/new.txt',e.bytes(4));const n=node(e,'folder/new.txt');
    e.fs.rename('/work/folder','/work/renamed');assert.equal(node(e,'renamed/new.txt'),n);
    const stream=e.fs.open('/work/renamed/new.txt','r+');e.fs.write(stream,e.bytes(1),0,1,0);
    assert.equal(b.snapshot().newNodes,2);assert.equal(b.snapshot().backingBytes,4);
  });
  await test('oversize msync delegates to the guarded stale alias before mutation', async () => {
    const e=environment(),n=node(e,'carry.aux'),stream=e.fs.open('/work/carry.aux','r+');
    stream.stream_ops={...stream.stream_ops};await e.install(8);const data=e.bytes(9);
    refuse(e,()=>e.fs.msync(stream,data,0,9,0),n);
  });
  await test('ordinary two-vector native IO reserves staging and backing before copy', async () => {
    const e=environment();const stream=e.fs.open('/work/new.txt','w');e.ctx.target=stream;
    const b=await e.install(8);
    e.run(`HEAPU8.set([65,66,67,68],64);HEAPU32.set([64,2,66,2],0);doWritev(target,0,2);`);
    assert.equal(node(e,'new.txt').usedBytes,4);assert.equal(b.snapshot().backingBytes,4);assert.equal(b.snapshot().copyBytes,4);
    measurements.push({case:'two-vector real JS staging route',...b.snapshot()});
  });
  await test('vector quota refusal precedes even the staging allocation', async () => {
    const e=environment();e.ctx.target=e.fs.open('/work/new.txt','w');const n=node(e,'new.txt');await e.install(8);
    e.run(`HEAPU32.set([64,5,69,4],0)`);
    refuse(e,()=>e.run('doWritev(target,0,2)'),n);
  });
  await test('vector copy work limit cannot be bypassed by in-place rewrites', async () => {
    const e=environment();e.fs.writeFile('/work/new.txt',e.bytes(4));e.ctx.target=e.fs.open('/work/new.txt','r+');
    const n=node(e,'new.txt'),b=await e.install(4);e.run('HEAPU32.set([64,2,66,2],0)');
    e.run('doWritev(target,0,2,0);doWritev(target,0,2,0)');assert.equal(b.snapshot().copyBytes,8);
    refuse(e,()=>e.run('doWritev(target,0,2,0)'),n);
  });
  await test('device writes unchanged; vector device copy uses log not output allowance', async () => {
    const e=environment();e.ctx.target=e.fs.open('/dev/stdout','w');const b=await e.install(1,64);
    e.fs.write(e.ctx.target,e.bytes(3,65),0,3);
    e.run('HEAPU8.set([66,67,68],64);HEAPU32.set([64,1,65,2],0);doWritev(target,0,2)');
    assert.deepEqual(e.device,[65,65,65,66,67,68]);assert.equal(b.snapshot().backingBytes,0);assert.equal(b.snapshot().growthBytes,0);
    assert.equal(b.snapshot().deviceCopyBytes,3);assert.equal(b.snapshot().copyBytes,0);
    measurements.push({case:'device staging independent of output quota',...b.snapshot()});
  });
  const frame = e => ({
    tag:{version:3,profile:2,runtimeIdentity:'0'.repeat(64),tool:'latex'},operation:'latex',mainEntry:'main.tex',
    files:[{name:'main.tex',bytes:e.bytes(1)}],outputs:['main.pdf'],maxOutputBytes:8,maxLogBytes:64
  });
  await test('modeled worker: caught quota and native return0 still resource-limit', async () => {
    const e=environment();e.run(`_compileLaTeX=()=>{nativeCalls++;try{FS.writeFile('/work/note.txt',new Uint8Array(9));}catch{}return 0;};`);
    await e.ctx.execute({data:frame(e)});
    const reply=e.messages.at(-1).reply;assert.equal(reply.outcome,'resource-limit');assert.equal(reply.nativeReturn,0);
    assert.equal(reply.entered,true);assert.equal(reply.files.length,0);assert.equal(e.run('nativeCalls'),1);
  });
  await test('modeled worker: unknown loader shape fails setup before main/native', async () => {
    const e=environment();e.run(`prepareExecutionContext=()=>{FS.chdir('/work');MEMFS.expandFileStorage=function changedShape(){};};`);
    await e.ctx.execute({data:frame(e)});
    const reply=e.messages.at(-1).reply;assert.equal(reply.outcome,'setup-failure');assert.equal(reply.entered,false);
    assert.equal(e.run('mainCalls+nativeCalls'),0);
  });
  await test('modeled worker: existing log threshold/latch remains resource-limit', async () => {
    const e=environment();e.run(`_compileLaTeX=()=>{try{self.memlog='abcdefghijklmnopqrstuv';}catch{}return 0;};`);
    await e.ctx.execute({data:frame(e)});assert.equal(e.messages.at(-1).reply.outcome,'resource-limit');
  });
  await test('modeled worker: plain successful callback preserves structured reply/input hash', async () => {
    const e=environment();await e.ctx.execute({data:frame(e)});const reply=e.messages.at(-1).reply;
    assert.equal(reply.outcome,'native-return');assert.equal(reply.nativeReturn,0);assert.equal(reply.inputs.length,1);
    assert.equal(reply.inputs[0].sha256,hash(Buffer.from([90])));assert.equal(e.run('mainCalls+nativeCalls'),2);
  });
  await test('v4 exact128 tiny node boundary and deletion do not refund attempts', async () => {
    const e=environment(),b=await e.install(8,64,128);
    for(let i=0;i<128;i++)e.fs.create('/work/v4-'+i+'.txt');
    assert.equal(b.snapshot().newNodes,128);assert.equal(b.snapshot().nodeLimit,128);
    e.fs.unlink('/work/v4-0.txt');assert.equal(b.snapshot().newNodes,128);
    refuse(e,()=>e.fs.create('/work/one-more.txt'));assert.equal(b.failed,true);
    assert.equal(b.snapshot().newNodes,128);refuse(e,()=>e.fs.create('/work/still-latched.txt'));
  });
  await test('v4 failed128th tiny construction retains reservation and latch', async () => {
    const e=environment(),b=await e.install(8,64,128);
    for(let i=0;i<127;i++)e.fs.create('/work/v4-'+i+'.txt');
    e.run("FS.createNode=()=>{throw new Error('injected tiny node creation failure');};");
    assert.throws(()=>e.fs.create('/work/fail128.txt'),/injected tiny node creation failure/);
    assert.equal(b.snapshot().newNodes,128);assert.equal(b.failed,true);
    refuse(e,()=>e.fs.create('/work/no-refund.txt'));assert.equal(b.snapshot().newNodes,128);
  });
  await test('only bounded64 or128 node budgets accepted, before any hook mutation', async () => {
    for(const limit of [0,63,65,129,Infinity,64.5,'128']){
      const e=environment(),write=e.mem.stream_ops.write,inode=e.fs.nextInode;
      await assert.rejects(e.install(8,64,limit),/unsupported pinned VFS shape/);
      assert.equal(e.mem.stream_ops.write,write);assert.equal(e.fs.nextInode,inode);
    }
  });
  const v4frame=e=>({...frame(e),tag:{...frame(e).tag,version:4},outputs:['main.pdf','main.aux','main.bbl','main.blg','main.log','main.toc','main.lof','main.lot','main.out']});
  await test('modeled v4 exact768 file128 output frame on actual extracted FS', async () => {
    const e=environment(),d=v4frame(e),data=e.bytes(1);
    d.files=[{name:'main.tex',bytes:data},...Array.from({length:767},(_,i)=>({name:'tiny-'+i+'.txt',bytes:data}))];
    d.outputs.push(...Array.from({length:119},(_,i)=>'chapter'+i+'.aux'));
    await e.ctx.execute({data:d});const reply=e.messages.at(-1).reply;
    assert.equal(reply.outcome,'native-return');assert.equal(reply.inputs.length,768);assert.equal(d.outputs.length,128);
    assert.equal(e.run('mainCalls+nativeCalls'),2);
  });
  const invalidFrames=[
    ['v4 file769',d=>{d.files.push(...Array.from({length:768},(_,i)=>({name:'tiny'+i+'.txt',bytes:d.files[0].bytes})));}],
    ['v4 output129',d=>{d.outputs.push(...Array.from({length:120},(_,i)=>'c'+i+'.aux'));}],
    ['wrong profile',d=>{d.tag.profile=1;}],['uppercase identity',d=>{d.tag.runtimeIdentity='A'.repeat(64);}],
    ['unknown version',d=>{d.tag.version=6;}],['main entry',d=>{d.mainEntry='chapter.tex';}],
    ['absolute path',d=>{d.files[0].name='/main.tex';}],['traversal',d=>{d.files[0].name='../main.tex';}],
    ['NUL',d=>{d.files[0].name='main\\0.tex';}],['backslash',d=>{d.files[0].name='a\\\\main.tex';}],
    ['duplicate',d=>{d.files.push(d.files[0]);}],['case alias',d=>{d.files.push({name:'MAIN.TEX',bytes:d.files[0].bytes});}],
    ['NFC alias',d=>{d.files.push({name:'é.tex',bytes:d.files[0].bytes},{name:'e\\u0301.tex',bytes:d.files[0].bytes});}],
    ['input prefix',d=>{d.files.push({name:'x',bytes:d.files[0].bytes},{name:'x/child.tex',bytes:d.files[0].bytes});}],
    ['output prefix',d=>{d.files.push({name:'main.toc/child.tex',bytes:d.files[0].bytes});}],
    ['runtime output alias',d=>{d.files.push({name:'MAIN.TOC',bytes:d.files[0].bytes});}],
    ['PDF injection',d=>{d.files.push({name:'main.pdf',bytes:d.files[0].bytes});}],
    ['duplicate output',d=>{d.outputs.push('main.toc');}],['unapproved output',d=>{d.outputs.push('main.idx');}],
    ['native path limit',d=>{d.files.push({name:'x'.repeat(246)+'.tex',bytes:d.files[0].bytes});}],
    ['nonbytes',d=>{d.files[0].bytes=[1];}],['generated byte cap',d=>{d.maxOutputBytes=8388609;}],
    ['v3 file513 unchanged',d=>{d.tag.version=3;d.files.push(...Array.from({length:512},(_,i)=>({name:'old'+i+'.txt',bytes:d.files[0].bytes})));}],
    ['v3 output65 unchanged',d=>{d.tag.version=3;d.outputs.push(...Array.from({length:56},(_,i)=>'old'+i+'.aux'));}]
  ];
  for(const [name,change] of invalidFrames)await test('modeled pre-seed frame refusal: '+name,async()=>{
    const e=environment(),d=v4frame(e);change(d);const saved=node(e,'carry.aux'),contents=saved.contents,inode=e.fs.nextInode;
    e.run('var seedWrites=0;FS.mkdirTree=()=>{seedWrites++;};FS.writeFile=()=>{seedWrites++;};');
    await e.ctx.execute({data:d});const reply=e.messages.at(-1).reply;
    assert.equal(reply.outcome,'setup-failure');assert.equal(reply.entered,false);
    assert.equal(e.run('seedWrites+mainCalls+nativeCalls'),0);assert.equal(e.fs.nextInode,inode);assert.equal(saved.contents,contents);assert.equal(saved.usedBytes,3);
  });
  await test('modeled v4 seeded declared TOC remains an input/output, not omitted',async()=>{
    const e=environment(),d=v4frame(e);d.files.push({name:'main.toc',bytes:e.bytes(1,65)});
    await e.ctx.execute({data:d});const reply=e.messages.at(-1).reply;
    assert.equal(reply.outcome,'native-return');assert.equal(reply.files.find(f=>f.name==='main.toc').bytes[0],65);
    assert.equal(reply.inputs.find(f=>f.name==='main.toc').sha256,hash(Buffer.from([65])));
  });
  const v5frame=(e,root='dir/paper.tex',tool='latex')=>{
    const leaf=root.slice(root.lastIndexOf('/')+1),job=leaf.slice(0,-4);
    let mainEntry=root;
    if(tool==='bibtex')mainEntry=leaf;else if(root.includes(' '))mainEntry='"'+root+'"';
    return {...v4frame(e),tag:{...frame(e).tag,version:5,tool},operation:tool,selectedRoot:root,mainEntry,
      files:[{name:root,bytes:e.bytes(1)},...(tool==='bibtex'?[{name:job+'.aux',bytes:e.bytes(1)}]:[])],
      outputs:['.pdf','.aux','.bbl','.blg','.log','.toc','.lof','.lot','.out'].map(ext=>job+ext)};
  };
  for(const root of ['renamed.tex','dir/paper.tex','тези/праця.tex','space dir/paper.tex','dir/Paper.TEX',
    'my thesis.tex','dir/my thesis.tex','space dir/my thesis.tex','a '+'x'.repeat(243)+'.tex'])
    for(const tool of ['latex','bibtex'])await test('modeled v5 exact root/tool entry: '+root+' '+tool,async()=>{
      const e=environment(),d=v5frame(e,root,tool);
      e.run('var literalEntry; cwrap=()=>entry=>{literalEntry=entry;mainCalls++;};');
      await e.ctx.execute({data:d});const reply=e.messages.at(-1).reply;
      assert.equal(reply.outcome,'native-return');assert.equal(e.run('literalEntry'),d.mainEntry);
      assert.equal(reply.inputs[0].name,root);assert.equal(e.run('mainCalls+nativeCalls'),2);
      assert.equal(reply.inputs[0].sha256,hash(Buffer.from([90])));
    });
  const invalid5=[
    ['wrong native latex argument',d=>{d.mainEntry='paper.tex';}],
    ['unnecessary quote',d=>{d.mainEntry='"dir/paper.tex"';}],
    ['missing selected root',d=>{delete d.selectedRoot;}],
    ['root not captured',d=>{d.selectedRoot='other.tex';d.mainEntry='other.tex';}],
    ['old fixed outputs',d=>{d.outputs=d.outputs.map(n=>n.replace('paper.','main.'));}],
    ['path-prefix output',d=>{d.outputs=d.outputs.map(n=>'dir/'+n);}],
    ['case output alias',d=>{d.files.push({name:'PAPER.PDF',bytes:d.files[0].bytes});}],
    ['output directory prefix',d=>{d.files.push({name:'paper.toc/child.tex',bytes:d.files[0].bytes});}],
    ['wrong v5 profile',d=>{d.tag.profile=1;}],
    ['unknown protocol',d=>{d.tag.version=6;}],
    ['TeX command syntax',d=>{d.selectedRoot=d.mainEntry='dir/paper%.tex';d.files[0].name=d.selectedRoot;}],
    ['quote syntax',d=>{d.selectedRoot=d.mainEntry='dir/pap"er.tex';d.files[0].name=d.selectedRoot;}],
    ['malformed utf16',d=>{d.selectedRoot=d.mainEntry='dir/a\ud800.tex';d.files[0].name=d.selectedRoot;}],
    ['overlong native lookup',d=>{d.selectedRoot=d.mainEntry='x'.repeat(246)+'.tex';d.files[0].name=d.selectedRoot;}],
    ['bibtex selected nested argument',d=>{d.operation=d.tag.tool='bibtex';d.files.push({name:'paper.aux',bytes:d.files[0].bytes});}],
    ['bibtex missing aux',d=>{d.operation=d.tag.tool='bibtex';d.mainEntry='paper.tex';}],
    ['bibtex empty aux',d=>{d.operation=d.tag.tool='bibtex';d.mainEntry='paper.tex';d.files.push({name:'paper.aux',bytes:d.files[0].bytes.subarray(0,0)});}],
    ['unquoted spaced latex',(d,e)=>{Object.assign(d,v5frame(e,'dir/my thesis.tex'));d.mainEntry=d.selectedRoot;}],
    ['quoted spaced bibtex',(d,e)=>{Object.assign(d,v5frame(e,'dir/my thesis.tex','bibtex'));d.mainEntry='"my thesis.tex"';}],
    ['nested spaced bibtex argument',(d,e)=>{Object.assign(d,v5frame(e,'dir/my thesis.tex','bibtex'));d.mainEntry=d.selectedRoot;}],
    ['spaced basename leading space',(d,e)=>{Object.assign(d,v5frame(e,'dir/ my thesis.tex'));}],
    ['spaced basename trailing space',(d,e)=>{Object.assign(d,v5frame(e,'my thesis .tex'));}],
    ['spaced path trailing space',(d,e)=>{Object.assign(d,v5frame(e,'dir/my thesis.tex '));}],
    ['spaced directory trailing space',(d,e)=>{Object.assign(d,v5frame(e,'dir /my thesis.tex'));}],
    ['spaced control',(d,e)=>{Object.assign(d,v5frame(e,'dir/my thesis\\t.tex'));}],
    ['spaced quote',(d,e)=>{Object.assign(d,v5frame(e,'dir/my thesis".tex'));}],
    ['spaced TeX metacharacter',(d,e)=>{Object.assign(d,v5frame(e,'dir/my thesis%.tex'));}],
    ['spaced output alias',(d,e)=>{Object.assign(d,v5frame(e,'dir/my thesis.tex'));d.files.push({name:'MY THESIS.BBL',bytes:e.bytes(1)});}],
    ['spaced output prefix',(d,e)=>{Object.assign(d,v5frame(e,'dir/my thesis.tex'));d.files.push({name:'my thesis.toc/child.tex',bytes:e.bytes(1)});}],
    ['spaced NFC output alias',(d,e)=>{Object.assign(d,v5frame(e,'dir/é thesis.tex'));d.files.push({name:'e\\u0301 THESIS.PDF',bytes:e.bytes(1)});}],
    ['spaced path over byte limit',(d,e)=>{Object.assign(d,v5frame(e,'a '+'x'.repeat(244)+'.tex'));}]
  ];
  for(const [name,change] of invalid5)await test('modeled v5 pre-seed refusal: '+name,async()=>{
    const e=environment(),d=v5frame(e);change(d,e);
    e.run('var seedWrites=0;FS.mkdirTree=()=>{seedWrites++;};FS.writeFile=()=>{seedWrites++;};');
    await e.ctx.execute({data:d});const reply=e.messages.at(-1).reply;
    assert.equal(reply.outcome,'setup-failure');assert.equal(reply.entered,false);assert.equal(e.run('seedWrites+mainCalls+nativeCalls'),0);
  });
  console.log(JSON.stringify({ ...literalManifest, checks: records.length, records,measurements,
    limitations:['No WASM, native compilation, real C# pipeline, PDF oracle, whole-browser peak or confinement acceptance in this script.',
      'Worker result tests use benign stand-in native callbacks and are modeled-only; FS tests use the actual extracted pinned implementation.'] },null,2));
}
main().catch(error=>{console.error(error.stack||String(error));process.exitCode=1;});
