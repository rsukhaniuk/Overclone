// Drawing/element lifetime only. C# owns document, native inspection, search,
// geometry, BGRA conversion and selection. No PDF parsing or runtime loading.
export function create(canvas) {
 if (!(canvas instanceof HTMLCanvasElement)) throw new Error('Canvas required');
 let generation=0,disposed=false;
 const context=canvas.getContext('2d',{willReadFrequently:true});
 if(!context)throw new Error('Canvas2D unavailable');
 const clear=()=>{context.clearRect(0,0,canvas.width,canvas.height);delete canvas.dataset.pdfHash;delete canvas.dataset.pdfPage;delete canvas.dataset.drawGeneration;};
 return {
  invalidate(value){
   if(disposed)return;
   if(!Number.isSafeInteger(value)||value<0)throw new Error('Drawing generation');
   if(value>generation){generation=value;clear();}
  },
  measure(){return Math.max(64,Math.min(1600,Math.floor(canvas.parentElement?.parentElement?.clientWidth||360)));},
  draw(value,hash,page,width,height,rgba){
   if(disposed||value!==generation)return false;
   if(!/^[0-9a-f]{64}$/.test(hash)||!Number.isInteger(page)||page<0||page>=32||!Number.isInteger(width)||!Number.isInteger(height)||width<1||height<1||width*height>4000000)throw new Error('Drawing bounds');
   const bytes=typeof rgba==='string'?Uint8Array.from(atob(rgba),c=>c.charCodeAt(0)):rgba;
   if(!(bytes instanceof Uint8Array)||bytes.byteLength!==width*height*4)throw new Error('RGBA shape');
   // Synchronous paint has no continuation capable of bypassing invalidate.
   canvas.width=width;canvas.height=height;
   context.putImageData(new ImageData(new Uint8ClampedArray(bytes),width,height),0,0);
   canvas.dataset.pdfHash=hash;canvas.dataset.pdfPage=String(page);canvas.dataset.drawGeneration=String(value);
   return true;
  },
  dispose(){if(disposed)return;clear();disposed=true;canvas.width=0;canvas.height=0;}
 };
}
