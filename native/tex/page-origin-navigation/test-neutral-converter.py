#!/usr/bin/env python3
"""Actual typed-channel positives, negatives and sticky unsafe controls."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

p=argparse.ArgumentParser(description=__doc__)
for name in ['dvi','xe','results']:
    p.add_argument('--'+name,type=Path,required=True)
a=p.parse_args();out=a.results.resolve();assert not out.exists();out.mkdir(parents=True);here=Path(__file__).resolve().parent;clock=here.parent/'synctex-fixtures/dvi/fixed-clock.cjs';rows=[];commands=[]
cases=[
 ('valid-metadata',r'pdf:docinfo << /Title (typed native fixture) >>',True),
 ('valid-color',r'color push rgb 1 0 0',True),
 ('raw-program',r'pdf:literal direct q 1 0 0 1 12 0 cm Q',False),
 ('opaque-object',r'pdf:obj @NEUTRALBAD << /Type /Example >>',False),
 ('metadata-suffix',r'pdf:docinfo << /Title (safe) >> 1 0 0 rg',False),
 ('catalog-page-tree',r'pdf:docview << /PageMode /UseOutlines /Pages << /Type /Pages /Kids [] /Count 0 >> >>',False),
 ('bad-link-type',r'pdf:bann << /Type /Annot /Subtype /Widget /A << /S /URI /URI (fixture) >> >>',False),
 ('bad-action-type',r'pdf:bann << /Type /Annot /Subtype /Link /A << /S /JavaScript /JS (fixture) >> >>',False),
 ('bad-border',r'pdf:bann << /Type /Annot /Subtype /Link /Border [-1 0 0] /A << /S /URI /URI (fixture) >> >>',False),
 ('bad-color-array',r'pdf:bann << /Type /Annot /Subtype /Link /C [0 1] /A << /S /URI /URI (fixture) >> >>',False),
 ('color-suffix',r'color push rgb 1 0 0 q',False),
 ('color-underflow',r'color pop',False),
 ('fractional-version',r'pdf:minorversion 1.5',False),
]
def run(label,cmd,env=None):
    r=subprocess.run(list(map(str,cmd)),env=env,capture_output=True,text=True,timeout=90)
    (out/(label+'.log')).write_text(r.stdout+r.stderr);commands.append({'label':label,'exit':r.returncode});return r.returncode
for kind in ['dvi','xe']:
    for label,special,good in cases:
        name=kind+'-'+label;project=out/(name+'-project');assert run(name+'-prepare',['python3',here/'make-project.py','--kind',kind,'--out',project])==0
        root=project/'тези/my thesis.tex';text=root.read_text();anchor=r'\hbox{\kern40bp ROOTBEFORE PAGEWORD}'
        assert text.count(anchor)==4
        payload='\\special{'+special+'}'
        if 'bann' in special:payload+=r'\special{pdf:eann}'
        if label in ['valid-color','color-suffix']:payload+=r'\special{color pop}'
        # A later valid native metadata operation must never clear unsafe state.
        payload+=r'\special{pdf:docinfo << /Subject (safe later operation) >>}'
        text=text.replace(anchor,r'\hbox{'+payload+r'\kern40bp ROOTBEFORE PAGEWORD}',1);root.write_text(text)
        env=dict(os.environ,PROJECT=str(project),REQUIRE_ORIGIN='1',CONVERTER=str(getattr(a,kind)))
        producer=out/(name+'-producer');assert run(name+'-compile',['node','-r',clock,here/'run-current-project.cjs',kind,'latex',producer],env)==0
        folder=out/(name+'-convert');code=run(name+'-convert',['node','-r',clock,here/'run-current-project.cjs',kind,'convert',folder,producer],env)
        receipt=json.loads((folder/'receipt.json').read_text());raw=(folder/'geometry.json').read_bytes();geometry=json.loads(raw)if raw else None
        if good:
            assert code==0 and receipt['nativeReturn']==0
            assert geometry and all(g['status']=='available'for g in geometry['pages'])
        elif receipt['nativeReturn']==0:
            assert geometry and all(g['status']=='unsupported'and g['writerToPdfRaw']is None and g['writerPageBoxBp']is None for g in geometry['pages']),name
        else:
            assert code!=0 and not raw,name
        rows.append({'kind':kind,'case':label,'expectedAvailable':good,'sourceSha256':hashlib.sha256(root.read_bytes()).hexdigest(),'native':receipt,'geometry':geometry})
(out/'receipt.json').write_text(json.dumps({'status':'pass','cases':len(rows),'skips':0,'scope':'actual native special execution; all negative cases followed by valid metadata to assert sticky unsafe state', 'rows':rows,'commands':commands},indent=2)+'\n');print('Typed native metadata/color positives and negatives pass')
