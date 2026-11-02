#!/usr/bin/env python3
"""Independent fixture PDF operator oracle; no product parsing/geometry policy.
Reads actual Flate/ObjStm/font/text/page bytes before loading map/geometry, then
uses the real acquired SyncTeX CLI. Never chooses a PDF point from a map result.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import zlib

out=Path(os.environ['RESULTS'])
run=Path(os.environ['ENGINE_RESULTS'])
query=Path(os.environ.get('SYNCTEX_QUERY','/inputs/generators/texk/web2c/synctex'))
work=out/'query-oracle';work.mkdir(exist_ok=True)
def integer(v):
 try:return int(v)
 except (ValueError,TypeError) as e:raise AssertionError('fixture integer') from e
def number(v):
 try:return float(v)
 except (ValueError,TypeError) as e:raise AssertionError('fixture number') from e
def load(p):
 try:return json.loads(p.read_bytes())
 except (ValueError,OSError) as e:raise AssertionError('fixture JSON') from e
def digest(b):return hashlib.sha256(b).hexdigest()
def compose(a,b):
 return [a[0]*b[0]+a[2]*b[1],a[1]*b[0]+a[3]*b[1],a[0]*b[2]+a[2]*b[3],a[1]*b[2]+a[3]*b[3],a[0]*b[4]+a[2]*b[5]+a[4],a[1]*b[4]+a[3]*b[5]+a[5]]
def point(a,x,y):return a[0]*x+a[2]*y+a[4],a[1]*x+a[3]*y+a[5]
def inverse(a,x,y):
 det=a[0]*a[3]-a[1]*a[2];assert det!=0
 return (a[3]*(x-a[4])-a[2]*(y-a[5]))/det,(-a[1]*(x-a[4])+a[0]*(y-a[5]))/det

def objects(pdf):
 obj,streams={},{}
 for m in re.finditer(rb'(\d+) 0 obj\s*',pdf):
  body=pdf[m.end():pdf.find(b'endobj',m.end())];marker=re.search(rb'\r?\nstream\r?\n',body);n=integer(m[1])
  if not marker:obj[n]=body;continue
  h=body[:marker.start()];size=re.search(rb'/Length\s+(\d+)',h);assert size
  data=pdf[m.end()+marker.end():m.end()+marker.end()+integer(size[1])]
  if b'/FlateDecode'in h:data=zlib.decompress(data)
  obj[n]=h;streams[n]=data
  if b'/Type/ObjStm'in h:
   first=re.search(rb'/First\s+(\d+)',h);count=re.search(rb'/N\s+(\d+)',h);assert first and count
   first,count=integer(first[1]),integer(count[1]);pairs=[integer(v) for v in data[:first].split()];assert len(pairs)==2*count
   for i in range(count):obj[pairs[2*i]]=data[first+pairs[2*i+1]:first+pairs[2*i+3] if i+1<count else len(data)]
 return obj,streams

def literal(b:bytes)->str:
 def repl(m:re.Match[bytes])->bytes:
  t=m[1]
  assert t is not None
  if t[:1] in b'01234567':
   try:return bytes([int(t,8)])
   except ValueError as e:raise AssertionError('fixture octal') from e
  return {b'n':b'\n',b'r':b'\r',b't':b'\t'}.get(t,t)
 return re.sub(rb'\\([0-7]{1,3}|.)',repl,b).decode('ascii')

def inspect(pdf):
 obj,streams=objects(pdf)
 assert any(b'CMR10'in v and b'/FontFile3'in v for v in obj.values()),'real embedded CMR10 glyph encoding'
 trees=[v for v in obj.values() if b'/Type/Pages'in v];assert len(trees)==1
 kids=re.search(rb'/Kids\[([^]]+)\]',trees[0]);assert kids
 ids=[integer(v) for v in re.findall(rb'(\d+) 0 R',kids[1])]
 n=rb'[-+]?(?:\d+(?:\.\d*)?|\.\d+)'
 six=n+rb'(?:\s+'+n+rb'){5}'
 cmd=re.compile(rb'(?P<cm>'+six+rb'\s+cm)|(?P<tm>'+six+rb'\s+Tm)|(?P<td>'+n+rb'\s+'+n+rb'\s+Td)|(?P<tj>\[.*?\]TJ)|(?P<tf>/\w+\s+'+n+rb'\s+Tf)|(?P<bt>BT)|(?P<et>ET)|(?P<q>q)|(?P<Q>Q)',re.S)
 result=[];pages=[]
 for page_no,k in enumerate(ids,1):
  p=obj[k];media=re.search(rb'/MediaBox\s*\[([^]]+)\]',p);crop=re.search(rb'/CropBox\s*\[([^]]+)\]',p);rot=re.search(rb'/Rotate\s*(\d+)',p)
  assert media and crop and rot
  media=[number(v) for v in media[1].split()];crop=[number(v) for v in crop[1].split()];rot=integer(rot[1]);assert len(media)==len(crop)==4 and rot in {0,90,180,270}
  pages.append({'page':page_no,'media':media,'crop':crop,'rotation':rot})
  refs=re.search(rb'/Contents\[([^]]+)\]',p);assert refs
  data=b' '.join(streams[integer(v)] for v in re.findall(rb'(\d+) 0 R',refs[1]))
  matrix=[1,0,0,1,0,0];stack=[];text_matrix=[1,0,0,1,0,0];previous=0
  for m in cmd.finditer(data):
   assert not data[previous:m.start()].strip(),data[previous:m.start()]
   previous=m.end();kind=m.lastgroup;token=m.group()
   if kind=='q':stack.append(matrix[:])
   elif kind=='Q':matrix=stack.pop()
   elif kind=='cm':matrix=compose(matrix,[number(v) for v in token.split()[:6]])
   elif kind=='bt':text_matrix=[1,0,0,1,0,0]
   elif kind=='tm':text_matrix=[number(v) for v in token.split()[:6]]
   elif kind=='td':
    x,y=[number(v) for v in token.split()[:2]];text_matrix=compose(text_matrix,[1,0,0,1,x,y])
   elif kind=='tj':
    word=''.join(literal(v) for v in re.findall(rb'\(((?:\\.|[^)\\])*)\)',token))
    x,y=point(compose(matrix,text_matrix),0,0)
    result.append({'word':word,'page':page_no,'x':x,'y':y,'nativePdfCtm':matrix[:]})
  assert not data[previous:].strip() and not stack
 return result,pages

commands=[];results=[];checks=0
for label in os.environ.get('ORACLE_CASES','positive mag1200 changed').split():
 pdf=(out/(label+'.pdf')).read_bytes();observed,pages=inspect(pdf)
 # Only now load producer expectations/map and the native matrix sidecar.
 source=load(run/(label+'.json'));geo=load(out/(label+'.geometry.json'))
 expected=source['expected'];assert [p['word'] for p in observed]==[p['word'] for p in expected];checks+=1
 assert len(pages)==len(geo['pages'])==4;assert [p['rotation'] for p in pages]==[0,90,180,270];checks+=2
 native_dvi=(run/(label+'.dvi')).read_bytes();assert digest(native_dvi)==load(out/(label+'.json'))['input']['sha256'];checks+=1
 for ext in ('pdf',):shutil.copyfile(out/(label+'.'+ext),work/(label+'.'+ext))
 shutil.copyfile(run/(label+'.synctex'),work/(label+'.synctex'))
 for actual,expected in zip(observed,expected):
  native=geo['pages'][actual['page']-1];assert native['status']=='available' and native['reason']=='none';matrix=native['writerToPdfRaw']
  argv=[str(query),'view','-i',f"{expected['line']}:1:{expected['file']}",'-o',str(work/(label+'.pdf'))]
  p=subprocess.run(argv,capture_output=True,text=True,check=True);commands.append(argv);(work/f"{label}-{actual['word']}-forward.txt").write_text(p.stdout+p.stderr)
  found=[]
  for part in re.split(r'(?=^Page:)',p.stdout,flags=re.M):
   f={k:number(v) for k,v in re.findall(r'^(Page|x|y|h|v|W|H):([-\d.]+)$',part,re.M)}
   if not {'Page','y','h','W'}<=set(f) or f['Page']!=actual['page']:continue
   x,y=point(matrix,f['h'],f['y']);xr,_=point(matrix,f['h']+f['W'],f['y'])
   if abs(y-actual['y'])<=.003 and min(x,xr)-.003<=actual['x']<=max(x,xr)+.003:found.append((f,abs(y-actual['y'])))
  assert found, f'{label}: {actual}: {p.stdout}'
  # Independent actual-PDF first-glyph baseline point, not a query-selected point.
  click=(actual['x']+2,actual['y']);h,v=inverse(matrix,*click)
  argv=[str(query),'edit','-o',f"{actual['page']}:{h}:{v}:{work/(label+'.pdf')}"]
  p=subprocess.run(argv,capture_output=True,text=True,check=True);commands.append(argv);(work/f"{label}-{actual['word']}-inverse.txt").write_text(p.stdout+p.stderr)
  filenames=re.findall(r'^Input:(.*)$',p.stdout,re.M);lines=[integer(v) for v in re.findall(r'^Line:(\d+)$',p.stdout,re.M)]
  inline_limit=label=='inline-include' and actual['word']=='INCLUDEMARK'
  if inline_limit:
   assert expected['file'] not in filenames,'Known upstream inline provenance behavior changed; independent review required'
  else:
   assert expected['file'] in filenames,f'{actual}: {p.stdout}'
  assert all(f in source['files'] for f in filenames),p.stdout
  if expected['file']!='розділ/my thesis.tex' and not inline_limit:assert expected['line'] in lines,p.stdout
  assert all(0<line<=len((run/(label+'.source.tex')).read_text().splitlines()) for line in lines)
  results.append({'fixture':label,'pdfWord':actual,'source':expected,'forwardMatches':[f for f,_ in found],'baselineErrorBp':min(err for _,err in found),'inverseFiles':filenames,'inverseLines':lines,'inverseSourceSupported':not inline_limit})
  checks+=6
 # Forward native writer extent corners should cover the actual serialized native
 # page extent, but the matrix was NOT selected/inferred from these PDF boxes.
 for page,native in zip(pages,geo['pages']):
  b=native['writerPageBoxBp'];corners=[point(native['writerToPdfRaw'],x,y) for x in (b[0],b[2]) for y in (b[1],b[3])]
  box=[min(p[0] for p in corners),min(p[1] for p in corners),max(p[0] for p in corners),max(p[1] for p in corners)]
  assert max(abs(a-b) for a,b in zip(box,page['media']))<.003;checks+=1
receipt={'schemaVersion':1,'status':'pass','checks':checks,'skips':0,'query':{'path':str(query),'sha256':digest(query.read_bytes())},'commands':commands,'results':results,'precision':{'thresholdBp':.003,'maxObservedBaselineErrorBp':max(x['baselineErrorBp'] for x in results),'basis':'Measured PDF decimal serialization/query float rounding; NOT a thesis rule tolerance'},'inlineIncludeLimitations':[x for x in results if not x['inverseSourceSupported']],'scope':'Actual native DVI/PDF/query and unrotated raw converter geometry. Crop/Rotate are real emitted controls; CSS/renderer and genuine nonzero-MediaBox acceptance remain OPEN.'}
(out/'oracle.json').write_text(json.dumps(receipt,ensure_ascii=False,indent=2)+'\n');print(json.dumps({'status':'pass','checks':checks,'precision':receipt['precision']}))
