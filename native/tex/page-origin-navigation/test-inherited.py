#!/usr/bin/env python3
"""Actual Lua root/page dictionary inheritance controls. Observe emitted PDF
bytes before geometry; no source-declared effective crop/rotation inference.
Legacy no-explicit-origin cases compare complete bytes to immutable graphics1.
"""
import hashlib,json,os,re,subprocess,sys
from pathlib import Path
from pdf_fixture import objects,integer
here=Path(__file__).resolve().parent;root=Path(os.environ['RESULTS'])
if root.exists():raise SystemExit('fresh inherited results required')
root.mkdir(parents=True);candidate=os.environ['CANDIDATE'];commands=[];rows=[];oracle_cases=[]
tex_crop=r'\pdfvariable pagesattr{/CropBox [-10 -10 700 900]}'
lua_crop=r'\directlua{pdf.setpagesattributes("/CropBox [-10 -10 700 900]")}'
tex_rotate=r'\pdfvariable pagesattr{/Rotate 0}'
lua_rotate=r'\directlua{pdf.setpagesattributes("/Rotate 90")}'
empty=r'\pdfvariable pagesattr{}\directlua{pdf.setpagesattributes("")}'
# name, early native setup, late native setup, expected available, fixture flags.
cases=[
 ('empty-both',empty,'',True,[]),
 ('empty-both-page-table',empty,'',True,['--lua-table']),
 ('empty-both-page-view',empty,'',True,['--view']),
 ('reset-before-emission',tex_crop+lua_crop+empty,'',True,[]),
 ('reset-after-pages',tex_crop+lua_crop,empty,True,[]),
 ('lua-root-outside',lua_crop,'',False,[]),
 ('tex-root-outside',tex_crop,'',False,[]),
 ('lua-root-contained',r'\directlua{pdf.setpagesattributes("/CropBox [40 40 550 740]")}', '',False,[]),
 ('tex-root-contained',r'\pdfvariable pagesattr{/CropBox [40 40 550 740]}','',False,[]),
 ('lua-root-rotate',lua_rotate,'',False,[]),
 ('tex-root-rotate',tex_rotate,'',False,[]),
 ('root-crop-cross-channel',tex_crop+lua_crop,'',False,[]),
 ('root-rotate-cross-channel',tex_rotate+lua_rotate,'',False,[]),
 ('root-mixed-channels',tex_crop+lua_rotate,'',False,[]),
 ('root-overridden-page-view',tex_crop+lua_rotate,'',False,['--view']),
 ('late-root-after-pages','',lua_crop,False,[]),
 ('late-duplicate-root-after-pages','',tex_rotate+lua_rotate,False,[]),
 ('root-media',r'\directlua{pdf.setpagesattributes("/MediaBox [-10 -10 700 900]")}','',False,[]),
 ('root-userunit',r'\pdfvariable pagesattr{/UserUnit 2}','',False,[]),
]
legacy=[
 ('legacy-lua-crop',lua_crop),('legacy-tex-crop',tex_crop),
 ('legacy-lua-rotate',lua_rotate),('legacy-tex-rotate',tex_rotate),
 ('legacy-crop-cross-channel',tex_crop+lua_crop),
 ('legacy-rotate-cross-channel',tex_rotate+lua_rotate),
 ('legacy-mixed-channels',tex_crop+lua_rotate),
]
def run(label,cmd,env=None):
 p=subprocess.run(list(map(str,cmd)),env=env,capture_output=True,text=True,timeout=60)
 (root/(label+'.log')).write_text(p.stdout+p.stderr);commands.append({'label':label,'argv':list(map(str,cmd)),'exit':p.returncode})
 assert p.returncode==0,f'{label}: setup/native failure; diagnose instead of counting inherited refusal'
def load(p):
 try:return json.loads(Path(p).read_bytes())
 except (OSError,ValueError)as error:raise AssertionError('invalid actual inherited receipt')from error
