#!/usr/bin/env python3
"""Private post-origin typed native metadata/color geometry witnesses."""
import hashlib
import json
from pathlib import Path
import sys

out, source = map(Path, sys.argv[1:3])
here = Path(__file__).resolve().parent
sha = lambda f: hashlib.sha256(Path(f).read_bytes()).hexdigest()
a = json.loads((out / 'association.json').read_text())
assert a['originVariant']['version'] == 1
for row in a['source']:
    assert sha(out / row['path']) == row['after']
def change(file, before, after):
    p = out / file
    raw = p.read_bytes()
    assert raw.count(before.encode()) == 1, (file, before[:80])
    p.write_bytes(raw.replace(before.encode(), after.encode()))
def copied(name):
    expected={'dpx-spc_color.c':'ac1c7d0d948becf459220c8ba9c08ae1f9bb7e33a258a342cc62ed66ee59d939',
              'dpx-pdfcolor.c':'92cfcc6b566d3450369fe9b9d526667be5e46eee1c5c4484e48a815692742537',
              'dpx-spc_dvipdfmx.c':'a9100bc8e1df0d4889653e189f197787610349e24651aaf42a2f9111b52111bf'}
    assert sha(source/name)==expected[name],name
    assert not (out / name).exists()
    (out / name).write_bytes((source / name).read_bytes())
    return name
header='oc_dvi.h'
change(header,'void oc_dvi_origin_unsafe(void);','void oc_dvi_origin_unsafe(void);\nvoid oc_dvi_geo_neutral(int safe);\nvoid oc_dvi_geo_neutral_end(int complete,int frame);\nint oc_dvi_neutral_color_depth(void);')
cpp='oc_dvi_origin.cpp'
change(cpp,'bool geo_unverified=false,geo_image_draw_checked=false,geo_image_draw_safe=false;','bool geo_unverified=false,geo_image_draw_checked=false,geo_image_draw_safe=false;\nbool geo_neutral_seen=false,geo_neutral_safe=false;')
change(cpp,'    geo_unverified=false;geo_image_draw_checked=false;geo_image_draw_safe=false;','    geo_unverified=false;geo_image_draw_checked=false;geo_image_draw_safe=false;\n    geo_neutral_seen=false;geo_neutral_safe=false;')
change(cpp,'uint32_t oc_dvi_origin_abi(void) { return 1; }','''uint32_t oc_dvi_origin_abi(void) { return 1; }
uint32_t oc_dvi_neutral_abi(void) { return 1; }
void oc_dvi_geo_neutral(int safe) {
    if(geo_neutral_seen){geo_unsafe=true;return;}
    geo_neutral_seen=true;geo_neutral_safe=safe!=0;
}
void oc_dvi_geo_neutral_end(int complete,int frame) {
    if(geo_neutral_seen){
        if(geo_neutral_safe&&complete&&frame)geo_unverified=false;
        else geo_unsafe=true;
    }
}''')
special='dpx-specials.c'
change(special,'  struct spc_handler special;','  struct spc_handler special;\n  pdf_tmatrix oc_before,oc_after;\n  int oc_depth=pdf_dev_current_depth();\n  pdf_dev_currentmatrix(&oc_before);')
change(special,'  oc_dvi_geo_special_end(error);','''  pdf_dev_currentmatrix(&oc_after);
  oc_dvi_geo_neutral_end(args.curptr==args.endptr,
    oc_depth==pdf_dev_current_depth() &&
    isfinite(oc_before.a)&&isfinite(oc_before.b)&&isfinite(oc_before.c)&&
    isfinite(oc_before.d)&&isfinite(oc_before.e)&&isfinite(oc_before.f)&&
    oc_before.a==oc_after.a&&oc_before.b==oc_after.b&&oc_before.c==oc_after.c&&
    oc_before.d==oc_after.d&&oc_before.e==oc_after.e&&oc_before.f==oc_after.f);
  oc_dvi_geo_special_end(error);''')
