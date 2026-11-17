#!/usr/bin/env python3
"""Compile isolated callback unit witness; never export private hooks in candidate."""
import argparse,os,json,subprocess
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__)
for name in ['candidate','source','cache','out']:p.add_argument('--'+name,type=Path,required=True)
p.add_argument('--kind',choices=['xe','dvi'],required=True);a=p.parse_args()
out=a.out.resolve();assert not out.exists();out.mkdir(parents=True);here=Path(__file__).resolve().parent
command=['em++','-std=c++17','-O2','-DOC_DVI_ABI='+('3'if a.kind=='xe'else'2'),'-DQUOTA_WRAPPER_PATH="'+str(a.candidate.resolve()/'geometry-overlay/oc_dvi_origin.cpp')+'"','-I'+str(a.source.resolve()),str(here/'test-output.cpp'),str(a.candidate.resolve()/'md5.o'),'-sENVIRONMENT=node','-sFILESYSTEM=0','-sALLOW_MEMORY_GROWTH=1','-sMAXIMUM_MEMORY=536870912','-sEXIT_RUNTIME=1','-o',str(out/'unit.js')]
env=dict(os.environ,EM_CACHE=str(a.cache.resolve()),EM_FROZEN_CACHE='1');subprocess.run(command,env=env,check=True,timeout=120)
(out/'commands.json').write_text(json.dumps({'compile':command,'scope':'exact wrapper callback unit witness with stub converter; not genuine PDF conversion or adoption'},indent=2)+'\n')
