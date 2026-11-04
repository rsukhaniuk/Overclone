#!/usr/bin/env python3
"""Opt-in native origin1 AFTER accepted graphics1; private outputs only."""
import hashlib,json,sys
from pathlib import Path
out,source=map(Path,sys.argv[1:3]);a=out/'association.json'
try:v=json.loads(a.read_text())
except (OSError,ValueError) as error:raise SystemExit('invalid graphics parent association')from error
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def replace(p,x,y):
 b=p.read_bytes();x=x.encode();y=y.encode()
 if b.count(x)!=1:raise SystemExit('origin preimage mismatch: '+p.name+': '+x[:80].decode())
 p.write_bytes(b.replace(x,y))
for x in v['source']:assert sha(out/x['path'])==x['after'],x['path']
cpp=out/'oc_dvi_origin.cpp';cpp.write_bytes((out/'oc_dvi_graphics.cpp').read_bytes())
replace(cpp,'struct GeoPage { double m[6], box[4]; bool available; };','struct GeoPage { double m[6], box[4]; bool available; bool origin_seen; unsigned keys; };')
replace(cpp,'if (p.available && !geo_unsafe)','if (p.available && p.origin_seen && !geo_unsafe)')
# Final native page dictionary/extent witness. Matrix remains independently
# measured at actual BOP; never derive a matrix from page dictionary dimensions.
anchor='uint32_t oc_dvi_graphics_abi(void) { return 1; }'
replace(cpp,anchor,anchor+r'''
uint32_t oc_dvi_origin_abi(void) { return 1; }
void oc_dvi_origin_unsafe(void) { geo_unsafe=true; }
int oc_dvi_origin_key(unsigned bit) {
    if (!geo_active || geo_count>=geo_pages || !bit || (measured[geo_count].keys&bit)) {
        geo_unsafe=true;return 0;
    }
    measured[geo_count].keys|=bit;return 1;
}
void oc_dvi_origin_final(unsigned page,double llx,double lly,double urx,double ury,
                         double x0,double y0,double x1,double y1,int safe) {
    if (!page || page>geo_count || page>geo_pages || measured[page-1].origin_seen) {
        geo_unsafe=true;return;
    }
    GeoPage &p=measured[page-1];p.origin_seen=true;
    double det=p.m[0]*p.m[3]-p.m[1]*p.m[2];
    if (!safe || !p.available || !coord(llx)||!coord(lly)||!coord(urx)||!coord(ury)||
        !coord(x0)||!coord(y0)||!coord(x1)||!coord(y1)||llx>=urx||lly>=ury||x0>=x1||y0>=y1||
        p.m[0]<=0||p.m[3]>=0||p.m[1]!=0||p.m[2]!=0||!std::isfinite(det)||det==0||
        std::abs(llx-x0)>0.0050001||std::abs(lly-y0)>0.0050001||
        std::abs(urx-x1)>0.0050001||std::abs(ury-y1)>0.0050001) {geo_unsafe=true;return;}
    p.box[0]=p.box[1]=1000000;p.box[2]=p.box[3]=-1000000;
    for (double x:{llx,urx}) for (double y:{lly,ury}) {
        double h=(p.m[3]*(x-p.m[4])-p.m[2]*(y-p.m[5]))/det;
        double w=(-p.m[1]*(x-p.m[4])+p.m[0]*(y-p.m[5]))/det;
        if (!coord(h)||!coord(w)) {geo_unsafe=true;return;}
        p.box[0]=std::min(p.box[0],h);p.box[1]=std::min(p.box[1],w);
        p.box[2]=std::max(p.box[2],h);p.box[3]=std::max(p.box[3],w);
    }
}
''')
h=out/'oc_dvi.h'
replace(h,'void oc_dvi_geo_image_draw(int safe);',r'''void oc_dvi_geo_image_draw(int safe);
void oc_dvi_origin_unsafe(void);
int oc_dvi_origin_key(unsigned bit);
void oc_dvi_origin_final(unsigned page,double llx,double lly,double urx,double ury,
                         double x0,double y0,double x1,double y1,int safe);''')
