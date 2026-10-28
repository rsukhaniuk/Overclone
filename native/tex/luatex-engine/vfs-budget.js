// Minimum browser/WASM FFI: SDK6 MEMFS allocation, vector and console guards.
// Adapted from installNativeVfsBudget in compile-port-worker.js. No workflow logic.
// vfs-shapes.js precedes this pre-js inside the generated factory (no eval/fetch).
let ocVfsShapeReady = false, ocVfsShapeError = '', ocVfsShapeRefs = null;
let ocVfsBudget = null;
function ocVfsSurface() {
  const surface = {};
  for (const name of Object.keys(ocVfsExpectedShapes)) {
    let object = name.startsWith('MEMFS.') ? MEMFS : FS;
    const keys = name.split('.');
    if (name === 'doWritev') { surface[name] = doWritev; continue; }
    if (name === 'FS_modeStringToFlags') { surface[name] = FS_modeStringToFlags; continue; }
    if (name === 'SYSCALLS.getStr') { surface[name] = SYSCALLS.getStr; continue; }
    for (const key of keys.slice(1,-1)) object = object?.[key];
    surface[name] = object?.[keys.at(-1)];
  }
  return surface;
}
Module['preRun'] ||= [];
Module['preRun'].push(() => {
  addRunDependency('oc-vfs-shapes');
  (async () => {
    const refs = ocVfsSurface();
    for (const [name, expected] of Object.entries(ocVfsExpectedShapes)) {
      const fn = refs[name];
      if (typeof fn !== 'function') throw new Error('missing ' + name);
      const text = Function.prototype.toString.call(fn);
      if (text.length > 16384) throw new Error('oversize ' + name);
      const digest = new Uint8Array(await globalThis.crypto.subtle.digest('SHA-256', new TextEncoder().encode(text)));
      const hash = Array.from(digest, b => b.toString(16).padStart(2,'0')).join('');
      if (hash !== expected) throw new Error('changed ' + name);
    }
    ocVfsShapeRefs = refs;
    ocVfsShapeReady = true;
  })().catch(error => { ocVfsShapeError = String(error).slice(0,180); })
    .finally(() => removeRunDependency('oc-vfs-shapes'));
});
function ocVfsSnapshot() {
  return ocVfsBudget ? ocVfsBudget.snapshot() : { installed:false, shapeReady:ocVfsShapeReady, shapeError:ocVfsShapeError };
}
function ocInstallVfsBudget(format) {
  const shape = name => { throw new Error('unsupported SDK6 VFS shape: ' + name); };
  if (!ocVfsShapeReady || ocVfsBudget) shape(ocVfsShapeError || 'initialization');
  const current = ocVfsSurface(), original = ocVfsShapeRefs;
  for (const name of Object.keys(original)) if (current[name] !== original[name]) shape(name);
  if (FS.allocate !== undefined || !MEMFS.ops_table || !Array.isArray(FS.streams)) shape('tables');
  const maximum = format ? 33554432 : 8388608, cap = 16777216, logCap = 262144;
  const records = new Map(), tables = new Map(), ops = MEMFS.ops_table;
  let failed = false, reason = '', growthBytes = 0, backingBytes = 0, copyBytes = 0;
  let newNodes = 0, logBytes = 0, transportBytes = 0, logTruncated = false, creating = false, deniedReads = 0;
  let vectorCalls = 0, vectorStageBytes = 0, largestVectorBytes = 0;
  const failure = why => { failed = true; if (!reason) reason = why; };
  const fail = why => { failure(why); throw new FS.ErrnoError(51); }; // SDK6 WASI ENOSPC.
  const uint = value => { if (!Number.isSafeInteger(value) || value < 0) fail('integer'); return value; };
  const add = (a,b) => { uint(a); uint(b); if (b > Number.MAX_SAFE_INTEGER-a) fail('integer'); return a+b; };
  const bytesOnly = bytes => { if (!(bytes instanceof Uint8Array || bytes instanceof Int8Array)) fail('byte input'); };
  const work = FS.lookupPath('/work',{follow:true}).node;
  const inside = node => { for (let n=0; node && n<16384; ++n,node=node.parent) { if(node===work)return true; if(node===node.parent)break; } return false; };
  const project = (node, mutate=false) => {
    if (!records.has(node) || !inside(node)) fail('private /work node');
    if (mutate) {
      if (failed) fail('latched');
      const path = FS.getPath(node);
      if (path === '/work/runtime' || path.startsWith('/work/runtime/') || path === '/work/config' || path.startsWith('/work/config/')) fail('read-only runtime/config');
    }
  };
  const remember = (table,source,keys) => {
    if (!table || Object.keys(table).length !== keys.length) shape('alias keys');
    for (const key of keys) if (table[key] !== source[key] || !Object.getOwnPropertyDescriptor(table,key)?.writable) shape('alias ' + key);
    tables.set(table,source);
  };
  const fileKeys = ['llseek','read','write','mmap','msync'];
  remember(MEMFS.stream_ops,MEMFS.stream_ops,fileKeys);
  remember(MEMFS.node_ops,MEMFS.node_ops,Object.keys(MEMFS.node_ops));
  for (const kind of ['dir','file','link','chrdev']) remember(ops[kind].node,MEMFS.node_ops,Object.keys(ops[kind].node));
  remember(ops.file.stream,MEMFS.stream_ops,fileKeys);
  remember(ops.dir.stream,MEMFS.stream_ops,['llseek']);
  const pending = [work]; let baselineBytes = 0, baselineCapacity = 0;
  while (pending.length) {
    const node = pending.pop();
    if (records.has(node) || records.size >= 16384 || node.mounted || !(FS.isFile(node.mode) || FS.isDir(node.mode))) shape('baseline node');
    const regular = FS.isFile(node.mode), kind = regular ? 'file' : 'dir';
    remember(node.node_ops,MEMFS.node_ops,Object.keys(ops[kind].node));
    if (regular) {
      bytesOnly(node.contents);
      if (uint(node.usedBytes) > node.contents.length) shape('baseline file');
      if (!FS.getPath(node).startsWith('/work/runtime/') && node.usedBytes > 8388608) shape('project/carry size');
      remember(node.stream_ops,MEMFS.stream_ops,fileKeys);
      baselineBytes += node.usedBytes; baselineCapacity += node.contents.buffer.byteLength;
    } else for (const child of Object.values(node.contents)) { if (child.parent !== node) shape('parent'); pending.push(child); }
    records.set(node,{base:regular?node.usedBytes:0,high:0,formatBuffer:false});
  }
  if (baselineBytes > 100663296 + 4096) shape('baseline bytes');
  for (const stream of FS.streams) if (stream && FS.isFile(stream.node.mode)) {
    if (!records.has(stream.node)) shape('open regular stream');
    remember(stream.stream_ops,MEMFS.stream_ops,fileKeys);
  }
  const regular = (node,mutate=false) => { project(node,mutate); if (!FS.isFile(node.mode)) fail('regular node'); bytesOnly(node.contents); if(uint(node.usedBytes)>node.contents.length)fail('file shape'); return records.get(node); };
  const logicalPlan = (record,size) => {
    uint(size); if (size > maximum) fail('file bytes');
    const high = Math.max(record.high,Math.max(0,size-record.base)), delta=high-record.high;
    if (delta > cap-growthBytes) fail('aggregate growth');
    return {record,high,delta};
  };
  const plan = (node,size) => logicalPlan(regular(node,true),size);
  const commit = p => { growthBytes += p.delta; p.record.high=p.high; };
  const capacity = bytes => { uint(bytes); if(bytes>cap-backingBytes-copyBytes)fail('backing/copy allocation work'); };
  const reserve = bytes => { capacity(bytes); backingBytes += bytes; };
  const copy = bytes => { uint(bytes); if(bytes>cap-copyBytes-backingBytes)fail('backing/copy allocation work'); copyBytes += bytes; };
  const expanded = (node,size) => {
    if (node.contents.length >= size) return 0;
    let next = Math.max(size,Math.floor(node.contents.length*(node.contents.length<1048576?2:1.125)));
    if(node.contents.length)next=Math.max(next,256);
    return uint(next);
  };
  const allocating = (fn,...args) => { try { return fn(...args); } catch(error) { failure('allocation/operation failure'); throw error; } };
  const isLog = node => /\.(log|fls|blg)$/i.test(node.name);
  const logPlan = (node,length) => {
    if (!isLog(node)) return;
    if (length > logCap-logBytes) { logTruncated=true; fail('log limit'); }
  };
  const formatNode = node => format && FS.getPath(node)==='/work/output/lualatex.fmt';
  const wrappers = {
    createNode(parent,name,mode,dev) {
      project(parent,true);
      if (!(FS.isFile(mode)||FS.isDir(mode)) || newNodes>=128) fail('new node limit/type');
      if(typeof name!=='string'||!name||name.length>240||name.includes('/')||name.includes('\\')||name==='.'||name==='..')fail('new node name');
      ++newNodes; creating=true;
      try { const node=original['MEMFS.createNode'](parent,name,mode,dev); records.set(node,{base:0,high:0,formatBuffer:false}); return node; }
      finally { creating=false; }
    },
    expandFileStorage(node,size) {
      const p=plan(node,size), bytes=expanded(node,size);
      capacity(bytes); commit(p); reserve(bytes);
      return allocating(original['MEMFS.expandFileStorage'],node,size);
    },
    resizeFileStorage(node,size) {
      const p=plan(node,size), bytes=node.usedBytes===size?0:size;
      capacity(bytes); commit(p); reserve(bytes);
      records.get(node).formatBuffer=false;
      return allocating(original['MEMFS.resizeFileStorage'],node,size);
    },
    setattr(node,attr) {
      project(node,true);
      if(attr.mode!==undefined && (!Number.isSafeInteger(attr.mode)||(attr.mode&61440)!==(node.mode&61440)))fail('node type change');
      if(attr.size!==undefined){const p=plan(node,attr.size);capacity(node.usedBytes===attr.size?0:attr.size);}
      return original['MEMFS.node_ops.setattr'](node,attr);
    },
    write(stream,buffer,offset,length,position,canOwn) {
      const node=stream.node,record=regular(node,true);
      bytesOnly(buffer);uint(offset);uint(length);uint(position);
      if(add(offset,length)>buffer.length)fail('byte range');
      if(!length)return 0;
      logPlan(node,length);
      const own=!!canOwn && buffer.buffer!==HEAP8.buffer,end=add(position,length);
      const direct=own||(!node.usedBytes&&!position), size=direct?length:Math.max(node.usedBytes,end), p=plan(node,size);
      // The genuine ~12MiB format is written in many tiny stdio chunks. Reserve
      // one charged 14MiB backing store rather than spending >100MiB on repeated
      // geometric reallocations. Bytes/format are unchanged; over-cap still fails.
      if(formatNode(node)) {
        const initial=record.formatBuffer?0:Math.max(14680064,end);
        const bytes=initial || expanded(node,end);
        capacity(bytes);commit(p);reserve(bytes);
        if(initial){const previous=node.contents;node.contents=new Uint8Array(initial);node.contents.set(previous.subarray(0,node.usedBytes));record.formatBuffer=true;}
        else if(bytes)original['MEMFS.expandFileStorage'](node,end);
        node.contents.set(buffer.subarray(offset,offset+length),position);
        node.usedBytes=Math.max(node.usedBytes,end);node.mtime=node.ctime=Date.now();
      } else {
        const bytes=own?buffer.buffer.byteLength:direct?length:expanded(node,end);
        capacity(bytes);commit(p);if(direct)reserve(bytes);
        allocating(original['MEMFS.stream_ops.write'],stream,buffer,offset,length,position,canOwn);
      }
      if(isLog(node))logBytes+=length;
      return length;
    },
    llseek(stream,offset,whence) {
      project(stream.node); uint(whence);
      const file=FS.isFile(stream.node.mode),record=records.get(stream.node);
      if(!file&&!FS.isDir(stream.node.mode))fail('seek node type');
      const position=original['MEMFS.stream_ops.llseek'](stream,offset,whence);
      uint(position);if(position>(file?Math.max(record.base,maximum):cap))fail('seek limit');return position;
    },
    mmap(){fail('mmap disabled after seeding');},
    msync(stream,buffer,offset,length,flags){return original['MEMFS.stream_ops.msync'](stream,buffer,offset,length,flags);},
    rename(node,parent,name){project(node,true);project(parent,true);if(node===work)fail('root rename');return original['MEMFS.node_ops.rename'](node,parent,name);},
    unlink(parent,name){project(parent,true);return original['MEMFS.node_ops.unlink'](parent,name);},
    rmdir(parent,name){project(parent,true);return original['MEMFS.node_ops.rmdir'](parent,name);},
    symlink(){fail('symlink disabled after seeding');}
  };
  // Change every existing shared table IN PLACE, not just MEMFS.stream_ops.
  for(const [table,source] of tables)for(const key of Object.keys(table))if((source===MEMFS.stream_ops||source===MEMFS.node_ops)&&wrappers[key])table[key]=wrappers[key];
  MEMFS.createNode=wrappers.createNode;MEMFS.expandFileStorage=wrappers.expandFileStorage;MEMFS.resizeFileStorage=wrappers.resizeFileStorage;
  SYSCALLS.getStr=ptr=>{
    uint(ptr);if(ptr>=HEAPU8.length)fail('path pointer');
    let size=0;
    while(size<=1024&&ptr+size<HEAPU8.length&&HEAPU8[ptr+size])++size;
    if(size>1024||ptr+size>=HEAPU8.length)fail('syscall path length');
    return original['SYSCALLS.getStr'](ptr);
  };
  const resolvePath = (path,mutate=false) => {
    if(typeof path!=='string'||path.length>1024)fail('path type/length');
    const absolute=PATH_FS.resolve(path);
    if(absolute!=='/work'&&!absolute.startsWith('/work/')) {
      if(mutate)fail('write outside /work');
      // Legitimate font discovery probes absent OS configuration. No host lookup
      // occurs: return ENOENT, without poisoning an otherwise valid compilation.
      deniedReads=Math.min(1000000,deniedReads+1);throw new FS.ErrnoError(44);
    }
    return absolute;
  };
  FS.createNode=(...args)=>{if(!creating)fail('non-MEMFS node');return original['FS.createNode'](...args);};
  FS.createStream=(stream,fd)=>{if(FS.isFile(stream.node.mode)||FS.isDir(stream.node.mode))project(stream.node);else if(!FS.streams.some(s=>s&&s.node===stream.node))fail('new device stream');return original['FS.createStream'](stream,fd);};
  FS.mount=FS.unmount=()=>fail('mounts disabled after seeding');
  FS.open=(path,flags,...args)=>{
    const bits=FS_modeStringToFlags(flags);
    const mutate=!!(bits&(3|64|512|1024));
    return original['FS.open'](resolvePath(path,mutate),flags,...args);
  };
  FS.read=(stream,...args)=>{if(FS.isFile(stream.node.mode))regular(stream.node);else if(stream.fd===0)return 0;else fail('non-file read');return original['FS.read'](stream,...args);};
  FS.write=(stream,buffer,offset,length,...args)=>{
    bytesOnly(buffer);uint(offset);uint(length);if(add(offset,length)>buffer.length)fail('byte range');
    if(!FS.isFile(stream.node.mode)){
      if(![1,2].includes(stream.fd)||!FS.isChrdev(stream.node.mode))fail('device output');
      if(failed)fail('latched');
      if(length>logCap-logBytes){logTruncated=true;fail('stdout/stderr limit');}logBytes+=length;
    }
    return original['FS.write'](stream,buffer,offset,length,...args);
  };
  FS.readFile=(path,options={})=>{
    if(options.encoding!==undefined&&options.encoding!=='binary'||options.flags!==undefined&&options.flags!==0)fail('binary read only');
    const node=FS.lookupPath(resolvePath(path),{follow:true}).node;regular(node);copy(node.usedBytes);
    return allocating(original['FS.readFile'],path,options);
  };
  const prepareWrite=(path,data,options={})=>{
    bytesOnly(data);path=resolvePath(path,true);
    let flags=options.flags??577;if(typeof flags==='string')flags=({r:0,'r+':2,w:577,'w+':578,a:1089,'a+':1090})[flags];uint(flags);
    const node=FS.lookupPath(path,{follow:!(flags&131072),noent_okay:true}).node;
    if(node)regular(node,true);else{project(FS.lookupPath(path,{parent:true}).node,true);if(newNodes>=128)fail('new node limit');}
    const used=node&&!(flags&512)?node.usedBytes:0,position=flags&1024?used:0,length=data.length;
    const own=!!options.canOwn&&data.buffer!==HEAP8.buffer,direct=own||(!used&&!position),size=direct?length:Math.max(used,add(position,length));
    if(node)plan(node,size);else logicalPlan({base:0,high:0},size);
    capacity(!length?0:own?data.buffer.byteLength:direct?length:expanded(node,add(position,length)));
  };
  FS.writeFile=(path,data,options)=>{prepareWrite(path,data,options);return original['FS.writeFile'](path,data,options);};
  FS.createDataFile=(parent,name,data,...rest)=>{let path=name;if(parent){const base=typeof parent==='string'?parent:FS.getPath(parent);path=name?PATH.join2(base,name):base;}if(data)prepareWrite(path,data,{canOwn:rest[2]});return original['FS.createDataFile'](parent,name,data,...rest);};
  FS.createLazyFile=()=>fail('lazy files disabled after seeding');
  doWritev=(stream,iov,count,offset)=>{
    uint(iov);uint(count);if(count>1024||iov%4||add(iov,8*count)>HEAPU8.byteLength)fail('vector table');
    let total=0;
    for(let i=0;i<count;i++){const at=iov/4+2*i,ptr=HEAPU32[at],length=HEAPU32[at+1];if(add(ptr,length)>HEAPU8.length)fail('vector bytes');total=add(total,length);}
    ++vectorCalls;largestVectorBytes=Math.max(largestVectorBytes,total);
    let backingPlan=0;
    if(FS.isFile(stream.node.mode)){
      const node=stream.node;regular(node,true);
      const position=offset===undefined?(stream.seekable&&(stream.flags&1024)?node.usedBytes:stream.position):offset;uint(position);
      const direct=!node.usedBytes&&!position,end=add(position,total);
      plan(node,direct?total:Math.max(node.usedBytes,end));logPlan(node,total);
      backingPlan=formatNode(node)?(records.get(node).formatBuffer?expanded(node,end):Math.max(14680064,end)):direct?total:expanded(node,end);
      capacity(backingPlan);
    }else if(![1,2].includes(stream.fd)||total>logCap-logBytes){logTruncated=true;fail('stdout/stderr vector limit');}
    if(count!==1){
      // The pinned SDK allocates its gather buffer BEFORE reaching FS.write.
      // Preflight both that staging buffer and the eventual MEMFS allocation,
      // then charge the staging work before invoking the unchanged SDK routine.
      capacity(add(backingPlan,total));copy(total);vectorStageBytes=add(vectorStageBytes,total);
    }
    return original.doWritev(stream,iov,count,offset);
  };
  const boundedTransport=(fn,text)=>{
    text=String(text);let count=0,end=0;
    for(const character of text){const cp=character.codePointAt(0),bytes=cp<128?1:cp<2048?2:cp<65536?3:4;if(count+bytes+1>logCap-transportBytes)break;count+=bytes;end+=character.length;}
    if(end!==text.length){logTruncated=true;failure('log transport limit');if(end)fn(text.slice(0,end));transportBytes+=count;return;}
    transportBytes+=count+1;fn(text);
  };
  const originalOut=out,originalErr=err;
  out=text=>boundedTransport(originalOut,text);err=text=>boundedTransport(originalErr,text);
  ocVfsBudget={get failed(){return failed;},snapshot:()=>({installed:true,format,maximum,cap,logCap,baselineBytes,baselineCapacity,growthBytes,backingBytes,copyBytes,allocationWork:backingBytes+copyBytes,newNodes,logBytes,transportBytes,logTruncated,deniedReads,vectorCalls,vectorStageBytes,largestVectorBytes,failed,reason})};
  return true;
}
