#!/usr/bin/env python3
"""Bounded candidate-only original/control PDF semantics and layout comparison."""
import hashlib,json,os,pathlib,subprocess,sys,xml.etree.ElementTree as ET
runtime,component,fmt,upstream,pdf_runtime,overlay,out=map(lambda p:pathlib.Path(p).resolve(),sys.argv[1:])
assert not out.exists();out.mkdir(parents=True)
base=pathlib.Path(__file__).resolve().parent
pin=lambda p:{'length':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()}
commands=[]
def run(argv):
    commands.append(argv)
    (out/'commands.json').write_text(json.dumps(commands,indent=2)+'\n')
    subprocess.run(argv,check=True,timeout=90)
def glyphs(file):
    tree=ET.parse(file);result=[]
    def walk(n,context):
        context=dict(context)
        if n.tag in ['page','fill_text','stroke_text','span']: context[n.tag]=sorted(n.attrib.items())
        if n.tag=='g':result.append((context,sorted(n.attrib.items())))
        for c in n:walk(c,context)
    walk(tree.getroot(),{});return result
cases=[('original',base.parent/'fixtures/harfbuzz.tex'),('mixed',base/'fixtures/mixed.tex'),('lines-pages',base/'fixtures/lines-pages.tex'),('latin',base/'fixtures/latin.tex')]
if os.getenv('HB_CASE'):
    cases=[c for c in cases if c[0]==os.environ['HB_CASE']]
    assert cases,'unknown case'
results=[]
for name,source in cases:
    pair=[]
    for kind in ['baseline','candidate']:
        target=out/(name+'-'+kind)
        mount={base.parent:'/audit',runtime:'/runtime',component:'/component',fmt.parent:'/format',upstream:'/upstream',pdf_runtime:'/pdf-runtime',overlay:'/overlay',out:'/results'}
        argv=['docker','run','--rm','--network','none','--read-only','--tmpfs','/tmp:rw,noexec,nosuid,size=64m','--cpus','2','--memory','1g','--pids-limit','128','--user',str(os.getuid())+':'+str(os.getgid())]
        for host,guest in mount.items():argv+=['-v',str(host)+':'+guest+(''if host==out else':ro')]
        common=argv+['--entrypoint','node',os.getenv('HB_TEST_IMAGE','oop-dev-tex-bootstrap:20261112')]
        src='/audit/'+str(source.relative_to(base.parent))
        run(common+['/audit/semantics/probe.cjs','/runtime','/component','/format/'+fmt.name,src,'/upstream','/overlay'if kind=='candidate'else'-','/results/'+target.name])
        run(common+['/audit/semantics/inspect.cjs','/pdf-runtime','/results/'+target.name+'/result.pdf','/results/'+target.name+'/e1.json'])
        pdf=target/'result.pdf'
        run(['pdftotext','-bbox',str(pdf),str(target/'poppler-bbox.html')])
        run(['pdftotext','-raw',str(pdf),str(target/'poppler-raw.txt')])
        run(['pdftoppm','-r','72',str(pdf),str(target/'page')])
        run(['mutool','draw','-F','trace','-o',str(target/'trace.xml'),str(pdf)])
        pair.append(target)
    a,b=pair;ea,eb=[json.loads((p/'e1.json').read_text())for p in pair]
    ra,rb=[json.loads((p/'receipt.json').read_text())for p in pair]
    equal_pages=ea['pageCount']==eb['pageCount']
    checks={'pageCount':equal_pages}
    checks['pageFacts']=[p['facts']for p in ea['pages']]==[p['facts']for p in eb['pages']]
    checks['e1Raster']=[p['raster']for p in ea['pages']]==[p['raster']for p in eb['pages']]
    ga,gb=[glyphs(p/'trace.xml')for p in pair];checks['glyphIdsPositionsAdvancesUnicode']=ga==gb
    ppm=lambda p:[(f.name,pin(f))for f in sorted(p.glob('page-*.ppm'))]
    checks['popplerRaster']=ppm(a)==ppm(b)
    maps={}
    for n in ['synctex.raw','geometry.json']:
        assert (a/n).exists()and(b/n).exists(),n
        maps[n]={'baseline':pin(a/n),'candidate':pin(b/n),'equal':(a/n).read_bytes()==(b/n).read_bytes()}
    checks['nativeGeometry']=maps['geometry.json']['equal']
    # Source line numbers and native raw positions are also expected exact.
    checks['nativeRawMap']=maps['synctex.raw']['equal']
    text='\n'.join(p['text']for p in eb['pages']);bbox=ET.parse(b/'poppler-bbox.html')
    bbox_words=[n.text or''for n in bbox.iter()if n.tag.split('}')[-1]=='word']
    query='مرحبا بالعالم'
    if name!='latin':
        checks['literalE1Extraction']=query in text
        checks['literalE1Search']=any(p['search'][query]for p in eb['pages'])
        checks['literalPopplerBbox']=any(query in word for word in bbox_words)
    if name=='mixed':
        checks['mixedLiterals']=all(literal in text for literal in ['Latin office ffi 123.','Latin I i f fi ffi.','لا لأ لإ لآ'])
    if name=='lines-pages':checks['physicalTwoPages']=eb['pageCount']==2
    results.append({'case':name,'checks':checks,'passed':all(checks.values()),'source':pin(source),'sourceUnchanged':ra['source']['unchanged']and rb['source']['unchanged'],'pages':eb['pageCount'],'e1Text':text,'glyphCount':len(gb),'nativeMaps':maps,'popplerBboxWords':bbox_words,'plainPopplerRawMayUseBidiDisplayOrder':True})
receipt={'schemaVersion':1,'status':'pass'if all(r['passed']for r in results)else'fail','scope':'native candidate only; no browser/profile promotion','overlay':pin(overlay/'manifest.json'),'sources':[{'name':p.name,**pin(p)}for p in sorted(base.glob('*'))if p.is_file()],'cases':results}
(out/'receipt.json').write_text(json.dumps(receipt,ensure_ascii=False,indent=2)+'\n');print(json.dumps({'status':receipt['status'],'cases':len(results),'failed':[{r['case']:[k for k,v in r['checks'].items()if not v]}for r in results if not r['passed']]}))
sys.exit(0 if receipt['status']=='pass'else 1)
