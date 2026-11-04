#!/usr/bin/env python3
"""Fixture-only final PDF/word/image oracle; never product parsing/geometry.
Decode actual final PDF BEFORE reading sidecars/maps or querying native nodes.
Reuse the hash-bound accepted graphics oracle's independent byte decoder.
"""
import hashlib,json,os,re,subprocess
from pathlib import Path
here=Path(__file__).resolve().parent;parent=here.parent/'graphics-navigation/pdf-query-oracle.py'
b=parent.read_bytes()
if hashlib.sha256(b).hexdigest()!='6314243d64830aaaa29e0f1acb54bc91366fd35bf7013fab244527419bb313cb':raise SystemExit('accepted independent PDF decoder drift')
import pdf_fixture as decoder
ns={name:getattr(decoder,name)for name in ['inspect','objects','pt','inv']}
root=Path(os.environ['RESULTS']);root.mkdir(parents=True,exist_ok=True)
query=Path('/inputs/generators/texk/web2c/synctex')
env={k:v for k,v in os.environ.items()if k not in ['SYNCTEX_EDITOR','SYNCTEX_VIEWER']};results=[]
def sha(b):return hashlib.sha256(b).hexdigest()
def load(p):
 try:return json.loads(Path(p).read_bytes())
 except (OSError,ValueError)as error:raise AssertionError('invalid native fixture receipt')from error
