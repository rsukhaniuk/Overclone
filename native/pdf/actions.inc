// Additive optional passive action metadata; project code, not PDFium source.
#include <fpdf_doc.h>
#include <string_view>
namespace {
constexpr uint32_t ACTION_LINK_CAP=128,ACTION_FIELD_CAP=4096;
struct ActionJson {
 size_t limit,count=0;bool filling=false;std::string bytes;
 explicit ActionJson(size_t n):limit(n){}
 bool add(std::string_view s){
  if(s.size()>limit-count){fail(E1_LIMIT);return false;}
  count+=s.size();if(filling)bytes.append(s.data(),s.size());return true;
 }
 bool string(const char* s,size_t n){
  if(!add("\""))return false;
  for(size_t i=0;i<n;++i){
   unsigned char c=static_cast<unsigned char>(s[i]);
   if(c=='"'||c=='\\'){char b[2]={'\\',static_cast<char>(c)};if(!add(std::string_view(b,2)))return false;}
   else if(c<32){char b[7];std::snprintf(b,sizeof b,"\\u%04x",c);if(!add(std::string_view(b,6)))return false;}
   else {char b=static_cast<char>(c);if(!add(std::string_view(&b,1)))return false;}
  }
  return add("\"");
 }
 bool number(double n){char b[64];int size=std::snprintf(b,sizeof b,"%.17g",n);if(size<0||size>=static_cast<int>(sizeof b)){fail(E1_INDETERMINATE);return false;}return add(std::string_view(b,static_cast<size_t>(size)));}
};
bool action_utf8(const char* text,size_t size){
 for(size_t i=0;i<size;){
  auto c=static_cast<unsigned char>(text[i++]);if(c<128)continue;
  uint32_t scalar=0,minimum=0;size_t extra=0;
  if(c>=0xc2&&c<=0xdf){scalar=c&31;minimum=0x80;extra=1;}
  else if(c>=0xe0&&c<=0xef){scalar=c&15;minimum=0x800;extra=2;}
  else if(c>=0xf0&&c<=0xf4){scalar=c&7;minimum=0x10000;extra=3;}else return false;
  if(extra>size-i)return false;
  for(size_t j=0;j<extra;++j){auto d=static_cast<unsigned char>(text[i++]);if((d&0xc0)!=0x80)return false;scalar=(scalar<<6)|(d&63);}
  if(scalar<minimum||scalar>0x10ffff||(scalar>=0xd800&&scalar<=0xdfff))return false;
 }
 return true;
}
bool action_field(ActionJson& out,FPDF_DOCUMENT doc,FPDF_ACTION action,bool applicable,bool uri){
 if(!applicable)return out.add("{\"status\":\"not-applicable\",\"nativeBytes\":null,\"value\":null}");
 unsigned long length=uri?FPDFAction_GetURIPath(doc,action,nullptr,0):FPDFAction_GetFilePath(action,nullptr,0);
 if(length>ACTION_FIELD_CAP){fail(E1_LIMIT);return false;}
 if(!out.add("{\"status\":"))return false;
 const char* state="unavailable";std::array<char,ACTION_FIELD_CAP> data{};bool valid=false;
 if(length){
  unsigned long got=uri?FPDFAction_GetURIPath(doc,action,data.data(),length):FPDFAction_GetFilePath(action,data.data(),length);
  if(got!=length||data[length-1]!=0)state="invalid-native-string";
  else if(!action_utf8(data.data(),length-1))state="invalid-utf8";
  else {state="available";valid=true;}
 }
 if(!out.string(state,std::strlen(state))||!out.add(",\"nativeBytes\":")||!out.number(length)||!out.add(",\"value\":"))return false;
 if(valid){if(!out.string(data.data(),length-1))return false;}else if(!out.add("null"))return false;
 return out.add("}");
}
bool serialize_actions(ActionJson& out,Slot& page,const std::array<FPDF_LINK,ACTION_LINK_CAP>& links,size_t count){
 auto* parent=get(page.parent,DOC);if(!parent){fail(E1_HANDLE);return false;}
 if(!out.add("{\"schemaVersion\":1,\"coordinateSpace\":\"raw-default-user-space\",\"physicalMeasurement\":\"indeterminate\",\"count\":")||!out.number(count)||!out.add(",\"links\":["))return false;
 for(size_t i=0;i<count;++i){
  FPDF_LINK link=links[i];FPDF_ACTION action=FPDFLink_GetAction(link);
  unsigned long type=action?FPDFAction_GetType(action):0;
  FS_RECTF rect{};bool rect_present=FPDFLink_GetAnnotRect(link,&rect);
  bool rect_valid=rect_present&&std::isfinite(rect.left)&&std::isfinite(rect.bottom)&&std::isfinite(rect.right)&&std::isfinite(rect.top)&&rect.left<=rect.right&&rect.bottom<=rect.top;
  if((i&&!out.add(","))||!out.add("{\"index\":")||!out.number(i)||!out.add(",\"rectangle\":{\"status\":"))return false;
  const char* rect_status=rect_valid?"available":(rect_present?"invalid":"unavailable");
  if(!out.string(rect_status,std::strlen(rect_status))||!out.add(",\"raw\":"))return false;
  if(rect_valid){
   if(!out.add("[")||!out.number(rect.left)||!out.add(",")||!out.number(rect.bottom)||!out.add(",")||!out.number(rect.right)||!out.add(",")||!out.number(rect.top)||!out.add("]"))return false;
  }else if(!out.add("null"))return false;
  if(!out.add("},\"action\":{\"status\":")||!out.add(action?"\"available\"":"\"absent\"")||!out.add(",\"nativeType\":"))return false;
  if(action){if(!out.number(type))return false;}else if(!out.add("null"))return false;
  if(!out.add(",\"typeStatus\":")||!out.add(!action?"\"absent\"":(type>=1&&type<=5?"\"known\"":"\"unknown\""))||!out.add("},\"uri\":"))return false;
  if(!action_field(out,parent->doc,action,action&&type==PDFACTION_URI,true)||!out.add(",\"filePath\":")||!action_field(out,parent->doc,action,action&&(type==PDFACTION_LAUNCH||type==PDFACTION_REMOTEGOTO),false))return false;
  // Action kind establishes locality BEFORE any numeric target interpretation.
  // The link getter can expose foreign/embedded numbers valid in this document's
  // index range. Neither that pointer nor an in-range integer establishes locality.
  FPDF_DEST destination=nullptr;const char* source=nullptr;
  const bool local_action=action&&type==PDFACTION_GOTO;
  const bool foreign_action=action&&(type==PDFACTION_REMOTEGOTO||type==PDFACTION_EMBEDDEDGOTO);
  if(local_action){destination=FPDFAction_GetDest(parent->doc,action);source="action-api";}
  else if(!action){destination=FPDFLink_GetDest(parent->doc,link);if(destination)source="link-api";}
  int target=destination?FPDFDest_GetDestPageIndex(parent->doc,destination):-1;
  bool target_valid=target>=0&&static_cast<uint32_t>(target)<parent->count;
  const char* state=foreign_action?"not-local":(target_valid?"available":(destination?"invalid":(local_action?"unavailable":"not-applicable")));
  if(!out.add(",\"destination\":{\"status\":")||!out.string(state,std::strlen(state))||!out.add(",\"source\":"))return false;
  if(source){if(!out.string(source,std::strlen(source)))return false;}else if(!out.add("null"))return false;
  if(!out.add(",\"pageIndex\":"))return false;
  if(target_valid){if(!out.number(target))return false;}else if(!out.add("null"))return false;
  if(!out.add("}}"))return false;
 }
 return out.add("]}");
}
}
API uint32_t e1_capabilities(uint32_t h){
 start();auto* page=page_slot(h);if(!page)return status;
 return json(*page,"{\"schemaVersion\":1,\"abiVersion\":1,\"actions\":true,\"coordinateSpace\":\"raw-default-user-space\",\"physicalMeasurement\":\"indeterminate\",\"maxLinks\":128,\"maxFieldBytesIncludingNul\":4096,\"actionExecution\":false}");
}
API uint32_t e1_actions(uint32_t h){
 start();auto* page=page_slot(h);if(!page)return status;
 if(live()>=page->budget.live_handles)return fail(E1_LIMIT);
 // Fixed-size stack storage: no count-driven allocation, bounded enumeration.
 std::array<FPDF_LINK,ACTION_LINK_CAP> links{};size_t count=0;int position=0;FPDF_LINK link=nullptr;
 while(FPDFLink_Enumerate(page->page,&position,&link)){
  if(count>=ACTION_LINK_CAP)return fail(E1_LIMIT);
  if(!link)return fail(E1_INDETERMINATE);
  links[count++]=link;
 }
 ActionJson measure(page->budget.output_bytes);
 if(!serialize_actions(measure,*page,links,count))return status;
 if(allocated()+measure.count>TOTAL_BYTES)return fail(E1_LIMIT);
 ActionJson actual(measure.count);actual.filling=true;actual.bytes.reserve(measure.count);
 if(!serialize_actions(actual,*page,links,count))return status;
 if(actual.count!=measure.count)return fail(E1_INDETERMINATE);
 return json(*page,actual.bytes);
}