special=out/'dpx-spc_pdfm.c'
replace(special,'if (++*count>2 || pdf_obj_typeof(key)!=PDF_NAME)','if (++*count>3 || pdf_obj_typeof(key)!=PDF_NAME)')
replace(special,'  if (!strcmp(name,"Rotate")) {','  if (!strcmp(name,"Rotate")) {\n    if (!oc_dvi_origin_key(2)) return 1;')
replace(special,'  if (!strcmp(name,"CropBox")) {','  if (!strcmp(name,"CropBox") || !strcmp(name,"MediaBox")) {\n    if (!oc_dvi_origin_key(!strcmp(name,"MediaBox") ? 1 : 4)) return 1;')
# Native dictionary parser otherwise silently replaces duplicate keys before
# typed page validation sees them. Retain native parse/output behavior but latch
# geometry unsafe on an actual duplicate insertion, including escaped PDF names.
p=out/'dpx-pdfparse.c';p.write_bytes((source/p.name).read_bytes())
replace(p,'    pdf_add_dict(result, key, value);','    if (pdf_add_dict(result, key, value)) oc_dvi_origin_unsafe();')
p.write_bytes(b'#define OC_DVI_INTERNAL_GEOMETRY 1\n#include "oc_dvi.h"\n'+p.read_bytes())
doc=out/'dpx-pdfdoc.c'
# The native PDF object, not a source/special scan, selects the writer box.
# Only direct bounded numeric rectangles can override the native extent. The
# existing native ROUND(...,.01) and dictionary writer still emit the PDF bytes.
helper=r'''
static int oc_origin_rect(pdf_obj *o,pdf_rect *b) {
  double v[4];int i;
  if (!o || pdf_obj_typeof(o)!=PDF_ARRAY || pdf_array_length(o)!=4) return 0;
  for(i=0;i<4;++i) {
    pdf_obj *n=pdf_get_array(o,i);
    if (!n || pdf_obj_typeof(n)!=PDF_NUMBER) return 0;
    v[i]=pdf_number_value(n);if(!isfinite(v[i])||fabs(v[i])>1000000) return 0;
  }
  if(v[0]>=v[2]||v[1]>=v[3])return 0;
  b->llx=v[0];b->lly=v[1];b->urx=v[2];b->ury=v[3];return 1;
}
'''
replace(doc,'static void\ndoc_flush_page (',helper+'\nstatic void\ndoc_flush_page (')
replace(doc,'  pdf_obj *contents_array;\n  unsigned int count;','  pdf_obj *contents_array;\n  unsigned int count;\n  int oc_explicit_media=0;')
replace(doc,'  {\n    const pdf_rect *resolved_box = (page->flags & USE_MY_MEDIABOX)',r'''  {
    pdf_obj *declared=pdf_lookup_dict(page->page_obj,"MediaBox");
    if(declared) {
      pdf_rect b;
      if(!oc_origin_rect(declared,&b))oc_dvi_origin_unsafe();
      else {page->cropbox=b;page->flags|=USE_MY_MEDIABOX;oc_explicit_media=1;}
    }
  }
  {
    const pdf_rect *resolved_box = (page->flags & USE_MY_MEDIABOX)''')
replace(doc,'    pdf_add_dict(page->page_obj, pdf_new_name("MediaBox"),  mediabox);',r'''    pdf_add_dict(page->page_obj, pdf_new_name("MediaBox"),  mediabox);
    {
      pdf_rect actual={0,0,0,0},crop;int safe=oc_origin_rect(pdf_lookup_dict(page->page_obj,"MediaBox"),&actual);
      pdf_obj *rotation=pdf_lookup_dict(page->page_obj,"Rotate");
      pdf_obj *cropped=pdf_lookup_dict(page->page_obj,"CropBox");
      if(rotation) {
        double r=pdf_obj_typeof(rotation)==PDF_NUMBER?pdf_number_value(rotation):-1;
        safe=safe&&(r==0||r==90||r==180||r==270);
      }
      if(cropped)safe=safe&&oc_origin_rect(cropped,&crop)&&(!oc_explicit_media||
        (crop.llx>=actual.llx&&crop.lly>=actual.lly&&crop.urx<=actual.urx&&crop.ury<=actual.ury));
      safe=safe&&!pdf_lookup_dict(page->page_obj,"UserUnit");
      oc_dvi_origin_final((unsigned)(page-p->pages.entries)+1,
        resolved_box->llx,resolved_box->lly,resolved_box->urx,resolved_box->ury,
        actual.llx,actual.lly,actual.urx,actual.ury,safe);
    }''')
v['originVariant']={'version':1,'capability':'oc_dvi_origin_abi()==1','graphicsRequired':1,'wrapper':sha(cpp),'header':sha(h),'recipe':sha(__file__),'nativeProvenance':'Actual BOP matrix plus final native page extent and emitted typed MediaBox; native parser duplicate insertion fails closed. No MediaBox-derived matrix.'}
for x in v['source']:x['after']=sha(out/x['path'])
v['source'].append({'path':p.name,'before':sha(source/p.name),'after':sha(p)})
a.write_text(json.dumps(v,indent=2)+'\n')
