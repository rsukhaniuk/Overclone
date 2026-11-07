#!/usr/bin/env python3
"""Independent preinstalled Poppler + exact E1/native-map candidate comparison.
Use the host's existing Poppler; this script never installs dependencies.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

p=argparse.ArgumentParser(description=__doc__)
for name in ('matrix','observations','converter-observations','out'):p.add_argument('--'+name,type=Path,required=True)
a=p.parse_args()
if a.out.exists():raise SystemExit('fresh output required')
a.out.mkdir(parents=True)
matrix=json.loads((a.matrix/'native-matrix.json').read_text())
rows=[]
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def invoke(command,label):
    r=subprocess.run(list(map(str,command)),stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=180)
    (a.out/(label+'.stdout')).write_bytes(r.stdout)
    (a.out/(label+'.stderr')).write_bytes(r.stderr)
    assert r.returncode==0 and not r.stderr,label+' independent reader error'
    return r.stdout
for engine in matrix['engines']:
    for case in matrix['cases']:
        pair=[]
        for variant in ('control','candidate'):
            stem=case+'-'+engine+'-'+variant
            if engine=='latex':
                converter_stem=case+'-'+variant
                if not (a.converter_observations/(converter_stem+'.pdf')).exists():converter_stem=stem
                pdf=a.converter_observations/(converter_stem+'.pdf')
                e1=a.converter_observations/(converter_stem+'.e1.json')
            else:
                pdf=a.matrix/stem/'main.pdf';e1=a.observations/(stem+'.e1.json')
            prefix=a.out/stem
            invoke(['pdftotext','-enc','UTF-8',pdf,str(prefix)+'.txt'],stem+'-text')
            invoke(['pdftoppm','-r','144','-png',pdf,prefix],stem+'-raster')
            rasters={f.name.removeprefix(stem):sha(f) for f in sorted(a.out.glob(stem+'-*.png'))}
            assert rasters,stem+' no independent raster'
            observation=json.loads(e1.read_text())
            pair.append({'pdf':{'length':pdf.stat().st_size,'sha256':sha(pdf)},'popplerRaster':rasters,
                         'text':Path(str(prefix)+'.txt').read_text(),'e1':observation})
        control,candidate=pair
        assert control['popplerRaster']==candidate['popplerRaster'],case+' Poppler glyph/spacing/pagination drift'
        assert control['e1']['pageCount']==candidate['e1']['pageCount']
        assert [p['raster'] for p in control['e1']['pages']]==[p['raster'] for p in candidate['e1']['pages']],case+' E1 raster drift'
        assert [p['facts'] for p in control['e1']['pages']]==[p['facts'] for p in candidate['e1']['pages']],case+' page facts drift'
        expected={'mixed':['Привіт Київ. Іі Її Єє Ґґ. ASCII I i.'],
                  'ligature':['mixed fі ffі fіlm; Cyrillic Іі.'],
                  'lifetime':['Original Іі; Latin I i; mixed fі ffі fіlm.','Numeric Iiі І iі IІ.'],
                  'hyphenation':['Інститут','ілюстрація.','offіce','fіnite'],
                  'meaning':['Direct Іі; let Іі; edef Іі; Latin IiI i.','Futurelet іі.'],
                  'faces':['Київ Іі I i.']}[case]
        e1text='\n'.join(p['text'] for p in candidate['e1']['pages'])
        for s in expected:
            assert s in candidate['text'],case+' Poppler semantic mismatch: '+s
            assert s in e1text,case+' E1 semantic mismatch: '+s
        if case=='ligature':
            # Current dvipdfmx/Poppler may expose untouched ASCII ffi/ff as
            # compatibility ligature scalars. Preserve that exact old spelling;
            # only the explicitly marked Cyrillic component changes semantics.
            assert control['text'].split('; mixed')[0]==candidate['text'].split('; mixed')[0]
        if case=='faces':
            assert candidate['text'].count('Іі')==292 and e1text.count('Іі')==292
            assert candidate['text'].count('I i.')==292 and e1text.count('I i.')==292
        native_control=a.matrix/(case+'-'+engine+'-control')/'native.synctex'
        native_candidate=a.matrix/(case+'-'+engine+'-candidate')/'native.synctex'
        assert native_control.read_bytes()==native_candidate.read_bytes()
        geometry=None
        if engine=='latex':
            base=case+('-latex' if (a.converter_observations/(case+'-latex-control.pdf.json')).exists() else '')
            left=json.loads((a.converter_observations/(base+'-control.pdf.json')).read_text())
            right=json.loads((a.converter_observations/(base+'-candidate.pdf.json')).read_text())
            assert left['geometry']==right['geometry']
            assert all(p['status']=='available' for p in right['geometry']['pages'])
            assert right['semantic']['status']==1
            geometry=right['geometry']
        rows.append({'case':case,'engine':engine,'pages':candidate['e1']['pageCount'],
                     'nativeMapSha256':sha(native_candidate),'nativeMapsByteIdentical':True,
                     'popplerRasterByteIdentical':True,'e1RasterByteIdentical':True,'pageFactsIdentical':True,
                     'semanticTextVerified':True,'search':[{k:len(v) for k,v in p['search'].items()} for p in candidate['e1']['pages']],
                     'controlPdf':control['pdf'],'candidatePdf':candidate['pdf'],
                     'popplerRaster':candidate['popplerRaster'],'geometry':geometry,
                     'logicalTextBoxes':'ActualText may change logical character subdivisions; physical glyph raster, native maps and DVI page transform are exact.'})
        print(engine,case,'PASS',flush=True)
(a.out/'receipt.json').write_text(json.dumps({'schemaVersion':1,'status':'pass','cases':rows,'skips':0,
    'tools':{n:subprocess.run([n,'-v'],capture_output=True,text=True).stderr.splitlines()[0] for n in ('pdftotext','pdftoppm')},
    'productRuntimeChanged':False,'browserAcceptance':False},indent=2)+'\n')
