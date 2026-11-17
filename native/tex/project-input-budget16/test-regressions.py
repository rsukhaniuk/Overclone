#!/usr/bin/env python3
"""Replay genuine retained P15 converter cases; compare exact parent/new bytes.

Uses existing native intermediates, not new TeX compiler runs. Includes all26
native neutral-special positive/negative sticky-unsafe controls and10 package/
biblatex/origin fixtures. Pass explicit retained parents and P15 runtime paths.
"""
import argparse,json,os,subprocess,shutil,hashlib
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__)
for name in ['parents','build','runtime','out']:p.add_argument('--'+name,type=Path,required=True)
a=p.parse_args();out=a.out.resolve();assert not out.exists();out.mkdir(parents=True)
here=Path(__file__).resolve().parent;repo=here.parents[2];clock=repo/'native/tex/synctex-fixtures/dvi/fixed-clock.cjs';cases=[]
for kind in ['xe','dvi']:
    suffix='xdv'if kind=='xe'else'dvi'
    for scope in ['packages','biblatex']:
        project=out/(kind+'-'+scope+'-project');project.mkdir();root=project/'тези/my thesis.tex';root.parent.mkdir()
        directory='PackageFontFixtures'if scope=='packages'else'CompilerBibliographyFixtures'
        shutil.copyfile(repo/'tests'/directory/('main-unicode.tex'if kind=='xe'else'main-latin.tex'),root)
        for name in ['chapter.tex','references.bib','figure.xbb']:shutil.copyfile(repo/'tests/CompilerSuiteFixtures'/name,project/name)
        shutil.copyfile(repo/'src/Client/wwwroot/icons/icon-192.png',project/'figure.png')
        cases.append((kind,scope,project,a.parents/f'corpus03/{kind}-{scope}-6-latex/output.{suffix}'))
    for mode in ['normal','view','mag1200']:
        stem=kind+'-'+mode;base=a.parents/'neutral-converter-origins01'
        cases.append((kind,mode,base/(stem+'-project'),base/f'{stem}-producer/output.{suffix}'))
    base=a.parents/'neutral-converter-domains01'
    for producer in sorted(base.glob(kind+'-*-producer')):
        label=producer.name[len(kind)+1:-len('-producer')]
        cases.append((kind,'neutral-'+label,base/(kind+'-'+label+'-project'),producer/f'output.{suffix}'))
records=[]
for kind,label,project,ir in cases:
    results=[]
    for variant,converter in [('parent',a.build/(kind+'01/parent-reproduced')),('candidate',a.build/(kind+'01/candidate'))]:
        dest=out/(kind+'-'+label+'-'+variant)
        command=['node','-r',str(clock),str(here/'replay.cjs'),str(a.runtime),str(converter),str(ir),str(project),str(dest),'compare']
        with (out/(dest.name+'.log')).open('wb')as log:subprocess.run(command,stdout=log,stderr=subprocess.STDOUT,check=True,timeout=90)
        results.append((dest,json.loads((dest/'receipt.json').read_text())))
    left,right=results
    assert left[1]['nativeReturn']==right[1]['nativeReturn'],(kind,label,'status')
    for name in ['output.pdf','geometry.json','output.log']:
        assert (left[0]/name).read_bytes()==(right[0]/name).read_bytes(),(kind,label,name)
    if label.startswith('neutral-'):
        good=label in ['neutral-valid-color','neutral-valid-metadata']
        geometry=right[1]['geometry']
        if right[1]['nativeReturn']==0:
            assert geometry and all(page['status']==('available'if good else'unsupported')for page in geometry['pages']),(kind,label)
        else:assert not good and right[1]['output']['pdfBytes']==right[1]['output']['geometryBytes']==0
    else:assert right[1]['nativeReturn']==0 and all(p['status']=='available'for p in right[1]['geometry']['pages'])
    records.append({'kind':kind,'case':label,'nativeReturn':right[1]['nativeReturn'],'projectBytes':right[1]['projectBytes'],'outputs':right[1]['output'],'exactParentEquality':True})
(out/'receipt.json').write_text(json.dumps({'schemaVersion':1,'status':'pass','cases':len(records),'skips':0,'records':records,'scope':'native converter replay of retained genuine intermediates; no new TeX engine/compiler/browser acceptance'},indent=2)+'\n')
print(json.dumps({'status':'pass','cases':len(records),'allParentOutputsIdentical':True}))
