/* Candidate-only geometry-neutral native PDF object validation.
 * Inputs are ACTUAL objects produced by the native PDF parser. No source,
 * package-name or special-prefix classifier supplies navigation authority.
 */
#include <math.h>
enum { OC_N_INFO=1, OC_N_VIEW, OC_N_OUTLINE, OC_N_LINK, OC_N_ACTION };
struct oc_n_context { unsigned kind,count,seen,action; };
static int oc_n_name(pdf_obj *o,const char *name) {
  return o && pdf_obj_typeof(o)==PDF_NAME && !strcmp(pdf_name_value(o),name);
}
static int oc_n_number(pdf_obj *o,int nullable) {
  double n;
  if(nullable && o && pdf_obj_typeof(o)==PDF_NULL)return 1;
  if(!o || pdf_obj_typeof(o)!=PDF_NUMBER)return 0;
  n=pdf_number_value(o);return isfinite(n)&&fabs(n)<=1000000;
}
static int oc_n_array(pdf_obj *o,unsigned min,unsigned max,int unit) {
  unsigned i,n;
  if(!o || pdf_obj_typeof(o)!=PDF_ARRAY)return 0;
  n=pdf_array_length(o);if(n<min||n>max)return 0;
  for(i=0;i<n;++i){pdf_obj *v=pdf_get_array(o,i);double x;
    if(!oc_n_number(v,0))return 0;x=pdf_number_value(v);
    if(x<0||(unit&&x>1))return 0;
  }
  return 1;
}
static int oc_n_destination(pdf_obj *o) {
  unsigned i,n;pdf_obj *mode;
  if(!o || pdf_obj_typeof(o)!=PDF_ARRAY)return 0;
  n=pdf_array_length(o);if(n<2||pdf_obj_typeof(pdf_get_array(o,0))!=PDF_INDIRECT)return 0;
  mode=pdf_get_array(o,1);
  if(oc_n_name(mode,"Fit"))return n==2;
  if(!oc_n_name(mode,"XYZ")||n!=5)return 0;
  for(i=2;i<5;++i)if(!oc_n_number(pdf_get_array(o,i),1))return 0;
  return 1;
}
static int oc_n_dictionary(pdf_obj *,unsigned);
static int oc_n_key(pdf_obj *key,pdf_obj *value,void *opaque) {
  struct oc_n_context *c=(struct oc_n_context*)opaque;const char *k;
  if(++c->count>16||pdf_obj_typeof(key)!=PDF_NAME)return 1;
  k=pdf_name_value(key);
  if(c->kind==OC_N_INFO) {
    if(strcmp(k,"Title")&&strcmp(k,"Subject")&&strcmp(k,"Creator")&&strcmp(k,"Author")&&
       strcmp(k,"Keywords")&&strcmp(k,"Producer")&&strcmp(k,"CreationDate")&&strcmp(k,"ModDate"))return 1;
    return pdf_obj_typeof(value)!=PDF_STRING||pdf_string_length(value)>4096;
  }
  if(c->kind==OC_N_VIEW) {
    if(!strcmp(k,"OpenAction"))return !oc_n_destination(value);
    if(!strcmp(k,"PageMode"))return !(oc_n_name(value,"UseNone")||oc_n_name(value,"UseOutlines")||oc_n_name(value,"UseThumbs"));
    return 1;
  }
  if(c->kind==OC_N_ACTION) {
    if(!strcmp(k,"S")){c->seen|=1;c->action=oc_n_name(value,"GoTo")?1:oc_n_name(value,"URI")?2:0;return !c->action;}
    if(!strcmp(k,"D")){c->seen|=2;return pdf_obj_typeof(value)!=PDF_STRING;}
    if(!strcmp(k,"URI")){c->seen|=4;return pdf_obj_typeof(value)!=PDF_STRING;}
    return 1;
  }
  if(!strcmp(k,"A")){c->seen|=1;return !oc_n_dictionary(value,OC_N_ACTION);}
  if(c->kind==OC_N_OUTLINE) {
    if(!strcmp(k,"Title")){c->seen|=2;return pdf_obj_typeof(value)!=PDF_STRING;}
    return 1;
  }
  if(c->kind==OC_N_LINK) {
    if(!strcmp(k,"Type")){c->seen|=2;return !oc_n_name(value,"Annot");}
    if(!strcmp(k,"Subtype")){c->seen|=4;return !oc_n_name(value,"Link");}
    if(!strcmp(k,"Border"))return !oc_n_array(value,3,3,0);
    if(!strcmp(k,"C")){unsigned n=pdf_obj_typeof(value)==PDF_ARRAY?pdf_array_length(value):0;return (n!=1&&n!=3&&n!=4)||!oc_n_array(value,1,4,1);}
    if(!strcmp(k,"H"))return !(oc_n_name(value,"I")||oc_n_name(value,"N")||oc_n_name(value,"O")||oc_n_name(value,"P"));
  }
  return 1;
}
static int oc_n_dictionary(pdf_obj *o,unsigned kind) {
  struct oc_n_context c={kind,0,0,0};
  if(!o||pdf_obj_typeof(o)!=PDF_DICT||pdf_foreach_dict(o,oc_n_key,&c))return 0;
  if(kind==OC_N_ACTION)return (c.action==1&&c.seen==3)||(c.action==2&&c.seen==5);
  if(kind==OC_N_OUTLINE)return c.seen==3;
  if(kind==OC_N_LINK)return c.seen==7;
  return 1;
}
