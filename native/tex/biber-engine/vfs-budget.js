// Minimum WASM filesystem glue. C++ owns the ABI, paths and operation lifecycle.
// The pinned SDK6 loader supplies MEMFS and readonly LZ4 bootstrap nodes. Keep
// references before inputs are seeded, then guard the lower shared operation
// tables as well as FS and writev: libc/XS callers must cross the same boundary.
let ocBiberVfsBudget = null;
let ocBiberVfsRefs = null;
function ocBiberVfsSurface() {
  const refs = {};
  for (const name of ['createNode','createStream','open','read','write','readFile','writeFile','createDataFile','lookupPath','mount','unmount','createLazyFile']) refs['FS.'+name]=FS[name];
  for (const name of ['createNode','expandFileStorage','resizeFileStorage']) refs['MEMFS.'+name]=MEMFS[name];
  for (const kind of ['node_ops','stream_ops']) for(const [name,value] of Object.entries(MEMFS[kind])) refs['MEMFS.'+kind+'.'+name]=value;
  for (const kind of ['node_ops','stream_ops']) for(const [name,value] of Object.entries(LZ4[kind])) refs['LZ4.'+kind+'.'+name]=value;
  refs.doWritev=doWritev; refs.getStr=SYSCALLS.getStr; refs.modeFlags=FS_modeStringToFlags;
  return refs;
}
Module['preRun'] ||= [];
Module['preRun'].push(()=>{ ocBiberVfsRefs=ocBiberVfsSurface(); });
Module['ocBiberVfsSnapshot']=()=>ocBiberVfsBudget?ocBiberVfsBudget.snapshot():{installed:false};
function ocInstallBiberVfsBudget(logCap,outputCap,jobStem) {
  const shape=()=>{throw Error('unsupported Biber SDK filesystem');};
  if(!ocBiberVfsRefs||ocBiberVfsBudget||!MEMFS.ops_table||FS.allocate!==undefined||!Array.isArray(FS.streams))shape();
  const original=ocBiberVfsRefs,current=ocBiberVfsSurface();
  for(const name of Object.keys(original))if(typeof original[name]!=='function'||current[name]!==original[name])shape();
  const cacheCap=8388608,allocationCap=33554432,records=new Map(),tables=new Map();
  const outputFiles=new Set(['/work/output/'+jobStem+'.bbl','/work/output/'+jobStem+'.blg']);
  let failed=false,reason='',growthBytes=0,outputBytes=0,cacheBytes=0,logBytes=0,backingBytes=0,copyBytes=0,newNodes=0,vectorCalls=0,vectorStageBytes=0,largestVectorBytes=0,deniedReads=0;
  const failure=why=>{failed=true;if(!reason)reason=why;};
  const fail=why=>{failure(why);throw new FS.ErrnoError(51);};
  const uint=n=>{if(!Number.isSafeInteger(n)||n<0)fail('integer');return n;};
  const add=(a,b)=>{uint(a);uint(b);if(b>Number.MAX_SAFE_INTEGER-a)fail('integer');return a+b;};
  const bytes=b=>{if(!(b instanceof Uint8Array||b instanceof Int8Array))fail('byte input');};
  const category=path=>path==='/work/output/terminal.log'?'log':outputFiles.has(path)?'output':path==='/work/cache/stdin'?'input':path.startsWith('/work/cache/')?'cache':'input';
  const mutablePath=path=>{
    if(path==='/work/cache'||path==='/work/output')return true;
    const kind=category(path);return kind==='output'||kind==='log'||kind==='cache';
  };
  const nodePath=node=>{if(!records.has(node))fail('unregistered node');return FS.getPath(node);};
  const nodeCheck=(node,mutate=false)=>{
    const path=nodePath(node);
    if(path!=='/work'&&!path.startsWith('/work/'))fail('node boundary');
    if(mutate&&(failed||!mutablePath(path)))fail(failed?'latched':'readonly input');
    return records.get(node);
  };
  const remember=(table,source,keys)=>{
    if(!table||Object.keys(table).length!==keys.length)shape();
    for(const key of keys)if(table[key]!==source[key]||!Object.getOwnPropertyDescriptor(table,key)?.writable)shape();
    tables.set(table,source);
  };
  const ops=MEMFS.ops_table,fileKeys=['llseek','read','write','mmap','msync'];
  remember(MEMFS.node_ops,MEMFS.node_ops,Object.keys(MEMFS.node_ops));
  remember(MEMFS.stream_ops,MEMFS.stream_ops,fileKeys);
  for(const kind of ['dir','file','link','chrdev'])remember(ops[kind].node,MEMFS.node_ops,Object.keys(ops[kind].node));
  remember(ops.file.stream,MEMFS.stream_ops,fileKeys);remember(ops.dir.stream,MEMFS.stream_ops,['llseek']);
  let baselineBytes=0,runtimeBytes=0,creating=false;
  const pending=[FS.lookupPath('/work').node];
  while(pending.length){
    const node=pending.pop(),path=FS.getPath(node),regular=FS.isFile(node.mode);
    if(records.has(node)||records.size>=16384||node.mounted||!(regular||FS.isDir(node.mode)))shape();
    const lz4=regular&&node.node_ops===LZ4.node_ops;
    if(lz4){
      if(!path.startsWith('/work/runtime/')||node.stream_ops!==LZ4.stream_ops||!node.contents?.compressedData||uint(node.size)>8388608)shape();
      runtimeBytes+=node.size;
    }else{
      const kind=regular?'file':'dir';remember(node.node_ops,MEMFS.node_ops,Object.keys(ops[kind].node));
      if(regular){bytes(node.contents);if(uint(node.usedBytes)>node.contents.length)shape();remember(node.stream_ops,MEMFS.stream_ops,fileKeys);baselineBytes+=node.usedBytes;}
      else for(const child of Object.values(node.contents)){if(child.parent!==node)shape();pending.push(child);}
    }
    records.set(node,{kind:category(path),high:0,lz4});
  }
  if(baselineBytes>8388608||runtimeBytes>67108864)shape();
  for(const stream of FS.streams)if(stream){if(!records.has(stream.node)||!FS.isFile(stream.node.mode))shape();if(!records.get(stream.node).lz4)remember(stream.stream_ops,MEMFS.stream_ops,fileKeys);}
  const regular=(node,mutate=false)=>{const r=nodeCheck(node,mutate);if(!FS.isFile(node.mode))fail('regular node');if(!r.lz4){bytes(node.contents);if(uint(node.usedBytes)>node.contents.length)fail('file shape');}else if(mutate)fail('readonly bootstrap');return r;};
  const plan=(node,size)=>{
    const record=regular(node,true);uint(size);
    const high=Math.max(record.high,size),delta=high-record.high,kind=record.kind;
    if(kind==='log'){if(size>logCap)fail('terminal size');}
    else if(kind==='output'){if(delta>outputCap-outputBytes)fail('output limit');}
    else if(kind==='cache'){if(delta>cacheCap-cacheBytes)fail('cache limit');}
    else fail('readonly input');
    return{record,high,delta,kind};
  };
  const commit=p=>{p.record.high=p.high;growthBytes+=p.delta;if(p.kind==='output')outputBytes+=p.delta;if(p.kind==='cache')cacheBytes+=p.delta;};
  const capacity=n=>{uint(n);if(n>allocationCap-backingBytes-copyBytes)fail('allocation work');};
  const reserve=n=>{capacity(n);backingBytes+=n;};
  const copy=n=>{capacity(n);copyBytes+=n;};
  const expanded=(node,size)=>{if(node.contents.length>=size)return 0;let n=Math.max(size,Math.floor(node.contents.length*(node.contents.length<1048576?2:1.125)));if(node.contents.length)n=Math.max(n,256);return uint(n);};
  const logPlan=(node,length)=>{if(category(nodePath(node))==='log'&&length>logCap-logBytes)fail('terminal limit');};
  const allocating=(fn,...args)=>{try{return fn(...args);}catch(e){failure('allocation/operation failure');throw e;}};
  const wrappers={
    createNode(parent,name,mode,dev){
      nodeCheck(parent,true);if(!(FS.isFile(mode)||FS.isDir(mode))||newNodes>=128||typeof name!=='string'||!name||name.includes('/')||name.includes('\\')||name==='.'||name==='..')fail('new node');
      const target=FS.getPath(parent)+'/'+name;if(!mutablePath(target)||FS.isDir(mode)&&!target.startsWith('/work/cache/'))fail('new node path');
      ++newNodes;creating=true;try{const node=original['MEMFS.createNode'](parent,name,mode,dev);records.set(node,{kind:category(target),high:0,lz4:false});return node;}finally{creating=false;}
    },
    expandFileStorage(node,size){const p=plan(node,size),n=expanded(node,size);capacity(n);commit(p);reserve(n);return allocating(original['MEMFS.expandFileStorage'],node,size);},
    resizeFileStorage(node,size){const p=plan(node,size),n=node.usedBytes===size?0:size;capacity(n);commit(p);reserve(n);return allocating(original['MEMFS.resizeFileStorage'],node,size);},
    setattr(node,attr){nodeCheck(node,true);if(attr.mode!==undefined&&(!Number.isSafeInteger(attr.mode)||(attr.mode&61440)!==(node.mode&61440)))fail('node type');if(attr.size!==undefined){plan(node,attr.size);capacity(node.usedBytes===attr.size?0:attr.size);}return original['MEMFS.node_ops.setattr'](node,attr);},
    write(stream,buffer,offset,length,position,canOwn){
      const node=stream.node;regular(node,true);bytes(buffer);uint(offset);uint(length);uint(position);if(add(offset,length)>buffer.length)fail('byte range');if(!length)return 0;
      logPlan(node,length);const own=!!canOwn&&buffer.buffer!==HEAP8.buffer,direct=own||(!node.usedBytes&&!position),end=add(position,length),p=plan(node,direct?length:Math.max(node.usedBytes,end));
      const n=own?buffer.buffer.byteLength:direct?length:expanded(node,end);capacity(n);commit(p);if(direct)reserve(n);
      const written=allocating(original['MEMFS.stream_ops.write'],stream,buffer,offset,length,position,canOwn);if(category(nodePath(node))==='log')logBytes+=written;return written;
    },
    llseek(stream,offset,whence){const record=nodeCheck(stream.node),position=original['MEMFS.stream_ops.llseek'](stream,offset,whence);uint(position);if(position>Math.max(stream.node.usedBytes||0,record.kind==='log'?logCap:8388608))fail('seek limit');return position;},
    mmap(){fail('mmap disabled');},msync(){fail('msync disabled');},
    rename(){fail('rename disabled');},symlink(){fail('symlink disabled');},
    // Genuine Biber removes an incomplete BBL on malformed input. Exact owned
    // BBL/BLG deletion is permitted; cumulative growth remains charged so that
    // delete/recreate cannot recycle the output budget. Keep terminal/input data.
    unlink(parent,name){const node=FS.lookupNode(parent,name),kind=category(nodePath(node));if(kind!=='cache'&&kind!=='output')fail('unlink outside cache/output');nodeCheck(parent,true);return original['MEMFS.node_ops.unlink'](parent,name);},
    rmdir(parent,name){const node=FS.lookupNode(parent,name);if(!nodePath(node).startsWith('/work/cache/'))fail('rmdir outside cache');nodeCheck(parent,true);return original['MEMFS.node_ops.rmdir'](parent,name);}
  };
  for(const[table]of tables)for(const key of Object.keys(table))if(wrappers[key])table[key]=wrappers[key];
  for(const key of ['createNode','expandFileStorage','resizeFileStorage'])MEMFS[key]=wrappers[key];
  // All bootstrap aliases share these tables, so even lower direct XS reads
  // remain bounded to the authenticated readonly runtime tree.
  LZ4.stream_ops.read=(stream,...args)=>{regular(stream.node);return original['LZ4.stream_ops.read'](stream,...args);};
  LZ4.stream_ops.write=()=>fail('readonly bootstrap');LZ4.node_ops.setattr=()=>fail('readonly bootstrap');
  const resolve=(path,mutate=false)=>{
    if(typeof path!=='string'||path.length>1024)fail('path length/type');const absolute=PATH_FS.resolve(path);
    if(absolute!=='/work'&&!absolute.startsWith('/work/')){if(mutate)fail('write boundary');deniedReads++;throw new FS.ErrnoError(44);}
    if(mutate&&!mutablePath(absolute))fail('readonly path');return absolute;
  };
  FS.createNode=(...args)=>{if(!creating)fail('non-MEMFS node');return original['FS.createNode'](...args);};
  FS.createStream=(stream,fd)=>{nodeCheck(stream.node);if(!(FS.isFile(stream.node.mode)||FS.isDir(stream.node.mode)))fail('stream type');return original['FS.createStream'](stream,fd);};
  FS.mount=FS.unmount=FS.createLazyFile=()=>fail('mount/lazy file disabled');
  FS.open=(path,flags,...args)=>{const bits=original.modeFlags(flags);return original['FS.open'](resolve(path,!!(bits&(3|64|512|1024))),flags,...args);};
  FS.read=(stream,...args)=>{regular(stream.node);return original['FS.read'](stream,...args);};
  FS.write=(stream,buffer,offset,length,...args)=>{regular(stream.node,true);return original['FS.write'](stream,buffer,offset,length,...args);};
  FS.readFile=(path,options={})=>{if(options.encoding!==undefined&&options.encoding!=='binary'||options.flags!==undefined&&options.flags!==0)fail('binary read only');const node=FS.lookupPath(resolve(path),{follow:true}).node;regular(node);copy(node.size??node.usedBytes);return allocating(original['FS.readFile'],path,options);};
  // C++ captures inputs before installation. Runtime bulk JS writes are not
  // needed by Perl and bypassing libc must not introduce an uncharged copy.
  FS.writeFile=FS.createDataFile=()=>fail('bulk write after seeding');
  SYSCALLS.getStr=ptr=>{uint(ptr);if(ptr>=HEAPU8.length)fail('path pointer');let n=0;while(n<=1024&&ptr+n<HEAPU8.length&&HEAPU8[ptr+n])n++;if(n>1024||ptr+n>=HEAPU8.length)fail('syscall path length');return original.getStr(ptr);};
  doWritev=(stream,iov,count,offset)=>{
    uint(iov);uint(count);if(count>1024||iov%4||add(iov,8*count)>HEAPU8.length)fail('vector table');let total=0;
    for(let i=0;i<count;i++){const at=iov/4+i*2,ptr=HEAPU32[at],length=HEAPU32[at+1];if(add(ptr,length)>HEAPU8.length)fail('vector bytes');total=add(total,length);}
    ++vectorCalls;largestVectorBytes=Math.max(largestVectorBytes,total);regular(stream.node,true);
    const node=stream.node,position=offset===undefined?(stream.seekable&&(stream.flags&1024)?node.usedBytes:stream.position):offset;uint(position);const end=add(position,total),direct=!node.usedBytes&&!position;
    plan(node,direct?total:Math.max(node.usedBytes,end));logPlan(node,total);const backing=direct?total:expanded(node,end);capacity(backing);
    if(count!==1){capacity(add(backing,total));copy(total);vectorStageBytes+=total;}
    return original.doWritev(stream,iov,count,offset);
  };
  return{get failed(){return failed;},snapshot:()=>({installed:true,failed,reason,baselineBytes,runtimeBytes,outputBytes,cacheBytes,logBytes,growthBytes,backingBytes,copyBytes,allocationWork:backingBytes+copyBytes,allocationCap,newNodes,vectorCalls,vectorStageBytes,largestVectorBytes,deniedReads})};
}
