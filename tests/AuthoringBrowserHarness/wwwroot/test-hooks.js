// TEST-only actual storage fault, never product runtime acquisition or policy.
export async function damageRuntime(id,index){
 if(!/^[0-9a-f]{32}$/.test(id)||!Number.isInteger(index)||index<0||index>=378)throw new Error('TEST runtime location');
 const root=await(await navigator.storage.getDirectory()).getDirectoryHandle('overclone-runtime-assets-v1');
 const directory=await root.getDirectoryHandle(id),handle=await directory.getFileHandle(String(index));
 const bytes=new Uint8Array(await(await handle.getFile()).arrayBuffer());
 if(bytes.length<1||bytes.length>8388608)throw new Error('TEST fault size');
 bytes[0]^=1;const writer=await handle.createWritable();await writer.write(bytes);await writer.close();
}
