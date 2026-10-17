// Minimal IndexedDB transport only. C# owns JSON/entry policy and revisions.
const DB='overclone-compile-history-index-v1', STORE='projects';
const id=value=>typeof value==='string'&&/^[0-9a-f]{32}$/.test(value)&&value!=='0'.repeat(32);
const revision=value=>typeof value==='string'&&/^(0|[1-9][0-9]{0,18})$/.test(value);
const limit=value=>Number.isInteger(value)&&value>0&&value<=65536;
const bounded=(value,max)=>typeof value==='string'&&value.length<=max&&new TextEncoder().encode(value).length<=max;
const errorStatus=error=>error?.name==='VersionError'?'schema':error?.name==='NotFoundError'?'corrupt':error?.name==='QuotaExceededError'?'quota':['SecurityError','NotAllowedError','PermissionDeniedError'].includes(error?.name)?'permission':'failure';
export function createHistoryIndexStorage(){
 let opening=null,closed=false;
 const open=()=>{
  if(closed)return Promise.reject(new Error('Closed index port'));
  if(!globalThis.indexedDB)return Promise.reject(Object.assign(new Error('Unavailable'),{indexStatus:'unavailable'}));
  return opening??=(new Promise((resolve,reject)=>{
   const request=indexedDB.open(DB,1);
   request.onupgradeneeded=()=>request.result.createObjectStore(STORE);
   request.onsuccess=()=>resolve(request.result);request.onerror=()=>reject(request.error);
  })).catch(error=>{opening=null;throw error;});
 };
 const transaction=async(mode,action)=>{
  let database;try{database=await open();}catch(error){return{status:error?.indexStatus||errorStatus(error)};}
  return new Promise(resolve=>{
   let tx,value={status:'failure'},failure;
   try{tx=database.transaction(STORE,mode);}catch(error){resolve({status:errorStatus(error)});return;}
   tx.oncomplete=()=>resolve(value);
   tx.onerror=()=>{failure??=tx.error;};
   tx.onabort=()=>resolve({status:errorStatus(failure||tx.error)});
   const abort=error=>{failure=error;try{tx.abort();}catch{resolve({status:errorStatus(error)});}};
   try{action(tx.objectStore(STORE),result=>{value=result;},abort);}catch(error){abort(error);}
  });
 };
 const validRow=(row,max)=>row&&typeof row==='object'&&Object.keys(row).length===2&&Object.hasOwn(row,'revision')&&Object.hasOwn(row,'json')&&revision(row.revision)&&row.revision!=='0'&&bounded(row.json,max);
 const read=(project,max)=>{
  if(!id(project)||!limit(max))return Promise.resolve({status:'corrupt'});
  return transaction('readonly',(store,done,abort)=>{
   const request=store.get(project);request.onerror=()=>abort(request.error);
   request.onsuccess=()=>{const row=request.result;if(row===undefined){done({status:'ok',revision:'0',json:null});return;}done(validRow(row,max)?{status:'ok',revision:row.revision,json:row.json}:{status:'corrupt'});};
  });
 };
 const compareExchange=(project,expected,next,json,max)=>{
  if(!id(project)||!limit(max)||!revision(expected)||!revision(next)||next==='0'||!bounded(json,max))return Promise.resolve({status:'corrupt'});
  return transaction('readwrite',(store,done,abort)=>{
   const get=store.get(project);get.onerror=()=>abort(get.error);
   get.onsuccess=()=>{
    const row=get.result;if(row!==undefined&&!validRow(row,max)){done({status:'corrupt'});return;}
    const current=row===undefined?'0':row.revision;if(current!==expected){done({status:'conflict'});return;}
    try{const put=store.put({revision:next,json},project);put.onerror=()=>abort(put.error);put.onsuccess=()=>done({status:'saved'});}catch(error){abort(error);}
   };
  });
 };
 const dispose=async()=>{closed=true;if(opening){try{(await opening).close();}catch{ /* Failed open owns no DB connection. */ }}};
 return{read,compareExchange,dispose};
}
