// Conservative source-associated geometric ink; unsupported paint stays indeterminate.
#include "core/fpdftext/cpdf_textpage.h"
#include "core/fpdfapi/page/cpdf_textobject.h"
#include "core/fpdfapi/page/cpdf_contentmarks.h"
#include "core/fpdfapi/page/cpdf_color.h"
#include "core/fpdfapi/font/cpdf_font.h"
#include "core/fxge/cfx_font.h"
#include "core/fxge/cfx_face.h"
#include "core/fxge/cfx_path.h"
#include "core/fxge/text_char_pos.h"
#include <unordered_map>
#include <map>
#include "core/fxge/cfx_renderdevice.h"
#include "core/fxge/cfx_fillrenderoptions.h"
#include "core/fxge/dib/cfx_dibitmap.h"
namespace {
constexpr size_t INK_POINT_CAP=4096,INK_OBJECT_CAP=10000,INK_WORK_CAP=20000000;
bool ink_finite(double v){return std::isfinite(v)&&std::abs(v)<=1000000;}
bool ink_face(const CPDF_Font* font){
 if(!font||!font->IsEmbedded()||font->IsType3Font()||font->IsVertWriting())return false;
 auto* facefont=font->GetFont();auto face=facefont->GetFace();
 return face&&!facefont->GetSubstFont()&&face->E1StableOutlineFace();
}
bool ink_opaque_black_text(const CPDF_PageObject* obj){
 const auto* text=obj->AsText();
 if(!text||!text->IsActive()||text->GetTextRenderMode()!=TextRenderingMode::MODE_FILL
    ||text->clip_path().HasRef()||!text->general_state().E1DefaultGeometryState()||text->GetContentMarks()->CountItems())return false;
 const auto* color=text->color_state().GetFillColor();
 return (!color||!color->IsPattern())&&text->color_state().GetFillColorRef()==0&&ink_face(text->GetFont().Get());
}
struct InkObject { const CPDF_PageObject* object;bool black;size_t native_count=0; };
struct InkWitness {bool available=false;double error=0;};
using InkKey=std::array<uint64_t,7>;
struct InkContext {
 CPDF_Page* page;std::vector<InkObject> objects;std::unordered_map<const CPDF_PageObject*,size_t> indexes;size_t work=0;bool page_group=false;uint32_t raster_cap=0;std::map<InkKey,InkWitness> witnesses;
 explicit InkContext(Slot& slot):page(CPDFPageFromFPDFPage(slot.page)),raster_cap(slot.budget.raster_pixels){
  if(!page){fail(E1_INDETERMINATE);return;}
  size_t count=page->GetPageObjectCount();if(count>INK_OBJECT_CAP){fail(E1_LIMIT);return;}
  page_group=!!page->GetDict()->GetDirectObjectFor("Group");objects.reserve(count);indexes.reserve(count);
  for(size_t i=0;i<count;++i){auto* object=page->GetPageObjectByIndex(i);if(!object){fail(E1_INDETERMINATE);return;}
   objects.push_back({object,ink_opaque_black_text(object),0});indexes.emplace(object,i);
  }
  // Native text extraction may deduplicate overlapping paint or replace glyphs
  // with ActualText/ligature pieces. Do not call that incomplete paint coverage.
  auto* textpage=CPDFTextPageFromFPDFTextPage(slot.text);
  if(!textpage){fail(E1_INDETERMINATE);return;}
  for(size_t i=0;i<textpage->size();++i){const auto& info=textpage->GetCharInfo(i);auto found=indexes.find(info.text_object());
   if(found!=indexes.end()&&info.char_type()==CPDF_TextPage::CharType::kNormal)objects[found->second].native_count++;
  }
  for(auto& object:objects){const auto* text=object.object->AsText();if(text&&object.native_count!=text->CharCount())object.black=false;}
 }
 bool tick(size_t n=1){if(n>INK_WORK_CAP-work){fail(E1_LIMIT);return false;}work+=n;return true;}
};
struct InkBox {double l=0,b=0,r=0,t=0;bool available=false;const char* state="indeterminate";double error=0;};
struct InkBounds {
 double l=0,b=0,r=0,t=0;bool used=false;
 bool point(double x,double y){if(!ink_finite(x)||!ink_finite(y))return false;
  if(!used){l=r=x;b=t=y;used=true;}else{l=std::min(l,x);r=std::max(r,x);b=std::min(b,y);t=std::max(t,y);}return true;}
 static double at(double p,double q,double r,double s,double u){double v=1-u;return v*v*v*p+3*v*v*u*q+3*v*u*u*r+u*u*u*s;}
 bool cubic(CFX_PointF p,CFX_PointF q,CFX_PointF r0,CFX_PointF s){
  if(!point(p.x,p.y)||!point(s.x,s.y))return false;
  auto axis=[&](double p0,double p1,double p2,double p3){
   const double a=-p0+3*p1-3*p2+p3,b=2*(p0-2*p1+p2),c=p1-p0;
   auto sample=[&](double u){return !(u>0&&u<1)||point(at(p.x,q.x,r0.x,s.x,u),at(p.y,q.y,r0.y,s.y,u));};
   if(a==0)return b==0||sample(-c/b);
   double disc=b*b-4*a*c;if(!std::isfinite(disc))return false;if(disc<0)return true;
   // Stable quadratic formula avoids cancellation near a repeated/endpoint root.
   double root=std::sqrt(disc),v=-0.5*(b+std::copysign(root,b));
   if(v==0)return sample(-b/(2*a));
   return sample(v/a)&&sample(c/v);
  };
  return axis(p.x,q.x,r0.x,s.x)&&axis(p.y,q.y,r0.y,s.y);
 }
};
bool ink_bounds(const CFX_Path& path,const CFX_Matrix& glyph,InkBounds& bounds){
 const auto& points=path.GetPoints();
 CFX_PointF previous;bool have=false;
 for(size_t i=0;i<points.size();++i){
  auto current=glyph.Transform(points[i].point_);
  if(!ink_finite(current.x)||!ink_finite(current.y))return false;
  if(points[i].type_==CFX_Path::Point::Type::kMove){previous=current;have=true;}
  else if(points[i].type_==CFX_Path::Point::Type::kLine){if(!have||!bounds.point(previous.x,previous.y)||!bounds.point(current.x,current.y))return false;previous=current;}
  else{
   if(!have||i+2>=points.size()||points[i+1].type_!=CFX_Path::Point::Type::kBezier||points[i+2].type_!=CFX_Path::Point::Type::kBezier)return false;
   auto second=glyph.Transform(points[i+1].point_),end=glyph.Transform(points[i+2].point_);
   if(!ink_finite(second.x)||!ink_finite(second.y)||!ink_finite(end.x)||!ink_finite(end.y)||!bounds.cubic(previous,current,second,end))return false;
   previous=end;i+=2;
  }
 }
 return bounds.used;
}
InkWitness ink_witness(InkContext& context,const CFX_Font* font,uint32_t glyph_index,int width,const CFX_Path& path,const CFX_Matrix& glyph){
 CFX_Matrix linear=glyph;linear.e=linear.f=0;
 auto bits=[](float value){uint32_t n;std::memcpy(&n,&value,sizeof n);return static_cast<uint64_t>(n);};
 InkKey key{reinterpret_cast<uintptr_t>(font),glyph_index,static_cast<uint32_t>(width),bits(linear.a),bits(linear.b),bits(linear.c),bits(linear.d)};
 auto found=context.witnesses.find(key);if(found!=context.witnesses.end())return found->second;
 if(context.witnesses.size()>=1024){fail(E1_LIMIT);return {};}
 InkWitness result;InkBounds bounds;
 if(!ink_bounds(path,linear,bounds)||bounds.r<=bounds.l||bounds.t<=bounds.b){context.witnesses.emplace(key,result);return result;}
 double scale=250.0/std::max(bounds.r-bounds.l,bounds.t-bounds.b);
 if(!std::isfinite(static_cast<float>(scale))||static_cast<float>(scale)<=0)return result;
 if(!std::isfinite(scale)||scale<=0)return result;
 int w=static_cast<int>(std::ceil((bounds.r-bounds.l)*scale))+4,h=static_cast<int>(std::ceil((bounds.t-bounds.b)*scale))+4;
 if(w<4||h<4||w>256||h>256)return result;
 size_t pixels=static_cast<size_t>(w)*static_cast<size_t>(h);
 if(pixels>context.raster_cap||!context.tick(pixels)){fail(E1_LIMIT);return result;}
 auto bitmap=pdfium::MakeRetain<CFX_DIBitmap>();if(!bitmap->Create(w,h,FXDIB_Format::k8bppMask)){fail(E1_MEMORY);return result;}
 bitmap->Clear(0);auto device=CFX_RenderDevice::CreateForBitmap(bitmap);if(!device)return result;
 CFX_Path transformed(path);transformed.Transform(linear);
 CFX_Matrix viewport(static_cast<float>(scale),0,0,static_cast<float>(-scale),static_cast<float>(2-bounds.l*scale),static_cast<float>(2+bounds.t*scale));
 CFX_FillRenderOptions options=CFX_FillRenderOptions::WindingOptions();options.text_mode=true;
 if(!device->DrawPath(transformed,&viewport,nullptr,0xff000000,0,options))return result;
 int l=w,r=-1,t=h,b=-1;
 for(int y=0;y<h;++y){auto row=bitmap->GetScanline(y);for(int x=0;x<w;++x)if(row[x]){l=std::min(l,x);r=std::max(r,x);t=std::min(t,y);b=std::max(b,y);}}
 if(r>=l&&b>=t&&l>0&&t>0&&r<w-1&&b<h-1){
  double measured_l=bounds.l+(l-2)/scale,measured_r=bounds.l+(r+1-2)/scale;
  double measured_b=bounds.t-(b+1-2)/scale,measured_t=bounds.t-(t-2)/scale;
  double error=3/scale;
  if(std::abs(measured_l-bounds.l)<=error&&std::abs(measured_r-bounds.r)<=error
    &&std::abs(measured_b-bounds.b)<=error&&std::abs(measured_t-bounds.t)<=error)result={true,error};
 }
 context.witnesses.emplace(key,result);return result;
}
InkBox measure_ink(InkContext& context,Slot& slot,int index,const Geometry& geometry){
 InkBox result;
 auto* textpage=CPDFTextPageFromFPDFTextPage(slot.text);if(!textpage||index<0||static_cast<size_t>(index)>=textpage->size())return result;
 const auto& info=textpage->GetCharInfo(index);
 uint32_t unicode=info.unicode();
 // Only parsed native generation with no glyph code proves unpainted text.
 // A painted glyph may map to SPACE/NBSP/control via ToUnicode/ActualText.
 if(info.char_type()==CPDF_TextPage::CharType::kGenerated&&info.char_code()==CPDF_Font::kInvalidCharCode){result.state="missing";return result;}
 if(info.char_type()!=CPDF_TextPage::CharType::kNormal||unicode==0)return result;
 auto* object=info.text_object();auto found=context.indexes.find(object);
 if(!object||found==context.indexes.end()||context.page_group||!context.objects[found->second].black)return result;
 auto font=object->GetFont();
 auto mapped=font->UnicodeFromCharCode(info.char_code());
 if(mapped.GetLength()!=1||static_cast<uint32_t>(mapped.Front())!=unicode)return result;
 uint32_t code=info.char_code();float zero=0;float size=object->GetFontSize();
 if(!ink_finite(size)||size<=0)return result;
 auto positions=font->GetCharPosList(pdfium::span<const uint32_t>(&code,size_t{1}),pdfium::span<const float>(&zero,size_t{1}),size);
 if(positions.size()!=1)return result;const auto& position=positions[0];
 if(position.fallback_font_position_!=-1||position.glyph_index_==0||position.glyph_index_==static_cast<uint32_t>(-1))return result;
 const auto* path=font->GetFont()->LoadGlyphPath(position.glyph_index_,position.font_char_width_);
 if(!path||path->GetPoints().size()>INK_POINT_CAP)return result;
 const auto& points=path->GetPoints();if(points.empty()){result.state="missing";return result;}
 if(!context.tick(points.size()))return result;
 CFX_Matrix matrix=info.matrix();matrix.e=info.origin().x;matrix.f=info.origin().y;
 if(!ink_finite(matrix.a)||!ink_finite(matrix.b)||!ink_finite(matrix.c)||!ink_finite(matrix.d)||!ink_finite(matrix.e)||!ink_finite(matrix.f)
   ||matrix.a*matrix.d-matrix.b*matrix.c==0)return result;
 CFX_Matrix glyph=position.GetEffectiveMatrix(CFX_Matrix(size,0,0,size,position.origin_.x,position.origin_.y));glyph.Concat(matrix);
 InkBounds bounds;if(!ink_bounds(*path,glyph,bounds))return result;
 auto witness=ink_witness(context,font->GetFont(),position.glyph_index_,position.font_char_width_,*path,glyph);
 if(status!=E1_OK||!witness.available)return result;
 if(!bounds.used||bounds.r<=bounds.l||bounds.t<=bounds.b){result.state="missing";return result;}
 double coordinate_error=std::max({std::abs(bounds.l),std::abs(bounds.b),std::abs(bounds.r),std::abs(bounds.t)})*8*std::numeric_limits<float>::epsilon();
 if(coordinate_error>witness.error/4)return result;
 double error=witness.error+coordinate_error+std::max(std::abs(glyph.a)+std::abs(glyph.c),std::abs(glyph.b)+std::abs(glyph.d))/4096.0;
 if(!std::isfinite(error)||error<0||error>1000000)return result;
 // Page boxes clip painted content even when the object has no clip path.
 // Require the entire measured uncertainty envelope within the effective page.
 if(bounds.l-error<geometry.l||bounds.b-error<geometry.b||bounds.r+error>geometry.r||bounds.t+error>geometry.t)return result;
 // Later unknown graphics may hide text. Verified opaque black text cannot
 // erase black outline ink; all other intersecting objects remain uncertain.
 for(size_t i=found->second+1;i<context.objects.size();++i){
  if(!context.tick())return result;const auto& other=context.objects[i];if(!other.object->IsActive()||other.black)continue;
  // An unsupported object's reported bbox may itself depend on an untrusted
  // font/form declaration. Do not use it to prove that later paint cannot hide
  // this glyph. Rich-page overlap support needs its own measured paint bounds.
  return result;
 }
 return {bounds.l,bounds.b,bounds.r,bounds.t,true,"available",error};
}
}
