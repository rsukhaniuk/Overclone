// Optional independent placement observations. Included after frozen layout.inc.
// No old E1/layout/ink field sets or measurement predicates are changed here.
#include "e1_object_layout.h"
#if defined(E1_PDFIUM_SOURCE_USERUNIT)
#include "core/fpdfapi/page/cpdf_image.h"
#include "core/fpdfapi/page/cpdf_imageobject.h"
namespace {
constexpr size_t OBJECT_LAYOUT_OBJECT_CAP=10000,OBJECT_LAYOUT_IMAGE_CAP=512;
constexpr size_t OBJECT_LAYOUT_JSON_CAP=1024*1024;
constexpr const char* OBJECT_LAYOUT_UNKNOWN_CTM="source-ctm-accumulation-error-unmeasured";
struct ObjectImagePlacement {
 const CPDF_PageObject* object=nullptr;
 size_t index=0;
 bool available=false;
 const char* reason="object-geometry-out-of-domain";
 std::array<double,6> matrix{};
 std::array<std::array<double,2>,4> quad{};
 double l=0,b=0,r=0,t=0,error=0;
};
bool object_image_dimension(const CPDF_Dictionary& dict,const char* key,int native){
 auto value=dict.GetDirectObjectFor(key);
 if(!value||!value->IsNumber())return false;
 double n=value->GetNumber();
 return layout_number(n)&&n>=1&&std::floor(n)==n&&n==native;
}
ObjectImagePlacement object_image_placement(const CPDF_PageObject& object,size_t index,const Geometry& g,bool page_group){
 ObjectImagePlacement row;row.object=&object;row.index=index;
 if(!object.IsActive()){row.reason="inactive-object";return row;}
 auto* native=object.AsImage();
 if(!native)return row;
 auto image=native->GetImage();
 if(!image)return row;
 auto dict=image->GetDict();
 if(!dict||!object_image_dimension(*dict,"Width",image->GetPixelWidth())||!object_image_dimension(*dict,"Height",image->GetPixelHeight())){
  row.reason="invalid-image-dimensions";return row;
 }
 if(page_group){row.reason="page-compositing-group";return row;}
 if(object.clip_path().HasRef()){row.reason="clip-path-present";return row;}
 if(object.GetContentMarks()->CountItems()||dict->KeyExist("OC")){
  row.reason="optional-content-unresolved";return row;
 }
 if(image->IsMask()||dict->KeyExist("Mask")||dict->KeyExist("SMask")){
  row.reason="image-mask-or-softmask";return row;
 }
 if(dict->KeyExist("ImageMask")){
  auto mask=dict->GetDirectObjectFor("ImageMask");
  if(!mask||!mask->IsBoolean()||mask->GetInteger()!=0){row.reason="image-mask-or-softmask";return row;}
 }
 if(!object.general_state().E1DefaultGeometryState()){
  row.reason="nondefault-compositing";return row;
 }
 const auto& matrix=native->matrix();
 row.matrix={matrix.a,matrix.b,matrix.c,matrix.d,matrix.e,matrix.f};
 for(double v:row.matrix)if(!layout_number(v)){row.reason="invalid-object-matrix";return row;}
 const double a=row.matrix[0],b=row.matrix[1],c=row.matrix[2],d=row.matrix[3],e=row.matrix[4],f=row.matrix[5];
 const double magnitude=std::max({1.0,std::abs(a)+std::abs(c)+std::abs(e),std::abs(b)+std::abs(d)+std::abs(f)});
 row.error=(8*std::numeric_limits<float>::epsilon()+16*std::numeric_limits<double>::epsilon())*magnitude;
 // Native matrix is the observation. This bound does not cover arbitrary
 // original source CTM concatenations; expose that limitation on every row.
 const double determinant=a*d-b*c;
 const double determinant_error=16*std::numeric_limits<float>::epsilon()*std::max(1.0,std::abs(a*d)+std::abs(b*c));
 if(!layout_number(row.error)||row.error<=0||!std::isfinite(determinant)||std::abs(determinant)<=determinant_error){
  row.reason="invalid-object-matrix";return row;
 }
 row.quad={{{e,f},{a+e,b+f},{a+c+e,b+d+f},{c+e,d+f}}};
 row.l=row.r=e;row.b=row.t=f;
 for(const auto& point:row.quad){
  if(!layout_number(point[0])||!layout_number(point[1])){row.reason="invalid-object-matrix";return row;}
  row.l=std::min(row.l,point[0]);row.r=std::max(row.r,point[0]);
  row.b=std::min(row.b,point[1]);row.t=std::max(row.t,point[1]);
 }
 if(row.l-row.error<g.l||row.b-row.error<g.b||row.r+row.error>g.r||row.t+row.error>g.t){
  row.reason="crosses-effective-page";return row;
 }
 row.available=true;row.reason="parsed-native-image-matrix";return row;
}
bool object_layout_rows(ActionJson& out,Slot& slot,const Geometry& g,const std::array<size_t,6>& counts,
                        const std::vector<ObjectImagePlacement>& images,LayoutScale scale){
 auto* parent=get(slot.parent,DOC);if(!parent){fail(E1_HANDLE);return false;}
 size_t count=0;for(size_t n:counts)count+=n;
 if(!out.add("{\"schemaVersion\":1,\"coordinateSpace\":\"pdf-raw-unrotated-bottom-left\",\"scope\":\"direct-page-image-placement\",\"pageIndex\":")
  ||!out.number(slot.count)||!out.add(",\"pageCount\":")||!out.number(parent->count)
  ||!out.add(",\"media\":")||!layout_box(out,g.ml,g.mb,g.mr,g.mt)||!out.add(",\"crop\":"))return false;
 float l=0,b=0,r=0,t=0;
 if(FPDFPage_GetCropBox(slot.page,&l,&b,&r,&t)){if(!layout_box(out,l,b,r,t))return false;}else if(!out.add("null"))return false;
 if(!out.add(",\"effective\":")||!layout_box(out,g.l,g.b,g.r,g.t)||!out.add(",\"rotation\":")||!out.number(g.rot*90)
  ||!out.add(",\"userUnit\":{\"status\":")||!out.string(scale.status,std::strlen(scale.status))||!out.add(",\"value\":"))return false;
 if(std::strcmp(scale.status,"available")==0){if(!out.number(scale.value))return false;}else if(!out.add("null"))return false;
 if(!out.add(",\"method\":")||!out.string(scale.method,std::strlen(scale.method))||!out.add("},\"directObjectCount\":")||!out.number(count)
  ||!out.add(",\"objectTypeCounts\":{"))return false;
 constexpr std::array<const char*,6> names{"text","path","image","shading","form","unknown"};
 for(size_t i=0;i<names.size();++i){if((i&&!out.add(","))||!out.string(names[i],std::strlen(names[i]))||!out.add(":")||!out.number(counts[i]))return false;}
 if(!out.add("},\"images\":["))return false;
 for(size_t i=0;i<images.size();++i){const auto& row=images[i];
  if((i&&!out.add(","))||!out.add("{\"objectIndex\":")||!out.number(row.index)||!out.add(",\"active\":")||!out.add(row.object->IsActive()?"true":"false")
   ||!out.add(",\"status\":")||!out.add(row.available?"\"available\"":"\"indeterminate\"")
   ||!out.add(",\"method\":\"native-image-unit-square-placement\",\"matrix\":"))return false;
  if(row.available){
   if(!out.add("["))return false;
   for(size_t j=0;j<row.matrix.size();++j)if((j&&!out.add(","))||!out.number(row.matrix[j]))return false;
   if(!out.add("]"))return false;
  }else if(!out.add("null"))return false;
  if(!out.add(",\"quad\":"))return false;
  if(row.available){
   if(!out.add("["))return false;
   for(size_t j=0;j<row.quad.size();++j)if((j&&!out.add(","))||!layout_pair(out,row.quad[j][0],row.quad[j][1],true))return false;
   if(!out.add("]"))return false;
  }else if(!out.add("null"))return false;
  if(!out.add(",\"rawBox\":"))return false;
  if(row.available){if(!layout_box(out,row.l,row.b,row.r,row.t))return false;}else if(!out.add("null"))return false;
  if(!out.add(",\"uncertainty\":"))return false;
  if(row.available){if(!out.add("{\"absoluteErrorRaw\":")||!out.number(row.error)||!out.add(",\"method\":\"pdfium-float32-matrix-bound\"}"))return false;}
  else if(!out.add("null"))return false;
  if(!out.add(",\"reason\":")||!out.string(row.reason,std::strlen(row.reason))||!out.add(",\"limitations\":[")
   ||!out.string(OBJECT_LAYOUT_UNKNOWN_CTM,std::strlen(OBJECT_LAYOUT_UNKNOWN_CTM))||!out.add("]}"))return false;
 }
 return out.add("],\"scanComplete\":true,\"limitations\":[\"direct-page-objects-only\",\"native-parsed-inventory-not-content-validation\",\"image-placement-not-ink\",\"image-visibility-not-established\",\"object-role-unestablished\",\"caption-association-unestablished\",\"nested-forms-not-inspected\",\"clip-or-compositing-uncertain-refused\",\"source-ctm-accumulation-error-unmeasured\"]}");
}
}
#endif
API uint32_t e1_object_layout_facts(uint32_t h){
 start();auto* slot=page_slot(h);if(!slot)return status;
#if defined(E1_PDFIUM_SOURCE_USERUNIT)
 Geometry g{};if(!geometry(*slot,g))return status;
 auto* page=CPDFPageFromFPDFPage(slot->page);
 if(!page||!page->GetDict())return fail(E1_INDETERMINATE);
 const size_t count=page->GetPageObjectCount();
 if(count>OBJECT_LAYOUT_OBJECT_CAP||live()>=slot->budget.live_handles)return fail(E1_LIMIT);
 std::array<size_t,6> counts{};std::vector<ObjectImagePlacement> images;
 images.reserve(std::min(count,OBJECT_LAYOUT_IMAGE_CAP));
 const bool page_group=page->GetDict()->KeyExist("Group");
 for(size_t i=0;i<count;++i){
  const auto* object=page->GetPageObjectByIndex(i);if(!object)return fail(E1_INDETERMINATE);
  auto type=object->GetType();size_t index=5;
  switch(type){
   case CPDF_PageObject::Type::kText:index=0;break;
   case CPDF_PageObject::Type::kPath:index=1;break;
   case CPDF_PageObject::Type::kImage:index=2;break;
   case CPDF_PageObject::Type::kShading:index=3;break;
   case CPDF_PageObject::Type::kForm:index=4;break;
   default:break;
  }
  ++counts[index];
  if(index==2){if(images.size()>=OBJECT_LAYOUT_IMAGE_CAP)return fail(E1_LIMIT);images.push_back(object_image_placement(*object,i,g,page_group));}
 }
 const auto scale=layout_scale(*slot);
 ActionJson preflight(std::min(static_cast<size_t>(slot->budget.output_bytes),OBJECT_LAYOUT_JSON_CAP));
 if(!object_layout_rows(preflight,*slot,g,counts,images,scale))return status;
 if(allocated()+preflight.count>TOTAL_BYTES)return fail(E1_LIMIT);
 ActionJson filled(preflight.count);filled.filling=true;filled.bytes.reserve(preflight.count);
 if(!object_layout_rows(filled,*slot,g,counts,images,scale))return status;
 if(filled.count!=preflight.count)return fail(E1_INDETERMINATE);
 return output(*slot,filled.bytes.data(),filled.bytes.size());
#else
 return fail(E1_UNSUPPORTED);
#endif
}
