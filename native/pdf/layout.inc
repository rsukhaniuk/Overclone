// Additive bounded native facts. This file evaluates no document standard.
// The optional internal accessor must be compiled against the SAME pinned
// source/configuration as libpdfium.a, never against guessed class layouts.
#if defined(E1_PDFIUM_SOURCE_USERUNIT)
#include "core/fpdfapi/page/cpdf_page.h"
#include "core/fpdfapi/parser/cpdf_dictionary.h"
#include "core/fpdfapi/parser/cpdf_object.h"
#include "fpdfsdk/cpdfsdk_helpers.h"
#endif
namespace {
constexpr int LAYOUT_CHARACTER_CAP=100000;
constexpr double LAYOUT_NUMBER_CAP=1000000;
bool layout_number(double v){return std::isfinite(v)&&std::abs(v)<=LAYOUT_NUMBER_CAP;}
struct LayoutScale { const char* status;const char* method;double value; };
LayoutScale layout_scale(Slot& p){
#if defined(E1_PDFIUM_SOURCE_USERUNIT)
 auto* native=CPDFPageFromFPDFPage(p.page);
 if(!native||!native->GetDict())return {"indeterminate","invalid",0};
 auto dict=native->GetDict();
 // UserUnit is a PAGE entry, not an inheritable page-tree attribute. Resolve
 // an indirect numeric object through PDFium, but never search /Parent.
 if(!dict->KeyExist("UserUnit"))return {"available","default-userunit",1};
 auto unit=dict->GetDirectObjectFor("UserUnit");
 if(!unit||!unit->IsNumber())return {"indeterminate","invalid",0};
 double value=unit->GetNumber();
 if(!layout_number(value)||value<=0||value>75000)return {"indeterminate","invalid",0};
 return {"available","page-dictionary-userunit",value};
#else
 (void)p;return {"unsupported","unavailable",0};
#endif
}
bool layout_pair(ActionJson& out,double x,double y,bool present){
 if(!present)return out.add("null");
 return out.add("[")&&out.number(x)&&out.add(",")&&out.number(y)&&out.add("]");
}
bool layout_box(ActionJson& out,double l,double b,double r,double t){
 for(double n:{l,b,r,t})if(!layout_number(n)){fail(E1_INDETERMINATE);return false;}
 return out.add("[")&&out.number(l)&&out.add(",")&&out.number(b)&&out.add(",")&&out.number(r)&&out.add(",")&&out.number(t)&&out.add("]");
}
bool serialize_layout(ActionJson& out,Slot& p,const Geometry& g,int count,LayoutScale scale){
 auto* parent=get(p.parent,DOC);if(!parent){fail(E1_HANDLE);return false;}
 if(!out.add("{\"schemaVersion\":1,\"pageIndex\":")||!out.number(p.count)||!out.add(",\"pageCount\":")||!out.number(parent->count)
   ||!out.add(",\"media\":")||!layout_box(out,g.ml,g.mb,g.mr,g.mt)||!out.add(",\"crop\":"))return false;
 float l=0,b=0,r=0,t=0;
 if(FPDFPage_GetCropBox(p.page,&l,&b,&r,&t)){if(!layout_box(out,l,b,r,t))return false;}else if(!out.add("null"))return false;
 if(!out.add(",\"effective\":")||!layout_box(out,g.l,g.b,g.r,g.t)||!out.add(",\"rotation\":")||!out.number(g.rot*90)
   ||!out.add(",\"userUnit\":{\"status\":")||!out.string(scale.status,std::strlen(scale.status))||!out.add(",\"value\":"))return false;
 if(std::strcmp(scale.status,"available")==0){if(!out.number(scale.value))return false;}else if(!out.add("null"))return false;
 if(!out.add(",\"method\":")||!out.string(scale.method,std::strlen(scale.method))||!out.add("},\"characters\":["))return false;
 for(int i=0;i<count;++i){
  uint32_t scalar=FPDFText_GetUnicode(p.text,i);
  if(scalar>0x10ffff||(scalar>=0xd800&&scalar<=0xdfff)){fail(E1_INDETERMINATE);return false;}
  double x=0,y=0;bool origin=FPDFText_GetCharOrigin(p.text,i,&x,&y)&&layout_number(x)&&layout_number(y);
  FS_MATRIX matrix{};bool has_matrix=FPDFText_GetMatrix(p.text,i,&matrix);
  if(has_matrix)for(double n:{matrix.a,matrix.b,matrix.c,matrix.d,matrix.e,matrix.f})if(!layout_number(n))has_matrix=false;
  double size=FPDFText_GetFontSize(p.text,i);bool has_size=layout_number(size)&&size>0;
  std::array<char,257> font{};int flags=0;unsigned long length=FPDFText_GetFontInfo(p.text,i,font.data(),font.size(),&flags);
  // No truncated font name or replacement decoding. Absence stays explicit.
  bool has_font=length>1&&length<=font.size()&&font[length-1]==0&&action_utf8(font.data(),length-1)
    &&std::memchr(font.data(),0,length-1)==nullptr;
  auto object=FPDFText_GetTextObject(p.text,i);int mode=object?FPDFTextObj_GetTextRenderMode(object):-1;
  if((i&&!out.add(","))||!out.add("{\"index\":")||!out.number(i)||!out.add(",\"unicode\":")||!out.number(scalar)
    ||!out.add(",\"origin\":")||!layout_pair(out,x,y,origin)||!out.add(",\"advance\":null,\"matrix\":"))return false;
  if(has_matrix){
   if(!out.add("[")||!out.number(matrix.a)||!out.add(",")||!out.number(matrix.b)||!out.add(",")||!out.number(matrix.c)
      ||!out.add(",")||!out.number(matrix.d)||!out.add(",")||!out.number(matrix.e)||!out.add(",")||!out.number(matrix.f)||!out.add("]"))return false;
  }else if(!out.add("null"))return false;
  if(!out.add(",\"nominalSize\":"))return false;
  if(has_size){if(!out.number(size))return false;}else if(!out.add("null"))return false;
  if(!out.add(",\"fontName\":"))return false;
  if(has_font){if(!out.string(font.data(),length-1))return false;}else if(!out.add("null"))return false;
  if(!out.add(",\"fontFlags\":"))return false;
  if(has_font){if(!out.number(static_cast<uint32_t>(flags)))return false;}else if(!out.add("null"))return false;
  if(!out.add(",\"renderMode\":"))return false;
  if(mode>=0&&mode<=7){if(!out.number(mode))return false;}else if(!out.add("null"))return false;
  // Public character boxes do not account for all clip/style effects. Never
  // turn these nominal/native facts into a painted ink or visual-bold claim.
  if(!out.add(",\"inkBox\":null,\"inkStatus\":\"unsupported\",\"styleStatus\":\"unsupported\"}"))return false;
 }
 return out.add("],\"complete\":true,\"limitations\":[\"glyph-ink-not-measured\",\"effective-style-not-measured\",\"advance-not-measured\",\"semantic-roles-not-classified\"]}");
}
}
API uint32_t e1_layout_capabilities(uint32_t h){
 start();auto* p=page_slot(h);if(!p)return status;auto scale=layout_scale(*p);
 return json(*p,std::string("{\"schemaVersion\":1,\"coordinateSpace\":\"pdf-raw-unrotated-bottom-left\",\"physicalScale\":")+quote(scale.status)
  +",\"glyphInk\":\"unsupported\",\"textStyle\":\"unsupported\",\"lineGrouping\":\"unsupported\",\"maxCharacters\":100000,\"nominalSizeUnit\":\"raw-user-units\",\"nominalSizeMethod\":\"pdfium-font-size\"}");
}
API uint32_t e1_layout_facts(uint32_t h){
 start();auto* p=page_slot(h);if(!p)return status;Geometry g{};if(!geometry(*p,g)||!text_page(*p))return status;
 int count=FPDFText_CountChars(p->text);
 if(count<0)return fail(E1_INDETERMINATE);
 if(count>LAYOUT_CHARACTER_CAP||static_cast<uint32_t>(count)>p->budget.text_units)return fail(E1_LIMIT);
 auto scale=layout_scale(*p);ActionJson preflight(p->budget.output_bytes);
 if(!serialize_layout(preflight,*p,g,count,scale))return status;
 if(preflight.count>MAX_OUTPUT||allocated()+preflight.count>TOTAL_BYTES||live()>=p->budget.live_handles)return fail(E1_LIMIT);
 ActionJson filled(preflight.count);filled.filling=true;filled.bytes.reserve(preflight.count);
 if(!serialize_layout(filled,*p,g,count,scale))return status;
 if(filled.count!=preflight.count)return fail(E1_INDETERMINATE);
 return output(*p,filled.bytes.data(),filled.bytes.size());
}
