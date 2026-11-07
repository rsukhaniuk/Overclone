#!/usr/bin/env python3
"""Actual converter controls; adversarial DVI files are independent QA inputs.
The production source/intermediate bytes are never modified by the application.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess

HERE=Path(__file__).resolve().parent
p=argparse.ArgumentParser(description=__doc__)
for name in ('runtime','overlay','component','base','out'):p.add_argument('--'+name,type=Path,required=True)
a=p.parse_args()
if a.out.exists():raise SystemExit('fresh output required')
a.out.mkdir(parents=True)
source=a.base.read_bytes()
def insert_specials(blob):
    # Genuine one-page DVI: use its validated trailing post_post pointer, then
    # the real postamble's backward BOP pointer. EOP is immediately before POST
    # in this explicitly single-page, no-between-pages-special fixture.
    assert source[:2]==bytes([247,2])
    tail=len(source)
    while source[tail-1]==223:tail-=1
    assert source[tail-1]==2 and source[tail-6]==249
    post=struct.unpack('>I',source[tail-5:tail-1])[0]
    assert source[post]==248 and struct.unpack('>H',source[post+27:post+29])[0]==1
    assert source[post-1]==140
    result=bytearray(source[:post-1]+blob+source[post-1:tail])
    struct.pack_into('>I',result,tail-5+len(blob),post+len(blob))
    result.extend(bytes([223])*4)
    while len(result)%4:result.append(223)
    return bytes(result)
special=lambda text:bytes([242])+struct.pack('>I',len(text.encode()))+text.encode()
begin=lambda text:'pdf:literal direct /Span << /ActualText <FEFF'+text+'> >> BDC'
end='pdf:literal direct EMC'
pair=lambda text:special(begin(text))+special(end)
cases={
    'valid-one':(pair('0456'),True),
    'valid-64':(pair('0066'*63+'0456'),True),
    'unbalanced-begin':(special(begin('0456')),False),
    'unmatched-end':(special(end),False),
    'nested':(special(begin('0456'))+pair('0406')+special(end),False),
    'bad-hex':(pair('045G'),False),
    'lowercase-hex':(pair('006f0456'),False),
    'surrogate':(pair('D8000456'),False),
    'non-whitelist':(pair('00E90456'),False),
    'ascii-only':(pair('0041'),False),
    'empty':(pair(''),False),
    'too-many-scalars':(pair('0066'*64+'0456'),False),
    'trailing-operator':(special(begin('0456')+' 1 0 0 1 5 0 cm')+special(end),False),
    'transform':(special('pdf:literal direct 1 0 0 1 5 0 cm'),False),
    'transform-before-valid':(special('pdf:literal direct 1 0 0 1 5 0 cm')+pair('0456'),False),
    'span-quota':(pair('0456')*65537,False),
}
rows=[]
for name,(blob,valid) in cases.items():
    dvi=a.out/(name+'.dvi');dvi.write_bytes(insert_specials(blob))
    pdf=a.out/(name+'.pdf')
    cmd=['node',str(HERE/'convert.cjs'),str(a.runtime),str(dvi),str(pdf),str(a.overlay),str(a.component)]
    r=subprocess.run(cmd,env=dict(os.environ,EXPECT_REFUSAL='0' if valid else '1'),stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=180)
    (a.out/(name+'.driver.log')).write_bytes(r.stdout)
    if r.returncode:raise SystemExit('actual converter negative failed: '+name)
    receipt=json.loads(Path(str(pdf)+'.json').read_text())
    available=receipt['geometry'] and all(x['status']=='available' for x in receipt['geometry']['pages'])
    assert bool(available)==valid,name+' geometry domain mismatch'
    if valid:assert receipt['semantic']['status']==1
    rows.append({'name':name,'expectedSafe':valid,'status':receipt['status'],
                 'semantic':receipt['semantic'],'geometryAvailable':bool(available),
                 'input':receipt['input'],'pdf':receipt['pdf']})
    print(name,'PASS',flush=True)
(a.out/'receipt.json').write_text(json.dumps({'schemaVersion':1,'status':'pass','nativeInvocations':len(rows),
    'skips':0,'baseDvi':{'length':len(source),'sha256':hashlib.sha256(source).hexdigest()},
    'cases':rows,'productRuntimeChanged':False},indent=2)+'\n')
