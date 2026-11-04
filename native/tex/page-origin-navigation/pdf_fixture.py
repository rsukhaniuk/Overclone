# Fixture-only decoder from accepted graphics oracle 6314243d64830aaaa29e0f1acb54bc91366fd35bf7013fab244527419bb313cb.
# Adaptations:4-page assertion and explicit bounded numeric-conversion failures.
#!/usr/bin/env python3
"""Independent actual compiled-portable PDF word/image/point oracle ONLY.
Not a product PDF parser. Decode actual page/font/content bytes before map or
geometry. Never choose a PDF point from a source-map result or inferred matrix.
"""
import json,re,zlib,hashlib,math
from pathlib import Path
from typing import Any
def integer(value,base=None):
 try:return int(value)if base is None else int(value,base)
 except (ValueError,TypeError,OverflowError)as error:raise AssertionError('invalid PDF fixture integer')from error
def scalar(value):
 try:n=float(value)
 except (ValueError,TypeError,OverflowError)as error:raise AssertionError('invalid PDF fixture number')from error
 if not math.isfinite(n):raise AssertionError('nonfinite PDF fixture number')
 return n
def required(pattern: bytes,data: bytes):
 match=re.search(pattern,data)
 if match is None:raise AssertionError('Missing actual fixture PDF field')
 return match
def load(p):
 try:return json.loads(Path(p).read_bytes())
 except (OSError,ValueError)as e:raise AssertionError('bad fixture JSON')from e
def sha(b):return hashlib.sha256(b).hexdigest()
def objects(pdf):
 o,s={},{}
 for m in re.finditer(rb'(\d+) 0 obj\s*',pdf):
  body=pdf[m.end():pdf.find(b'endobj',m.end())];mark=re.search(rb'\r?\nstream\r?\n',body);i=integer(m[1])
  if not mark:o[i]=body;continue
  h=body[:mark.start()];n=re.search(rb'/Length\s+(\d+)',h);assert n
  v=pdf[m.end()+mark.end():m.end()+mark.end()+integer(n[1])]
  if b'/FlateDecode'in h:v=zlib.decompress(v)
  o[i]=h;s[i]=v
  if re.search(rb'/Type\s*/ObjStm',h):
   f=integer(required(rb'/First\s+(\d+)',h)[1]);c=integer(required(rb'/N\s+(\d+)',h)[1]);pairs=list(map(integer,v[:f].split()));assert len(pairs)==2*c
   for j in range(c):o[pairs[j*2]]=v[f+pairs[j*2+1]:f+pairs[j*2+3]if j+1<c else len(v)]
 return o,s
def compose(a,b):return[a[0]*b[0]+a[2]*b[1],a[1]*b[0]+a[3]*b[1],a[0]*b[2]+a[2]*b[3],a[1]*b[2]+a[3]*b[3],a[0]*b[4]+a[2]*b[5]+a[4],a[1]*b[4]+a[3]*b[5]+a[5]]
def pt(m,x,y):return[m[0]*x+m[2]*y+m[4],m[1]*x+m[3]*y+m[5]]
def inv(m,x,y):
 d=m[0]*m[3]-m[1]*m[2];assert d
 return[(m[3]*(x-m[4])-m[2]*(y-m[5]))/d,(-m[1]*(x-m[4])+m[0]*(y-m[5]))/d]
def cmap(data):
 out={}
 for section in re.findall(rb'beginbfchar(.*?)endbfchar',data,re.S):
  for a,b in re.findall(rb'<([\da-fA-F]+)>\s*<([\da-fA-F]+)>',section):out[integer(a,16)]=bytes.fromhex(b.decode()).decode('utf-16-be')
 for section in re.findall(rb'beginbfrange(.*?)endbfrange',data,re.S):
  for a,b,c in re.findall(rb'<([\da-fA-F]+)>\s*<([\da-fA-F]+)>\s*<([\da-fA-F]+)>',section):
   a,b,c=[integer(x,16)for x in[a,b,c]];assert b-a<65536
   for i in range(a,b+1):out[i]=(c+i-a).to_bytes(2,'big').decode('utf-16-be')
 return out
def literal(b):
 return re.sub(rb'\\([0-7]{1,3}|.)',lambda m:bytes([integer(m[1],8)])if m[1][:1]in b'01234567'else{b'n':b'\n',b'r':b'\r',b't':b'\t'}.get(m[1],m[1]),b)