cases=load(os.environ['CASES'])
for case in cases:
 folder=Path(case['folder']);pdf=(folder/'output.pdf').read_bytes()
 # Actual native PDF content, fonts, matrices and final page boxes FIRST.
 observed,media,draws=ns['inspect'](pdf);obj,streams=ns['objects'](pdf)
 page_objs=[v for v in obj.values()if re.search(rb'/Type\s*/Page\b',v)]
 assert len(page_objs)==4
 trees=[v for v in obj.values()if re.search(rb'/Type\s*/Pages\b',v)];assert len(trees)==1
 # These positive fixtures use no inherited crop/rotation/unit channels. DVI
 # natively emits a root default MediaBox, overridden by every actual Page's
 # explicit box below; do not confuse that native default with Lua attributes.
 assert all(not re.search(rb'/(?:CropBox|Rotate|UserUnit)\b',t)for t in trees),'unproved inherited root page attributes'
 ids=[decoder.integer(i)for i in re.findall(rb'(\d+) 0 R',decoder.required(rb'/Kids\s*\[([^]]+)\]',trees[0])[1])]
 views=[]
 for p in [obj[i]for i in ids]:
  assert len(re.findall(rb'/MediaBox\b',p))==1,'ambiguous actual emitted MediaBox'
  assert not re.search(rb'/UserUnit\b',p),'native default PDF user space required'
  crop=re.findall(rb'/CropBox\s*\[([^]]+)\]',p);rotation=re.findall(rb'/Rotate\s+([-\d.]+)',p)
  assert len(crop)<=1 and len(rotation)<=1
  views.append({'crop':list(map(decoder.scalar,crop[0].split()))if crop else None,'rotation':decoder.scalar(rotation[0])if rotation else 0})
 if case.get('view'):
  assert all(v['crop']is not None for v in views)
  assert {v['rotation']for v in views}=={0,90,180,270}
 assert any(p[0]>0 and p[1]>0 for p in media)and any(p[0]<0 and p[1]<0 for p in media)
 assert len(draws)==4 and all(d['dimensions']==[192,192]for d in draws)
 assert all(p[0]<p[2]and p[1]<p[3]for p in media)
 # Only AFTER final-PDF observations read the actual selected native map/facts.
 geometry=load(folder/'geometry.json');raw=Path(case['map']).read_bytes();project=Path(case['project'])
 source=(project/'тези/my thesis.tex').read_text().splitlines();work=root/case['name'];work.mkdir(exist_ok=True)
 (work/'output.pdf').write_bytes(pdf);(work/'output.synctex').write_bytes(raw)
 words=[];forward_cache={};forward_calls=0
 for word,file in [('ROOTBEFORE','тези/my thesis.tex'),('ROOTAFTER','тези/my thesis.tex'),('INCLUDEMARK','глави/chapter one.tex'),('LEFTEDGE','тези/my thesis.tex'),('RIGHTEDGE','тези/my thesis.tex')]:
  found=[p for p in observed if p['text'].startswith(word)];assert len(found)==4,f'{word}: {observed}'
  # Source-line expectations are established BEFORE inverse queries from the
  # independent fixture source. These are single-line multiword hbox anchors;
  # exact line (zero tolerance), NOT a range inferred from an inverse answer.
  source_bytes=(project/file).read_bytes()
  source_lines=source_bytes.decode('utf8').splitlines()
  expected=[i for i,t in enumerate(source_lines,1)if word in t]
  if file.startswith('тези/'):assert len(expected)==4;lines=expected
  else:assert len(expected)==1;lines=expected*4
  source_sha=sha(source_bytes)
  assert len(lines)==4
  for actual,line in zip(found,lines):
   pg=actual['page'];m=geometry['pages'][pg-1]['writerToPdfRaw'];assert m
   x,y=actual['baseline'];box=media[pg-1]
   crop=views[pg-1]['crop']or box;rotation=views[pg-1]['rotation'];assert rotation in [0,90,180,270]
   assert box[0]<=crop[0]<crop[2]<=box[2]and box[1]<=crop[1]<crop[3]<=box[3]
   assert crop[0]<=x<=crop[2]and crop[1]<=y<=crop[3],f'actual PDF marker clipped: {actual}: {crop}'
   # Independent intrinsic PDF viewport semantics, not browser/CSS evidence.
   if rotation==0:display=[x-crop[0],crop[3]-y];back=[display[0]+crop[0],crop[3]-display[1]]
   elif rotation==90:display=[y-crop[1],x-crop[0]];back=[display[1]+crop[0],display[0]+crop[1]]
   elif rotation==180:display=[crop[2]-x,y-crop[1]];back=[crop[2]-display[0],display[1]+crop[1]]
   else:display=[crop[3]-y,crop[2]-x];back=[crop[2]-display[1],crop[3]-display[0]]
   assert max(abs(a-b)for a,b in zip(back,[x,y]))<1e-9
   request=(file,line)
   if request not in forward_cache:
    p=subprocess.run([str(query),'view','-i',f'{line}:1:{file}','-o',str(work/'output.pdf')],capture_output=True,text=True,check=True,env=env)
    forward_cache[request]=p.stdout+p.stderr;forward_calls+=1
   forward=forward_cache[request]
   (work/f'{word}-{pg}-forward.txt').write_text(forward);matches=[]
   for block in re.split(r'(?=^Page:)',forward,flags=re.M):
    f={k:decoder.scalar(v)for k,v in re.findall(r'^(Page|x|y|h|v|W|H):([-\d.]+)$',block,re.M)}
    if not {'Page','h','y','W'}<=set(f):continue
    # Repeated inclusion of the same native source line has no per-occurrence
    # forward selector in query ABI1. Validate its actual chosen PDF occurrence,
    # never pretend the request designated the page used by an inverse click.
    targets=[t for t in found if t['page']==f['Page']and(len(set(lines))<4 or t['page']==pg)]
    for target in targets:
     target_m=geometry['pages'][target['page']-1]['writerToPdfRaw'];assert target_m
     a=ns['pt'](target_m,f['h'],f['y']);b=ns['pt'](target_m,f['h']+f['W'],f['y']);tx,ty=target['baseline']
     if abs(a[1]-ty)<.004 and min(a[0],b[0])-.004<=tx<=max(a[0],b[0])+.004:
      matches.append({'nativeQuery':f,'actualPdfTarget':target,'sameOccurrenceAsInverse':target['page']==pg})
   assert matches,f'{case["name"]}: {word}: {actual}: {forward}'
   u,v=ns['inv'](m,x+2,y)
   p=subprocess.run([str(query),'edit','-o',f'{pg}:{u}:{v}:{work/"output.pdf"}'],capture_output=True,text=True,check=True,env=env)
   (work/f'{word}-{pg}-inverse.txt').write_text(p.stdout+p.stderr)
   allowed={file,'./'+file,'/work/'+file,'/work/project/'+file,'/work/project/./'+file};inverse=[]
   for block in re.split(r'(?=^Input:)',p.stdout,flags=re.M):
    name=re.search(r'^Input:(.*)$',block,re.M)
    if name is None:continue
    native_line=re.search(r'^Line:(\d+)$',block,re.M)
    assert native_line is not None,f'inverse result missing bound Line: {block}'
    inverse.append({'file':name[1],'line':decoder.integer(native_line[1])})
   assert inverse and all(t['file']in allowed and t['line']==line for t in inverse),f'{word}: expected {file}:{line}, tolerance0; {p.stdout}'
   words.append({'actualPdf':actual,'word':word,'sourceFile':file,'requestedLine':line,'forward':matches,'inverseNames':[t['file']for t in inverse],'inverseLines':[t['line']for t in inverse],'inverseResults':inverse,'expectedInverse':{'file':file,'sourceSha256':source_sha,'line':line,'toleranceLines':0,'basis':'independent single-line multiword fixture source anchor, established before native inverse query'},'actualCrop':crop,'actualRotation':rotation,'intrinsicDisplayPoint':display,'semantics':'exact source-line assertion for these anchors; no general token/column or unboxed/no-glue precision claim; intrinsic PDF coordinates, not browser/CSS proof'})
 extents=[]
 for box,g in zip(media,geometry['pages']):
  assert g['status']=='available';v=g['writerPageBoxBp'];m=g['writerToPdfRaw']
  pts=[ns['pt'](m,x,y)for x in [v[0],v[2]]for y in[v[1],v[3]]]
  actual=[min(p[0]for p in pts),min(p[1]for p in pts),max(p[0]for p in pts),max(p[1]for p in pts)]
  assert max(abs(a-b)for a,b in zip(actual,box))<.0051,f'{actual}: {box}'
  extents.append({'page':g['page'],'actualPdfBox':box,'nativeExtentTransformed':actual})
 results.append({'name':case['name'],'pdfSha256':sha(pdf),'mapSha256':sha(raw),'actualMediaBoxes':media,'actualViews':views,'actualImageDraws':draws,'words':words,'extents':extents,'actualForwardCalls':forward_calls,'actualInverseCalls':len(words)})
(root/'oracle.json').write_text(json.dumps({'schemaVersion':1,'status':'pass','results':results,'querySha256':sha(query.read_bytes()),'parentDecoderSha256':sha(parent.read_bytes()),'decoderSha256':sha((here/'pdf_fixture.py').read_bytes()),'scope':'Synthetic actual final-PDF word baselines/image draws/native geometry and queries. No ink/physical/CSS/browser or consumer-profile acceptance.'},ensure_ascii=False,indent=2)+'\n')
print('actual final native-origin PDF boxes/words/images, source queries and writer extents pass')
