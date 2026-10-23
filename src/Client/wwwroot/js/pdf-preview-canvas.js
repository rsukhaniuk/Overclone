// Drawing/element lifetime only. C# owns document, native inspection, search,
// geometry, BGRA conversion and selection. No PDF parsing or runtime loading.
const MAX_PAGE=512; // matches PdfInspectionBudget.MaxDocumentPages / PdfPreviewPolicy.MaxDocumentPages
export function create(canvas, pointer = null) {
 if (!(canvas instanceof HTMLCanvasElement)) throw new Error('Canvas required');
 let generation=0,disposed=false;
 const context=canvas.getContext('2d',{willReadFrequently:true});
 if(!context)throw new Error('Canvas2D unavailable');
 const clear=()=>{context.clearRect(0,0,canvas.width,canvas.height);delete canvas.dataset.pdfHash;delete canvas.dataset.pdfPage;delete canvas.dataset.drawGeneration;};
 // Browser event/geometry glue only. Capture the identity of the pixels the
 // user actually clicked before a queued C# event can observe another frame.
 const clicked=e=>{
  if(disposed||e.button!==0||canvas.dataset.drawGeneration!==String(generation))return;
  const hash=canvas.dataset.pdfHash,page=Number(canvas.dataset.pdfPage),r=canvas.getBoundingClientRect();
  const x=(e.clientX-r.left)/r.width,y=(e.clientY-r.top)/r.height;
  if(!/^[0-9a-f]{64}$/.test(hash||'')||!Number.isInteger(page)||page<0||page>=MAX_PAGE
   ||!Number.isFinite(x+y)||x<0||x>1||y<0||y>1)return;
  pointer.invokeMethodAsync('OnPointer',generation,hash,page,x,y,e.detail>=2).catch(()=>{});
 };
 if(pointer)canvas.addEventListener('click',clicked);
 return {
  invalidate(value){
   if(disposed)return;
   if(!Number.isSafeInteger(value)||value<0)throw new Error('Drawing generation');
   if(value>generation){generation=value;clear();}
  },
  measure(){return Math.max(64,Math.min(1600,Math.floor(canvas.parentElement?.parentElement?.clientWidth||360)));},
  draw(value,hash,page,width,height,rgba){
   if(disposed||value!==generation)return false;
   if(!/^[0-9a-f]{64}$/.test(hash)||!Number.isInteger(page)||page<0||page>=MAX_PAGE||!Number.isInteger(width)||!Number.isInteger(height)||width<1||height<1||width*height>4000000)throw new Error('Drawing bounds');
   const bytes=typeof rgba==='string'?Uint8Array.from(atob(rgba),c=>c.charCodeAt(0)):rgba;
   if(!(bytes instanceof Uint8Array)||bytes.byteLength!==width*height*4)throw new Error('RGBA shape');
   // Synchronous paint has no continuation capable of bypassing invalidate.
   canvas.width=width;canvas.height=height;
   context.putImageData(new ImageData(new Uint8ClampedArray(bytes),width,height),0,0);
   canvas.dataset.pdfHash=hash;canvas.dataset.pdfPage=String(page);canvas.dataset.drawGeneration=String(value);
   return true;
  },
  dispose(){if(disposed)return;clear();disposed=true;if(pointer)canvas.removeEventListener('click',clicked);canvas.width=0;canvas.height=0;}
 };
}
