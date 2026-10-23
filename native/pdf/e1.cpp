#include "e1.h"
#include <fpdfview.h>
#include <fpdf_text.h>
#include <fpdf_transformpage.h>
#include <fpdf_edit.h>
#include <emscripten/emscripten.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <string>
#include <vector>
#define API extern "C" EMSCRIPTEN_KEEPALIVE
namespace {
constexpr uint32_t INPUT=1,OUTPUT=2,DOC=3,PAGE=4,SLOTS=64;
constexpr uint32_t MAX_BYTES=8*1024*1024,MAX_OUTPUT=16*1024*1024;
constexpr uint64_t TOTAL_BYTES=64*1024*1024;
struct Slot {
 uint32_t generation=1,kind=0,parent=0,count=0;
 std::vector<uint8_t> bytes;
 FPDF_DOCUMENT doc=nullptr;FPDF_PAGE page=nullptr;FPDF_TEXTPAGE text=nullptr;
 E1Budget budget{};
};
std::array<Slot,SLOTS> slots;
uint32_t status=0,result=0,native_error=0;bool initialized=false;
void start(){status=E1_OK;result=0;native_error=0;}
uint32_t fail(uint32_t code){status=code;result=0;return code;}
uint32_t live(){uint32_t n=0;for(auto& s:slots)n+=s.kind!=0;return n;}
uint64_t allocated(){uint64_t n=0;for(auto& s:slots)n+=s.bytes.size();return n;}
uint32_t handle(const Slot& s){return (s.generation<<8)|static_cast<uint32_t>(&s-slots.data()+1);}
Slot* get(uint32_t h,uint32_t kind){
 uint32_t i=h&255;if(!i||i>SLOTS)return nullptr;auto& s=slots[i-1];
 return s.kind==kind&&s.generation==(h>>8)?&s:nullptr;
}
Slot* fresh(uint32_t kind,uint32_t limit){
 if(live()>=limit){fail(E1_LIMIT);return nullptr;}
 for(auto& s:slots)if(!s.kind&&s.generation<0xffffff){s.kind=kind;return &s;}
 fail(E1_LIMIT);return nullptr;
}
void dispose(Slot& s){
 if(s.text)FPDFText_ClosePage(s.text);
 if(s.page)FPDF_ClosePage(s.page);
 if(s.doc)FPDF_CloseDocument(s.doc);
 s.text=nullptr;s.page=nullptr;s.doc=nullptr;
 std::vector<uint8_t>().swap(s.bytes);s.kind=0;s.parent=0;s.count=0;s.budget={};++s.generation;
}
bool valid_budget(const E1Budget& b){
 return b.version==1&&b.struct_bytes==sizeof(E1Budget)&&b.input_bytes>0&&b.input_bytes<=MAX_BYTES&&
 b.pages>0&&b.pages<=512&&b.text_units>0&&b.text_units<=100000&&b.search_hits>0&&b.search_hits<=256&&
 b.raster_pixels>0&&b.raster_pixels<=4000000&&b.output_bytes>0&&b.output_bytes<=MAX_OUTPUT&&
 b.live_handles>0&&b.live_handles<=SLOTS;
}
Slot* page_slot(uint32_t h){auto* p=get(h,PAGE);if(!p)fail(E1_HANDLE);return p;}
uint32_t output(Slot& p,const void* data,size_t size){
 if(size>p.budget.output_bytes||allocated()+size>TOTAL_BYTES)return fail(E1_LIMIT);
 auto* s=fresh(OUTPUT,p.budget.live_handles);if(!s)return status;
 auto* b=static_cast<const uint8_t*>(data);s->bytes.assign(b,b+size);result=handle(*s);return E1_OK;
}
uint32_t json(Slot& p,const std::string& value){return output(p,value.data(),value.size());}
std::string num(double v){char b[64];if(!std::isfinite(v))return "null";std::snprintf(b,sizeof b,"%.17g",v);return b;}
std::string arr(double a,double b,double c,double d){return "["+num(a)+","+num(b)+","+num(c)+","+num(d)+"]";}
std::string quote(const std::string& s){
 std::string out="\"";for(unsigned char c:s){if(c=='"'||c=='\\'){out+='\\';out+=char(c);}else if(c<32){char b[7];std::snprintf(b,sizeof b,"\\u%04x",c);out+=b;}else out+=char(c);}return out+'"';
}
void utf8(std::string& s,uint32_t c){
 if(c<0x80)s+=char(c);else if(c<0x800){s+=char(0xc0|(c>>6));s+=char(0x80|(c&63));}
 else if(c<0x10000){s+=char(0xe0|(c>>12));s+=char(0x80|((c>>6)&63));s+=char(0x80|(c&63));}
 else{s+=char(0xf0|(c>>18));s+=char(0x80|((c>>12)&63));s+=char(0x80|((c>>6)&63));s+=char(0x80|(c&63));}
}
struct Geometry { double ml,mb,mr,mt,l,b,r,t,w,h;int rot; };
bool geometry(Slot& p,Geometry& g){
 float ml=0,mb=0,mr=0,mt=0,l=0,b=0,r=0,t=0;
 if(!FPDFPage_GetMediaBox(p.page,&ml,&mb,&mr,&mt)){fail(E1_UNSUPPORTED);return false;}
 // An absent CropBox has PDF's MediaBox default, recorded explicitly as default.
 if(!FPDFPage_GetCropBox(p.page,&l,&b,&r,&t)){l=ml;b=mb;r=mr;t=mt;}
 for(float v:{ml,mb,mr,mt,l,b,r,t})if(!std::isfinite(v)){fail(E1_INDETERMINATE);return false;}
 g={ml,mb,mr,mt,std::max(l,ml),std::max(b,mb),std::min(r,mr),std::min(t,mt),0,0,FPDFPage_GetRotation(p.page)};
 if(g.rot<0||g.rot>3||!(g.r>g.l&&g.t>g.b)){fail(E1_UNSUPPORTED);return false;}
 g.w=(g.rot%2)?g.t-g.b:g.r-g.l;g.h=(g.rot%2)?g.r-g.l:g.t-g.b;
 if(!std::isfinite(g.w)||!std::isfinite(g.h)){fail(E1_INDETERMINATE);return false;}
 if(std::abs(FPDF_GetPageWidthF(p.page)-g.w)>0.001||std::abs(FPDF_GetPageHeightF(p.page)-g.h)>0.001){fail(E1_UNSUPPORTED);return false;}
 return true;
}
std::array<double,2> forward(const Geometry& g,double x,double y){
 double u=x-g.l,v=y-g.b,W=g.r-g.l,H=g.t-g.b;
 switch(g.rot){case 0:return {u,H-v};case 1:return {v,u};case 2:return {W-u,v};default:return {H-v,W-u};}
}
std::array<double,2> inverse(const Geometry& g,double x,double y){
 double W=g.r-g.l,H=g.t-g.b;
 switch(g.rot){case 0:return {x+g.l,H-y+g.b};case 1:return {y+g.l,x+g.b};case 2:return {W-x+g.l,y+g.b};default:return {W-y+g.l,H-x+g.b};}
}
// C1: signed32 safety is distinct from the deliberately smaller precision domain.
constexpr double DEVICE_DOMAIN=1048576.0,DEVICE_TOLERANCE=0.51;
bool signed_device(double v){
 return std::isfinite(v)&&v>=std::numeric_limits<int32_t>::min()&&v<=std::numeric_limits<int32_t>::max()&&
 std::round(v)>=std::numeric_limits<int32_t>::min()&&std::round(v)<=std::numeric_limits<int32_t>::max();
}
bool checked_device(double x,double y){
 if(!signed_device(x)||!signed_device(y)){fail(E1_LIMIT);return false;}
 if(std::abs(x)>DEVICE_DOMAIN||std::abs(y)>DEVICE_DOMAIN||std::abs(std::round(x))>DEVICE_DOMAIN||std::abs(std::round(y))>DEVICE_DOMAIN){fail(E1_INDETERMINATE);return false;}
 return true;
}
struct Viewport {int w,h;double sx,sy;};
bool viewport(const Geometry& g,int32_t ox,int32_t oy,double scale,Viewport& v){
 double w=std::round(g.w*scale),h=std::round(g.h*scale);
 if(!std::isfinite(w)||!std::isfinite(h)||w<1||h<1||w>1000000||h>1000000){fail(E1_LIMIT);return false;}
 // int64 addition before calling native APIs which accept/add signed32 values.
 int64_t right=int64_t(ox)+static_cast<int64_t>(w),bottom=int64_t(oy)+static_cast<int64_t>(h);
 if(!signed_device(double(right))||!signed_device(double(bottom))){fail(E1_LIMIT);return false;}
 if(!checked_device(ox,oy)||!checked_device(double(right),double(bottom)))return false;
 v={static_cast<int>(w),static_cast<int>(h),w/g.w,h/g.h};
 if(!std::isfinite(v.sx)||!std::isfinite(v.sy)||v.sx<=0||v.sy<=0){fail(E1_INDETERMINATE);return false;}
 return true;
}
std::array<double,2> device_point(const Geometry& g,const Viewport& v,int32_t ox,int32_t oy,double x,double y){
 auto q=forward(g,x,y);return {ox+q[0]*v.sx,oy+q[1]*v.sy};
}
std::string rectangle(const Geometry& g,double l,double b,double r,double t){
 auto a=forward(g,l,b),c=forward(g,l,t),d=forward(g,r,b),e=forward(g,r,t);
 return arr(std::min({a[0],c[0],d[0],e[0]}),std::min({a[1],c[1],d[1],e[1]}),std::max({a[0],c[0],d[0],e[0]}),std::max({a[1],c[1],d[1],e[1]}));
}
FPDF_TEXTPAGE text_page(Slot& p){
 if(!p.text)p.text=FPDFText_LoadPage(p.page);
 if(!p.text)fail(E1_PDF);
 return p.text;
}
bool text_count(Slot& p,int& count){
 if(!text_page(p))return false;count=FPDFText_CountChars(p.text);
 if(count<0){fail(E1_INDETERMINATE);return false;}
 if(static_cast<uint32_t>(count)>p.budget.text_units/2){fail(E1_LIMIT);return false;}return true;
}
}
static_assert(sizeof(E1Budget)==36,"ABI v1 budget layout");
API uint32_t e1_version(){return 1;}
API uint32_t e1_status(){return status;}
API uint32_t e1_native_error(){return native_error;}
API uint32_t e1_result(){return result;}
API uint32_t e1_live(){return live();}
API uint32_t e1_alloc(uint32_t size){
 start();if(!size||size>MAX_OUTPUT||allocated()+size>TOTAL_BYTES)return fail(E1_LIMIT),0;
 auto* s=fresh(INPUT,SLOTS);if(!s)return 0;s->bytes.resize(size);return handle(*s);
}
API uintptr_t e1_data(uint32_t h){start();auto* s=get(h,INPUT);if(!s){fail(E1_HANDLE);return 0;}return reinterpret_cast<uintptr_t>(s->bytes.data());}
API uint32_t e1_size(uint32_t h){start();auto* s=get(h,INPUT);if(!s)s=get(h,OUTPUT);if(!s){fail(E1_HANDLE);return 0;}return static_cast<uint32_t>(s->bytes.size());}
API uint32_t e1_copy(uint32_t h,uint32_t dest){
 start();auto* s=get(h,OUTPUT);auto* d=get(dest,INPUT);if(!s||!d)return fail(E1_HANDLE);
 if(d->bytes.size()<s->bytes.size())return fail(E1_LIMIT);
 std::memcpy(d->bytes.data(),s->bytes.data(),s->bytes.size());return E1_OK;
}
API uint32_t e1_release(uint32_t h){start();auto* s=get(h,INPUT);if(!s)s=get(h,OUTPUT);if(!s)return fail(E1_HANDLE);dispose(*s);return E1_OK;}
API uint32_t e1_open(uint32_t h,uint32_t bh){
 start();auto* in=get(h,INPUT);auto* bs=get(bh,INPUT);if(!in||!bs)return fail(E1_HANDLE);
 if(bs->bytes.size()!=sizeof(E1Budget))return fail(E1_ABI);
 E1Budget b{};std::memcpy(&b,bs->bytes.data(),sizeof b);
 if(b.version!=1||b.struct_bytes!=sizeof b)return fail(E1_ABI);
 if(!valid_budget(b)||in->bytes.size()>b.input_bytes||allocated()+in->bytes.size()>TOTAL_BYTES)return fail(E1_LIMIT);
 uint32_t docs=0;for(auto& s:slots)docs+=s.kind==DOC;if(docs>=4)return fail(E1_LIMIT);
 auto* s=fresh(DOC,b.live_handles);if(!s)return status;s->budget=b;s->bytes=in->bytes;
 if(!initialized){FPDF_InitLibrary();initialized=true;}
 s->doc=FPDF_LoadMemDocument64(s->bytes.data(),s->bytes.size(),nullptr);
 if(!s->doc){native_error=static_cast<uint32_t>(FPDF_GetLastError());dispose(*s);return fail(E1_PDF);}
 int count=FPDF_GetPageCount(s->doc);
 if(count<=0||static_cast<uint32_t>(count)>b.pages){dispose(*s);return fail(E1_LIMIT);}
 s->count=static_cast<uint32_t>(count);result=handle(*s);return E1_OK;
}
API uint32_t e1_document_pages(uint32_t h){start();auto* s=get(h,DOC);if(!s){fail(E1_HANDLE);return 0;}return s->count;}
API uint32_t e1_page(uint32_t h,uint32_t index){
 start();auto* d=get(h,DOC);if(!d)return fail(E1_HANDLE);if(index>=d->count)return fail(E1_ARGUMENT);
 uint32_t pages=0;for(auto& s:slots)pages+=s.kind==PAGE;if(pages>=8)return fail(E1_LIMIT);
 auto* s=fresh(PAGE,d->budget.live_handles);if(!s)return status;s->budget=d->budget;s->parent=h;s->page=FPDF_LoadPage(d->doc,static_cast<int>(index));
 if(!s->page){dispose(*s);return fail(E1_PDF);}result=handle(*s);return E1_OK;
}
API uint32_t e1_close(uint32_t h){
 start();if(auto* s=get(h,PAGE)){dispose(*s);return E1_OK;}
 auto* d=get(h,DOC);if(!d)return fail(E1_HANDLE);
 for(auto& s:slots)if(s.kind==PAGE&&s.parent==h)dispose(s);dispose(*d);return E1_OK;
}
API uint32_t e1_facts(uint32_t h){
 start();auto* p=page_slot(h);if(!p)return status;Geometry g{};if(!geometry(*p,g))return status;
 float l=0,b=0,r=0,t=0;bool crop=FPDFPage_GetCropBox(p->page,&l,&b,&r,&t);
 return json(*p,"{\"media\":"+arr(g.ml,g.mb,g.mr,g.mt)+",\"crop\":"+(crop?arr(l,b,r,t):"null")+",\"cropDefaulted\":"+(crop?"false":"true")+",\"effective\":"+arr(g.l,g.b,g.r,g.t)+",\"rotation\":"+std::to_string(g.rot*90)+",\"displayPoints\":["+num(g.w)+","+num(g.h)+"],\"nativeWidthHeight\":["+num(FPDF_GetPageWidthF(p->page))+","+num(FPDF_GetPageHeightF(p->page))+"],\"sourceMapping\":\"unsupported\"}");
}
API uint32_t e1_point(uint32_t h,uint32_t inv,double x,double y,int32_t ox,int32_t oy,double scale){
 start();auto* p=page_slot(h);if(!p)return status;
 if(inv>1||!std::isfinite(x)||!std::isfinite(y)||!std::isfinite(scale)||scale<=0||scale>100)return fail(E1_ARGUMENT);
 Geometry g{};if(!geometry(*p,g))return status;Viewport v{};if(!viewport(g,ox,oy,scale,v))return status;
 auto device=inv?std::array<double,2>{x,y}:device_point(g,v,ox,oy,x,y);
 // Validate transformed/rounded device values BEFORE any native int conversion.
 if(!checked_device(device[0],device[1]))return status;
 auto q=inv?inverse(g,(x-ox)/v.sx,(y-oy)/v.sy):device;
 if(!std::isfinite(q[0])||!std::isfinite(q[1]))return fail(E1_INDETERMINATE);
 int nx=0,ny=0;double px=0,py=0;
 if(inv){if(!FPDF_DeviceToPage(p->page,ox,oy,v.w,v.h,0,static_cast<int>(std::round(x)),static_cast<int>(std::round(y)),&px,&py))return fail(E1_INDETERMINATE);}
 else {if(!FPDF_PageToDevice(p->page,ox,oy,v.w,v.h,0,x,y,&nx,&ny))return fail(E1_INDETERMINATE);px=nx;py=ny;}
 if(!std::isfinite(px)||!std::isfinite(py))return fail(E1_INDETERMINATE);
 // Native PDFium uses float matrices. Bound measured error in DEVICE space;
 // inverse comparison includes requested-device integer quantization as well.
 auto observed=inv?device_point(g,v,ox,oy,px,py):std::array<double,2>{px,py};
 double ex=std::abs(observed[0]-device[0]),ey=std::abs(observed[1]-device[1]);
 if(!std::isfinite(ex)||!std::isfinite(ey)||ex>DEVICE_TOLERANCE||ey>DEVICE_TOLERANCE)return fail(E1_INDETERMINATE);
 return json(*p,"{\"point\":["+num(q[0])+","+num(q[1])+"],\"native\":["+num(px)+","+num(py)+"],\"nativeQuantizedPixels\":true,\"deviceTolerancePixels\":0.51,\"precisionDomainPixels\":1048576,\"deviceAgreementPixels\":["+num(ex)+","+num(ey)+"]}");
}
API uint32_t e1_rectangle(uint32_t h,uint32_t inv,double l,double b,double r,double t,int32_t ox,int32_t oy,double scale){
 start();auto* p=page_slot(h);if(!p)return status;
 if(inv>1||!std::isfinite(l)||!std::isfinite(b)||!std::isfinite(r)||!std::isfinite(t)||l>r||b>t||!std::isfinite(scale)||scale<=0||scale>100)return fail(E1_ARGUMENT);
 Geometry g{};if(!geometry(*p,g))return status;Viewport v{};if(!viewport(g,ox,oy,scale,v))return status;
 std::array<std::array<double,2>,4> points={{{l,b},{l,t},{r,b},{r,t}}};
 for(auto& q:points){
  auto device=inv?q:device_point(g,v,ox,oy,q[0],q[1]);if(!checked_device(device[0],device[1]))return status;
  q=inv?inverse(g,(q[0]-ox)/v.sx,(q[1]-oy)/v.sy):device;
  if(!std::isfinite(q[0])||!std::isfinite(q[1]))return fail(E1_INDETERMINATE);
 }
 return json(*p,arr(std::min({points[0][0],points[1][0],points[2][0],points[3][0]}),std::min({points[0][1],points[1][1],points[2][1],points[3][1]}),std::max({points[0][0],points[1][0],points[2][0],points[3][0]}),std::max({points[0][1],points[1][1],points[2][1],points[3][1]})));
}
API uint32_t e1_text(uint32_t h){
 start();auto* p=page_slot(h);if(!p)return status;int count=0;if(!text_count(*p,count))return status;
 Geometry g{};if(!geometry(*p,g))return status;
 std::vector<unsigned short> units(static_cast<size_t>(count)*2+1);
 int got=FPDFText_GetText(p->text,0,count,units.data());if(got<0||static_cast<uint32_t>(got)>p->budget.text_units+1)return fail(E1_INDETERMINATE);
 size_t n=got>0?static_cast<size_t>(got-1):0;std::string text,unit_json="[",chars="[";
 for(size_t i=0;i<n;++i){if(i)unit_json+=',';unit_json+=std::to_string(units[i]);}
 unit_json+=']';
 for(size_t i=0;i<n;++i){uint32_t c=units[i];if(c>=0xd800&&c<=0xdbff){if(i+1>=n||units[i+1]<0xdc00||units[i+1]>0xdfff)return fail(E1_UNSUPPORTED);c=0x10000+((c-0xd800)<<10)+(units[++i]-0xdc00);}else if(c>=0xdc00&&c<=0xdfff)return fail(E1_UNSUPPORTED);utf8(text,c);}
 // Per-character API indexes are PDFium character indexes, not UTF-16 offsets.
 for(int i=0;i<count;++i){
  if(i)chars+=',';double l=0,r=0,b=0,t=0;bool ok=FPDFText_GetCharBox(p->text,i,&l,&r,&b,&t)&&std::isfinite(l)&&std::isfinite(r)&&std::isfinite(b)&&std::isfinite(t);
  char name[256]={};int flags=0;auto size=FPDFText_GetFontInfo(p->text,i,name,sizeof name,&flags);
  std::string font=(size>0&&size<=sizeof name)?std::string(name):"";
  double origin_x=0,origin_y=0;bool origin_ok=FPDFText_GetCharOrigin(p->text,i,&origin_x,&origin_y)&&std::isfinite(origin_x)&&std::isfinite(origin_y);
  std::string origin=origin_ok?"["+num(origin_x)+","+num(origin_y)+"]":"null";
  chars+="{\"index\":"+std::to_string(i)+",\"unicode\":"+std::to_string(FPDFText_GetUnicode(p->text,i))+",\"origin\":"+origin+",\"box\":"+(ok?arr(l,b,r,t):"null")+",\"displayBox\":"+(ok?rectangle(g,l,b,r,t):"null")+",\"boxStatus\":"+quote(ok?"available":"indeterminate")+",\"font\":"+quote(font)+",\"fontStatus\":"+quote(font.empty()?"indeterminate":"available")+"}";
  if(chars.size()+text.size()+unit_json.size()>p->budget.output_bytes)return fail(E1_LIMIT);
 }
 chars+=']';return json(*p,"{\"text\":"+quote(text)+",\"utf16\":"+unit_json+",\"characterCount\":"+std::to_string(count)+",\"characters\":"+chars+",\"sourceMapping\":\"unsupported\"}");
}
API uint32_t e1_search(uint32_t h,uint32_t query){
 start();auto* p=page_slot(h);auto* q=get(query,INPUT);if(!p)return status;if(!q)return fail(E1_HANDLE);
 if(q->bytes.size()<4||q->bytes.size()>512||q->bytes.size()%2)return fail(E1_ARGUMENT);
 std::vector<unsigned short> units(q->bytes.size()/2);std::memcpy(units.data(),q->bytes.data(),q->bytes.size());
 if(units.back()!=0||std::find(units.begin(),units.end()-1,0)!=units.end()-1)return fail(E1_ARGUMENT);
 int count=0;if(!text_count(*p,count))return status;
 auto find=FPDFText_FindStart(p->text,units.data(),0,0);if(!find)return fail(E1_INDETERMINATE);
 std::string out="[";uint32_t hits=0;
 while(FPDFText_FindNext(find)){
  if(hits>=p->budget.search_hits){FPDFText_FindClose(find);return fail(E1_LIMIT);}
  if(hits++)out+=',';out+="{\"start\":"+std::to_string(FPDFText_GetSchResultIndex(find))+",\"count\":"+std::to_string(FPDFText_GetSchCount(find))+"}";
 }
 FPDFText_FindClose(find);return json(*p,out+"]");
}
API uint32_t e1_raster(uint32_t h,uint32_t w,uint32_t height){
 start();auto* p=page_slot(h);if(!p)return status;
 if(!w||!height||w>16384||height>16384)return fail(E1_LIMIT);
 uint64_t pixels=uint64_t(w)*height,bytes=pixels*4+16;
 if(pixels>p->budget.raster_pixels||bytes>p->budget.output_bytes||allocated()+bytes>TOTAL_BYTES||live()>=p->budget.live_handles)return fail(E1_LIMIT);
 auto* s=fresh(OUTPUT,p->budget.live_handles);if(!s)return status;s->bytes.resize(static_cast<size_t>(bytes));
 uint32_t header[4]={0x31424752,w,height,w*4};std::memcpy(s->bytes.data(),header,sizeof header);
 auto bitmap=FPDFBitmap_CreateEx(static_cast<int>(w),static_cast<int>(height),FPDFBitmap_BGRA,s->bytes.data()+16,static_cast<int>(w*4));
 if(!bitmap){dispose(*s);return fail(E1_MEMORY);}
 FPDFBitmap_FillRect(bitmap,0,0,static_cast<int>(w),static_cast<int>(height),0xffffffff);
 FPDF_RenderPageBitmap(bitmap,p->page,0,0,static_cast<int>(w),static_cast<int>(height),0,0);
 FPDFBitmap_Destroy(bitmap);result=handle(*s);return E1_OK;
}
API void e1_reset(){
 start();for(auto& s:slots)if(s.kind==PAGE)dispose(s);for(auto& s:slots)if(s.kind)dispose(s);
 if(initialized){FPDF_DestroyLibrary();initialized=false;}
}
#include "actions.inc"