p=out/special;p.write_bytes(b'#include <math.h>\n'+p.read_bytes())
pdfm='dpx-spc_pdfm.c'
change(pdfm,'static int\nspc_handler_pdfm_annot', (here/'neutral-dictionaries.inc').read_text()+'\nstatic int\nspc_handler_pdfm_annot')
change(pdfm,'  /* Order is important... */','  oc_dvi_geo_neutral(oc_n_dictionary(annot_dict,OC_N_LINK));\n  /* Order is important... */')
change(pdfm,'  error = spc_begin_annot(spe, sd->annot_dict);','  oc_dvi_geo_neutral(oc_n_dictionary(sd->annot_dict,OC_N_LINK));\n  error = spc_begin_annot(spe, sd->annot_dict);')
change(pdfm,'  error = spc_end_annot(spe);','  oc_dvi_geo_neutral(oc_n_dictionary(sd->annot_dict,OC_N_LINK));\n  error = spc_end_annot(spe);')
change(pdfm,'  pdf_doc_bookmarks_add(item_dict, is_open);','  oc_dvi_geo_neutral(oc_n_dictionary(item_dict,OC_N_OUTLINE));\n  pdf_doc_bookmarks_add(item_dict, is_open);')
change(pdfm,'  pdf_doc_add_names("Dests",','  oc_dvi_geo_neutral(oc_n_destination(array));\n  pdf_doc_add_names("Dests",')
change(pdfm,'  docinfo = pdf_doc_docinfo();','  oc_dvi_geo_neutral(oc_n_dictionary(dict,OC_N_INFO));\n  docinfo = pdf_doc_docinfo();')
change(pdfm,'  catalog  = pdf_doc_catalog();','  oc_dvi_geo_neutral(oc_n_dictionary(dict,OC_N_VIEW));\n  catalog  = pdf_doc_catalog();')
change(pdfm,'static int\nspc_handler_pdfm_do_nothing', '''static int
oc_neutral_version(struct spc_env *spe,struct spc_arg *args) {
  pdf_obj *o=parse_pdf_object(&args->curptr,args->endptr,NULL);double n=-1;int safe=0;
  if(o&&pdf_obj_typeof(o)==PDF_NUMBER){n=pdf_number_value(o);safe=isfinite(n)&&n>=0&&n<=9&&floor(n)==n;}
  if(o)pdf_release_obj(o);
  oc_dvi_geo_neutral(safe);
  return safe?0:-1;
}
static int
spc_handler_pdfm_do_nothing''')
change(pdfm,'  {"minorversion", spc_handler_pdfm_do_nothing},','  {"minorversion", oc_neutral_version},\n  {"majorversion", oc_neutral_version},')
color=copied('dpx-spc_color.c')
p=out/color;p.write_bytes(b'#define OC_DVI_INTERNAL_GEOMETRY 1\n#include "oc_dvi.h"\n#include <math.h>\n'+p.read_bytes())
change(color,'static int\nspc_handler_color_push', '''static int oc_neutral_color(const pdf_color *c) {
  int i,type=pdf_color_type(c);
  if(type!=PDF_COLORSPACE_TYPE_DEVICEGRAY&&type!=PDF_COLORSPACE_TYPE_DEVICERGB&&type!=PDF_COLORSPACE_TYPE_DEVICECMYK)return 0;
  if(c->num_components!=1&&c->num_components!=3&&c->num_components!=4)return 0;
  for(i=0;i<c->num_components;++i)if(!isfinite(c->values[i])||c->values[i]<0||c->values[i]>1)return 0;
  return 1;
}
static int
spc_handler_color_push''')
change(color,'    pdf_color_push(&colorspec, &colorspec);','    int before=oc_dvi_neutral_color_depth();\n    pdf_color_push(&colorspec, &colorspec);\n    oc_dvi_geo_neutral(oc_neutral_color(&colorspec)&&oc_dvi_neutral_color_depth()==before+1);')
change(color,'  pdf_color_pop();','  int before=oc_dvi_neutral_color_depth();\n  pdf_color_pop();\n  oc_dvi_geo_neutral(before>0&&oc_dvi_neutral_color_depth()==before-1);')
change(color,'    pdf_color_set(&colorspec, &colorspec);','    pdf_color_set(&colorspec, &colorspec);\n    oc_dvi_geo_neutral(oc_neutral_color(&colorspec)&&oc_dvi_neutral_color_depth()==0);')
colors=copied('dpx-pdfcolor.c');p=out/colors;p.write_bytes(p.read_bytes()+b'\nint oc_dvi_neutral_color_depth(void){return color_stack.current;}\n')
config=copied('dpx-spc_dvipdfmx.c');p=out/config;p.write_bytes(b'#define OC_DVI_INTERNAL_GEOMETRY 1\n#include "oc_dvi.h"\n'+p.read_bytes())
change(config,'  args->curptr = args->endptr;','  args->curptr = args->endptr;\n  oc_dvi_geo_neutral(1); /* exact pinned native config handler and prescan both ignore it */')
for row in a['source']:
    row['after']=sha(out/row['path'])
for name in [color,colors,config]:
    a['source'].append({'path':name,'before':sha(source/name),'after':sha(out/name)})
a['neutralVariant']={'version':1,'capability':'oc_dvi_neutral_abi()==1','recipe':sha(__file__),'dictionaryValidator':sha(here/'neutral-dictionaries.inc'),'wrapper':sha(out/cpp),'header':sha(out/header),'domain':'Actual native parsed metadata/link/destination/outline objects and DeviceGray/RGB/CMYK stack operations, complete consumption, unchanged native CTM and save depth; prior unsafe latch never cleared. Arbitrary programs/objects/page dictionaries remain refused.'}
(out/'association.json').write_text(json.dumps(a,indent=2)+'\n')