def inspect(pdf):
 obj,streams=objects(pdf)
 fonts:dict[int,dict[str,Any]]={};resources={};images={}
 for k,v in obj.items():
  if re.search(rb'/Type\s*/Font\b',v)and re.search(rb'/Subtype\s*/(?:Type0|Type1)\b',v):
   ref=re.search(rb'/ToUnicode\s+(\d+) 0 R',v);fonts[k]={'cid':bool(re.search(rb'/Subtype\s*/Type0\b',v)),'map':cmap(streams[integer(ref[1])])if ref else{},'body':v}
  for name,f in re.findall(rb'/(F\w+)\s+(\d+) 0 R',v):
   if integer(f)in obj:resources[name.decode()]=integer(f)
  if re.search(rb'/Subtype\s*/Image\b',v):images[k]=[integer(required(rb'/Width\s+(\d+)',v)[1]),integer(required(rb'/Height\s+(\d+)',v)[1])]
 # Actual page tree order, not a map-selected page or text fixture coordinate.
 trees=[v for v in obj.values()if re.search(rb'/Type\s*/Pages\b',v)];assert len(trees)==1
 ids=[integer(n)for n in re.findall(rb'(\d+) 0 R',required(rb'/Kids\s*\[([^]]+)\]',trees[0])[1])];assert len(ids)==4
 number=rb'[-+]?(?:\d+(?:\.\d*)?|\.\d+)';tok=re.compile(rb'\((?:\\[\s\S]|[^)\\])*\)|<[\da-fA-F\s]*>|/[^\s<>()\[\]]+|\[|\]|'+number+rb'|[A-Za-z\x27\x22*]+')
 observed=[];image_draws=[];pages=[]
 for pg,i in enumerate(ids,1):
  v=obj[i];box=re.search(rb'/MediaBox\s*\[([^]]+)\]',v)or re.search(rb'/MediaBox\s*\[([^]]+)\]',trees[0]);assert box;media=list(map(scalar,box[1].split()));pages.append(media)
  content_refs=re.search(rb'/Contents\s*\[([^]]+)\]',v);refs=[integer(x)for x in re.findall(rb'(\d+) 0 R',content_refs[1])]if content_refs else[integer(required(rb'/Contents\s+(\d+) 0 R',v)[1])]
  data=b' '.join(streams[n]for n in refs);matrix=[1,0,0,1,0,0];stack=[];text=[1,0,0,1,0,0]
  operands:list[Any]=[];array:list[bytes]|None=None;face:dict[str,Any]|None=None
  for match in tok.finditer(data):
   t=match.group()
   if t==b'[':assert array is None;array=[];continue
   if t==b']':assert array is not None;operands.append(array);array=None;continue
   if array is not None:array.append(t);continue
   if t[:1]in [b'/',b'(',b'<']or re.fullmatch(number,t):operands.append(t);continue
   if t==b'q':stack.append(matrix[:])
   elif t==b'Q':matrix=stack.pop()
   elif t==b'cm':matrix=compose(matrix,list(map(scalar,operands[-6:])))
   elif t==b'BT':text=[1,0,0,1,0,0]
   elif t==b'Tm':text=list(map(scalar,operands[-6:]))
   elif t==b'Td':text=compose(text,[1,0,0,1,*map(scalar,operands[-2:])])
   elif t==b'Tf':face=fonts[resources[operands[-2][1:].decode()]]
   elif t in [b'TJ',b'Tj']:
    assert face
    parts=operands[-1]if isinstance(operands[-1],list)else[operands[-1]];chars=[]
    for p in parts:
     if p.startswith(b'<'):bs=bytes.fromhex(p[1:-1].decode());step=2 if face['cid'] else 1
     elif p.startswith(b'('):bs=literal(p[1:-1]);step=2 if face['cid'] else 1
     else:continue
     assert len(bs)%step==0
     for j in range(0,len(bs),step):
      code=int.from_bytes(bs[j:j+step],'big');value=face['map'].get(code)
      if value is None:
       assert not face['cid']and any(x in face['body']for x in[b'CMR',b'CMMI',b'CMBX',b'CMTI']),face['body']
       value=chr(code)if 32<=code<127 else '?'
      chars.append(value)
    observed.append({'page':pg,'text':''.join(chars),'baseline':pt(compose(matrix,text),0,0)})
   elif t==b'Do':
    name=operands[-1][1:];matches=[integer(x)for body in obj.values()for x in re.findall(rb'/'+re.escape(name)+rb'\s+(\d+) 0 R',body)];assert matches
    image_draws.append({'page':pg,'resource':name.decode(),'dimensions':images.get(matches[0]),'quad':[pt(matrix,x,y)for x,y in[(0,0),(1,0),(1,1),(0,1)]]})
   operands=[]
  assert not stack
 assert image_draws and any(x['dimensions']==[192,192]for x in image_draws)
 return observed,pages,image_draws
