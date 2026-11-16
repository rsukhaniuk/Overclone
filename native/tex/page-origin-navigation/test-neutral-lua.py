#!/usr/bin/env python3
"""Actual Lua writer-channel positives and conservative negative controls."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

p=argparse.ArgumentParser(description=__doc__)
for name in ['candidate','results']:
    p.add_argument('--'+name,type=Path,required=True)
a=p.parse_args();out=a.results.resolve();assert not out.exists();out.mkdir(parents=True);here=Path(__file__).resolve().parent;clock=here.parent/'synctex-fixtures/dvi/fixed-clock.cjs';commands=[];rows=[]
annotation=lambda body:r'\pdfextension annot width 1bp height 1bp depth 0bp{'+body+'}'
color=lambda body:r'\pdfextension colorstack 0 push{'+body+r'}\pdfextension colorstack 0 pop'
cases=[
 ('native-color',color('1 0 0 rg 1 0 0 RG'),True),
 ('native-catalog',r'\pdfextension catalog{/PageMode/UseOutlines}',True),
 ('native-uri-annotation',annotation('/Subtype/Link/A<</Type/Action/S/URI/URI(https://example.org/)>>'),True),
 ('raw-paint-literal',r'\pdfextension literal direct{0 g 0 G}',False),
 ('raw-transform-literal',r'\pdfextension literal direct{q 1 0 0 1 12 0 cm Q}',False),
 ('color-program',color('q 1 0 0 1 12 0 cm Q'),False),
 ('color-out-of-range',color('2 g 2 G'),False),
 ('color-empty-pop',r'\pdfextension colorstack 0 pop',False),
 ('opaque-object',r'\immediate\pdfextension obj{<< /Type/Example >>}',False),
 ('catalog-page-tree',r'\pdfextension catalog{/Pages<< /Type/Pages/Kids[]/Count 0 >>}',False),
 ('catalog-prefix',r'\pdfextension catalog{/PageModeExtra/UseOutlines}',False),
 ('annotation-widget',annotation('/Subtype/Widget/A<</S/URI/URI(fixture)>>'),False),
 ('annotation-action',annotation('/Subtype/Link/A<</S/JavaScript/JS(fixture)>>'),False),
 ('annotation-suffix',annotation('/Subtype/Link/A<</S/URI/URI(fixture)>> 1 0 0 rg'),False),
 ('annotation-duplicate',annotation('/Subtype/Link/A<</S/URI/URI(fixture)>>/Border[0 0 0]/Border[0 0 0]'),False),
 ('annotation-color-count',annotation('/Subtype/Link/A<</S/URI/URI(fixture)>>/C[0 1]'),False),
]
def run(label,cmd,env=None):
    r=subprocess.run(list(map(str,cmd)),env=env,capture_output=True,text=True,timeout=90);(out/(label+'.log')).write_text(r.stdout+r.stderr);commands.append({'label':label,'exit':r.returncode});return r.returncode
for label,tex,good in cases:
    project=out/(label+'-project');assert run(label+'-prepare',['python3',here/'make-project.py','--kind','lua','--out',project])==0
    root=project/'тези/my thesis.tex';source=root.read_text();anchor=r'\hbox{\kern40bp ROOTBEFORE PAGEWORD}';assert source.count(anchor)==4
    # Valid later device-color operations cannot clear any unsafe native latch.
    follow=''if good else color('0 g 0 G')
    source=source.replace(anchor,r'\hbox{'+tex+follow+r'\kern40bp ROOTBEFORE PAGEWORD}',1);root.write_text(source)
    env=dict(os.environ,PROJECT=str(project),REQUIRE_ORIGIN='1',LUA_CANDIDATE=str(a.candidate));folder=out/label;code=run(label+'-compile',['node','-r',clock,here/'run-current-project.cjs','lua','latex',folder],env)
    receipt=json.loads((folder/'receipt.json').read_text());raw=(folder/'geometry.json').read_bytes();geometry=json.loads(raw)if raw else None
    if good:
        assert code==0 and receipt['nativeReturn']==0,label
        assert geometry and all(g['status']=='available'for g in geometry['pages']),label
    elif receipt['nativeReturn']==0:
        assert geometry and all(g['status']=='unsupported'and g['writerToPdfRaw']is None and g['writerPageBoxBp']is None for g in geometry['pages']),label
    else:
        assert code!=0 and not raw and not receipt['rawMap']['length'] and not receipt['outputs'].get('pdf'),label
    rows.append({'case':label,'expectedAvailable':good,'sourceSha256':hashlib.sha256(root.read_bytes()).hexdigest(),'native':receipt,'geometry':geometry})
(out/'receipt.json').write_text(json.dumps({'status':'pass','cases':len(rows),'skips':0,'scope':'actual native writer roles, color provenance, raw-program/opaque-object refusal and unsafe-state persistence', 'rows':rows,'commands':commands},indent=2)+'\n');print('Typed native Lua writer/color positive and negative controls pass')