def sha(b):return hashlib.sha256(b).hexdigest()
def execute(name,project,directory):
 out=root/name
 run(name,['node','--require',here.parent/'synctex-fixtures/dvi/fixed-clock.cjs',here/'run-project.cjs','lua','latex',out],dict(os.environ,PROJECT=str(project),REQUIRE_ORIGIN='0',LUA_CANDIDATE=str(directory)))
 j=load(out/'receipt.json');assert j['nativeReturn']==0 and j['rawMap']['length']>0
 pdf=(out/'output.pdf').read_bytes();obj,_=objects(pdf)
 roots=[{'object':k,'bytes':v.decode('latin1')}for k,v in obj.items()if re.search(rb'/Type\s*/Pages\b',v)]
 pages=[{'object':k,'bytes':v.decode('latin1')}for k,v in obj.items()if re.search(rb'/Type\s*/Page\b',v)]
 assert len(roots)==1 and len(pages)==4
 # Only now read optional native geometry, after actual dictionary inspection.
 g=load(out/'geometry.json');assert len(g['pages'])==4
 return out,j,g,roots,pages
for name,early,late,available,flags in cases:
 project=root/(name+'-project');run(name+'-prepare',['python3',here/'make-project.py','--kind','lua','--out',project,'--extra-tex',early,'--final-tex',late,*flags])
 out,j,g,rs,ps=execute(name,project,candidate)
 expected='available'if available else'unsupported'
 assert all(p['status']==expected for p in g['pages']),f'{name}: inherited domain not enforced'
 if not available:assert all(p['writerToPdfRaw']is None and p['writerPageBoxBp']is None for p in g['pages'])
 assert all('/MediaBox' in p['bytes']for p in ps)
 if available:
  assert all(not re.search(r'/(?:MediaBox|CropBox|Rotate|UserUnit)\b',p['bytes'])for p in rs),'empty/reset positive emitted inherited attributes'
  parent,_,_,_,_=execute(name+'-prior-origin',project,'/control/r2-page-origins/candidate/lua-origin1')
  for f in ['output.pdf','output.log','raw.synctex','geometry.json']:assert (out/f).read_bytes()==(parent/f).read_bytes(),f'{name}: positive byte regression: {f}'
  oracle_cases.append({'name':name,'folder':str(out),'project':str(project),'map':str(out/'raw.synctex'),'view':'--view'in flags})
 else:assert any(re.search(r'/(?:MediaBox|CropBox|Rotate|UserUnit)\b',p['bytes'])for p in rs),'negative did not actually emit root attributes'
 rows.append({'name':name,'expected':expected,'actualRootDictionaries':rs,'actualPageDictionaries':ps,'actualGeometry':g,'actualNative':j,'legacyNoExplicit':False})
for name,setup in legacy:
 project=root/(name+'-project');run(name+'-prepare',['python3',here/'make-project.py','--kind','lua','--out',project,'--extra-tex',setup,'--no-media','--view'])
 out,j,g,rs,ps=execute(name,project,candidate)
 parent,_,pg,_,_=execute(name+'-graphics-parent',project,'/control/r2-graphics-navigation/candidate/lua-graphics4')
 for f in ['output.pdf','output.log','raw.synctex','geometry.json']:assert (out/f).read_bytes()==(parent/f).read_bytes(),f'{name}: legacy parent byte regression: {f}'
 assert all(p['status']=='available'for p in g['pages'])
 rows.append({'name':name,'expected':'available','legacyNoExplicit':True,'byteIdenticalToGraphicsParent':True,'actualRootDictionaries':rs,'actualPageDictionaries':ps,'actualGeometry':g,'actualNative':j,'scope':'Existing ambiguous-root legacy behavior preserved exactly, NOT new explicit-origin support.'})
(root/'oracle-cases.json').write_text(json.dumps(oracle_cases,ensure_ascii=False,indent=2)+'\n')
(root/'receipt.json').write_text(json.dumps({'schemaVersion':1,'status':'pass','rows':rows,'commands':commands,'skips':0,'scope':'Actual native root/page emitted dictionaries;19 explicit-origin cases (empty/reset positives and inherited negatives),7 unchanged legacy parent comparisons. No source-derived effective attribute or browser claim.'},ensure_ascii=False,indent=2)+'\n')
print('actual inherited root/channel/late/reset native controls and exact legacy comparisons pass')
