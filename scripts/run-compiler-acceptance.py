#!/usr/bin/env python3
"""Verify a declared compiler corpus, then wrap the existing real browser test.

No downloads, builds, runtime adoption, source repair or injected native outputs.
Preflight hashes explicit inputs only; it never starts Docker or a browser.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = 'tests/CompilerCompatibilityFixtures/manifest.json'
ENGINE = {'pdflatex':'local-authoring-v4','latex-dvi':'latex-dvi','xelatex':'xelatex','lualatex':'lualatex'}

class Refusal(ValueError):
    pass

def require(condition, message):
    if not condition:
        raise Refusal(message)

def digest(data):
    return hashlib.sha256(data).hexdigest()

def pin(path):
    with path.open('rb') as stream:
        value = hashlib.file_digest(stream, 'sha256').hexdigest()
    return {'length':path.stat().st_size,'sha256':value}

def relative(value):
    require(isinstance(value,str) and 0<len(value)<=300 and '\\' not in value and ':' not in value,
            'unsafe manifest relative path')
    require(not value.startswith('/') and all(p not in ('','.','..') for p in value.split('/')),
            'unsafe manifest relative path')
    require(all(ord(c)>=32 and ord(c)!=127 for c in value),'control character in manifest path')
    return value

def safe_path(value, directory=True, fresh=False):
    require(value is not None,'missing explicit input/results path')
    raw=Path(value)
    require('..' not in raw.parts and not any(c in str(raw) for c in ',\n\r\0'),'unsafe explicit path')
    path=raw.absolute()
    require(not any(p.is_symlink() for p in (path,*path.parents)),'symlink input/output is refused')
    if fresh:
        require(not path.exists() and path.parent.is_dir(),'results must be fresh with an existing parent')
    else:
        require(path.is_dir() if directory else path.is_file(),'missing explicit input: '+str(path))
    return path

def checked_file(root, name, expected=None):
    name=relative(name)
    file=safe_path(root/name,directory=False)
    observed=pin(file)
    if expected is not None:
        require(observed=={'length':expected['length'],'sha256':expected['sha256']},'source/input digest drift: '+name)
    return file,observed

def load_json(path):
    require(path.stat().st_size<=8*1024*1024,'manifest exceeds bounded size')
    try:return json.loads(path.read_text())
    except (ValueError,UnicodeError) as error:raise Refusal('invalid JSON manifest') from error

def closure(rows):
    return digest('\n'.join(f"{r['name']}\t{r['length']}\t{r['sha256']}" for r in rows).encode())

def verify_sources(root, manifest):
    files=manifest['sourceFiles']
    require(isinstance(files,list) and 1<=len(files)<=256,'bounded source graph required')
    names=[relative(r['name']) for r in files]
    require(names==sorted(set(names)),'source graph must be sorted and unique')
    require({manifest['harness'],'toolchains.env','scripts/run-compiler-acceptance.py'}<=set(names),
            'harness, runner and pinned toolchain must be in the checked source graph')
    for row in files:
        require(type(row['length']) is int and 0<=row['length']<=4*1024*1024 and re.fullmatch('[0-9a-f]{64}',row['sha256']), 'invalid source pin')
        checked_file(root,row['name'],row)
    expected=next(p for p in files if p['name']=='scripts/run-compiler-acceptance.py')
    require(pin(Path(__file__))=={'length':expected['length'],'sha256':expected['sha256']},
            'executing runner differs from the reviewed source graph')
    # Every relative CommonJS dependency must itself be in the checked graph.
    for name in names:
        if not name.endswith(('.cjs','.js')):continue
        text=(root/name).read_text()
        for child in re.findall(r"require\(['\"](\.[^'\"]+)['\"]\)",text):
            target=Path(os.path.normpath(str(Path(name).parent/child))).as_posix()
            require(target in names,'unchecked relative harness dependency: '+target)
    return closure(files)

def verify_runtime(directory, required):
    path=safe_path(directory/'pins.json',directory=False);rows=load_json(path)
    require(isinstance(rows,list) and 1<=len(rows)<=8192,'invalid runtime inventory')
    names=[];total=0
    for row in rows:
        require(isinstance(row,dict) and set(row)=={'name','length','sha256'},'invalid runtime pin fields')
        name=relative(row['name']);names.append(name)
        require(type(row['length']) is int and 0<=row['length']<=40*1024*1024,'invalid runtime pin length')
        require(isinstance(row['sha256'],str) and re.fullmatch('[0-9a-f]{64}',row['sha256']),'invalid runtime pin hash')
        checked_file(directory/'files',name,row);total+=row['length']
    require(len(names)==len(set(names)) and total<=512*1024*1024,'duplicate/overlimit runtime inventory')
    require(set(required)<=set(names),'missing fixture runtime inputs: '+','.join(sorted(set(required)-set(names))))
    ordered=sorted(rows,key=lambda r:r['name'])
    return {'pins':pin(path),'resources':len(rows),'totalBytes':total,'closureSha256':closure(ordered)}

def verify_pdf(directory):
    path=safe_path(directory/'manifest.json',directory=False);value=load_json(path)
    require(value.get('version')==1 and len(value.get('resources',[]))==2,'PDF manifest must declare two resources')
    require({r.get('role') for r in value['resources']}=={'loader','wasm'},'PDF loader/wasm roles required')
    for row in value['resources']:checked_file(directory/'files',row['name'],row)
    return {'manifest':pin(path),'resources':value['resources']}

def verify_publication(directory, expected_id):
    checked_file(directory,'index.html')
    file,_=checked_file(directory,'service-worker-assets.js');raw=file.read_text()
    match=re.fullmatch(r'\s*self\.assetsManifest\s*=\s*(\{.*\})\s*;\s*',raw,re.S)
    require(match is not None,'invalid generated SDK asset manifest')
    value=json.loads(match.group(1));assets=value.get('assets')
    require(isinstance(value.get('version'),str) and isinstance(assets,list) and 1<=len(assets)<=16384,'invalid SDK inventory')
    if expected_id:require(value['version']==expected_id,'publication identity mismatch')
    names=[];total=0
    for row in assets:
        name=relative(row['url']);names.append(name)
        _,actual=checked_file(directory,name)
        encoded='sha256-'+base64.b64encode(bytes.fromhex(actual['sha256'])).decode()
        require(row.get('hash')==encoded,'published SDK asset digest drift: '+name);total+=actual['length']
    require(len(names)==len(set(names)),'duplicate SDK asset')
    return {'id':value['version'],'assetManifest':pin(file),'assets':len(assets),'totalAssetBytes':total,'allAssetHashesVerified':True}

def select_fixture(manifest,args):
    fixtures={f['id']:f for f in manifest['fixtures']}
    require(args.fixture in fixtures,'unknown fixture; available: '+', '.join(fixtures))
    fixture=fixtures[args.fixture]
    require(fixture['status']=='available','real/supplied corpus is not available; synthetic fixtures do not replace it')
    require(args.engine is not None and args.backend is not None,'selected fixture needs explicit --engine and --backend')
    require(any(args.engine in c['engines'] and args.backend==c['backend'] for c in fixture['combinations']),
            'incompatible fixture/engine/backend choice')
    require(any(args.engine in v['engines'] for v in fixture['variants']),'missing declared engine fixture variant')
    root=args.selected_root or fixture['selectedRoot']
    require(root.endswith('.tex') and len(root.encode('utf-16-le'))//2<=200,'invalid selected TeX root')
    relative(root)
    require(root=='main.tex' or fixture.get('rootRenameAllowed',False),'fixture does not support root renaming')
    return fixture,root

def verify_projects(root, fixtures,manifest):
    # File fixtures refer directly to checked bytes. Generated controls bind the
    # unchanged checked-in extractor/generator plus recorded project identities.
    source_names={row['name'] for row in manifest['sourceFiles']}
    for fixture in fixtures:
        for variant in fixture.get('variants',[]):
            require(variant.get('files') and len(variant['files'])<=64,'invalid project fixture inventory')
            require(len({relative(f['name']) for f in variant['files']})==len(variant['files']),'project file collision')
            for row in variant['files']:
                require(type(row['length']) is int and 0<=row['length']<=1048576,
                        'invalid fixture length or app per-file capture bound')
                require(isinstance(row['sha256'],str) and re.fullmatch('[0-9a-f]{64}',row['sha256']),
                        'invalid fixture digest')
                if 'source' in row:
                    require(row['source'] in source_names,'unchecked fixture source')
                    checked_file(root,row['source'],row)
                else:
                    require(row['generatorSource'] in source_names,'unchecked fixture generator')
            require(sum(r['length'] for r in variant['files'])<=8388608,'fixture exceeds app aggregate bound')

def snapshot_source(root,manifest,destination):
    for row in manifest['sourceFiles']:
        file,_=checked_file(root,row['name'],row);target=destination/row['name'];target.parent.mkdir(parents=True,exist_ok=True)
        target.write_bytes(file.read_bytes());require(pin(target)=={'length':row['length'],'sha256':row['sha256']},'source changed while snapshotting');target.chmod(0o444)
    target=destination/MANIFEST;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes((root/MANIFEST).read_bytes());target.chmod(0o444)
    for p in sorted(destination.rglob('*'),reverse=True):
        if p.is_dir():p.chmod(0o555)
    destination.chmod(0o555)

def image_identity(reference,base):
    def inspect(name):
        run=subprocess.run(['docker','image','inspect',name],capture_output=True,text=True,timeout=20)
        require(run.returncode==0,'required cached Docker image is unavailable; no pull/build is performed')
        value=json.loads(run.stdout)[0]
        return {'id':value['Id'],'os':value['Os'],'architecture':value['Architecture'],'layers':value['RootFS']['Layers']}
    child=inspect(reference);parent=inspect(base)
    require(child['layers'][:len(parent['layers'])]==parent['layers'],'cached browser image does not derive from the pinned base')
    return {'requested':reference,'resolvedId':child['id'],'pinnedBase':base,'baseId':parent['id'],
            'baseLayerPrefixVerified':True,'os':child['os'],'architecture':child['architecture']}

def verify_browser_receipt(value,fixture,args,selected,manifest,runtime,pdf,publication):
    require(value.get('result')=='PASS','browser harness did not pass')
    require(value.get('case')==fixture['case'] and value.get('compilerChoice')==ENGINE[args.engine],
            'browser fixture/engine differs from the selected corpus cell')
    require(value.get('bundledRuntime') is True,'browser did not use automatically bundled tools')
    sdk=value.get('sdk',{})
    require(sdk.get('manifestVersion')==publication['id'] and sdk.get('manifestAssets')==publication['assets']
            and sdk.get('allAssetHashesVerified') is True and sdk.get('cspValid') is True,
            'browser publication identity or integrity differs from preflight')
    sources={p['name']:p for p in manifest['sourceFiles']}
    require(value.get('harnessSha256')==sources[manifest['harness']]['sha256']
            and value.get('sourcePdfHelperSha256')==sources['scripts/source-pdf-navigation-ui-checks.cjs']['sha256'],
            'browser did not execute the reviewed harness/helpers')
    resources=value.get('resources',{})
    require(resources.get('compilerPins')==runtime['resources']
            and resources.get('pinsSha256')==runtime['pins']['sha256']
            and resources.get('pdfManifestSha256')==pdf['manifest']['sha256'],
            'browser runtime inputs differ from preflight')
    require(value.get('online',{}).get('selectedMain','main.tex')==selected,
            'browser selected root differs from the requested literal root')
    cold=value.get('coldOffline',{})
    require(cold.get('originRefused') is True and cold.get('offlineBeforeNavigation') is True,
            'full stopped-origin cold-offline evidence is missing')

def parser():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--preflight',action='store_true',help='validate identities and write a planned command; no Docker/browser execution')
    p.add_argument('--fixture',help='corpus fixture ID; omit only for all-synthetic preflight')
    p.add_argument('--engine',choices=list(ENGINE));p.add_argument('--backend',choices=['none','bibtex','biber'])
    p.add_argument('--selected-root',help='literal UI-renamed root, default from fixture')
    p.add_argument('--publication',type=Path,required=True,help='explicit published static wwwroot directory')
    p.add_argument('--publication-id',help='expected SDK asset version; required for browser execution')
    p.add_argument('--results',type=Path,default=os.environ.get('RESULT_DIR'),help='fresh result directory; or explicit RESULT_DIR')
    p.add_argument('--authoring-runtime',type=Path,default=os.environ.get('AUTHORING_RUNTIME_DIR'),help='pins.json + files; or AUTHORING_RUNTIME_DIR')
    p.add_argument('--pdf-runtime',type=Path,default=os.environ.get('PDF_RUNTIME_DIR'),help='manifest.json + files; or PDF_RUNTIME_DIR')
    p.add_argument('--lua-harfbuzz-source',type=Path,default=os.environ.get('LUAHARFBUZZ_SOURCE_DIR'),help='approved original Amiri source directory; or LUAHARFBUZZ_SOURCE_DIR')
    p.add_argument('--source-root',type=Path,default=ROOT,help='public repository source checkout')
    p.add_argument('--browser-image',default='oop-dev-browser:local',help='already cached image derived from pinned BROWSER_IMAGE; never pulled')
    return p

def main(argv=None):
    args=parser().parse_args(argv)
    require(args.preflight or args.fixture is not None,'browser execution needs an explicit --fixture')
    require(args.preflight or args.publication_id,'browser execution needs explicit --publication-id')
    require(args.fixture is not None or not any([args.engine,args.backend,args.selected_root]),'engine/backend/root require a selected fixture')
    source=safe_path(args.source_root);manifest_path=safe_path(source/MANIFEST,directory=False);manifest=load_json(manifest_path)
    require(manifest.get('schemaVersion')==1,'unsupported corpus manifest version')
    require(len({f['id'] for f in manifest['fixtures']})==len(manifest['fixtures']),'duplicate fixture IDs')
    fixture,selected=(select_fixture(manifest,args) if args.fixture else (None,None))
    fixtures=[fixture] if fixture else [f for f in manifest['fixtures']if f['status']=='available']
    source_digest=verify_sources(source,manifest);verify_projects(source,fixtures,manifest)
    runtime=safe_path(args.authoring_runtime);pdf=safe_path(args.pdf_runtime);publication=safe_path(args.publication);results=safe_path(args.results,fresh=True)
    inputs=[source,runtime,pdf,publication]
    font=None
    if any(f.get('externalFont') for f in fixtures):
        upstream=safe_path(args.lua_harfbuzz_source);font,_=checked_file(upstream,manifest['amiri']['source'],manifest['amiri']);inputs.append(upstream)
    require(not any(results==p or results in p.parents or p in results.parents for p in inputs),'results/input overlap is refused')
    required=sorted({n for f in fixtures for n in f.get('requiredRuntimeInputs',[])})
    runtime_fact=verify_runtime(runtime,required);pdf_fact=verify_pdf(pdf);publication_fact=verify_publication(publication,args.publication_id)
    for key in ['resources','totalBytes','closureSha256']:require(runtime_fact[key]==manifest['runtimeProfile'][key],'mixed/unexpected compiler runtime profile')
    pdf_profile=manifest['pdfRuntime']
    approved_pdf_pairs=[pdf_profile['resources'], *pdf_profile.get('compatibleResources', [])]
    require(pdf_fact['resources'] in approved_pdf_pairs,'unexpected PDF runtime identity')
    for resource in pdf_fact['resources']:
        checked_file(publication, 'bundled-runtime/'+resource['name'], resource)
    toolchains=dict(line.split('=',1)for line in (source/'toolchains.env').read_text().splitlines()if line and not line.startswith('#'))
    base=toolchains['BROWSER_IMAGE'];require('@sha256:'in base,'immutable browser base required')
    require(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._/@:+-]*',args.browser_image),'invalid cached browser image reference')
    image=None if args.preflight else image_identity(args.browser_image,base)
    results.mkdir(mode=0o755);browser=results/'browser';browser.mkdir(mode=0o755);browser.chmod(0o777)
    snapshot=results/'source';snapshot.mkdir();snapshot_source(source,manifest,snapshot)
    name='oop-compiler-acceptance-'+uuid.uuid4().hex[:16]
    command=['docker','run','--rm','--pull=never','--name',name,'--network','none','--read-only','--user','65534:65534',
             '--cpus','2','--memory','4g','--pids-limit','256','--shm-size','1g','--tmpfs','/tmp:rw,nosuid,nodev,size=1g']
    for host,guest in [(snapshot,'/source'),(runtime,'/runtime'),(pdf,'/pdf'),(publication,'/publication')]:
        command+=['--mount',f'type=bind,src={host},dst={guest},readonly']
    command+=['--mount',f'type=bind,src={browser},dst=/results']
    if font:command+=['--mount',f'type=bind,src={font},dst=/upstream/fonts/amiri-regular.ttf,readonly']
    env={'PUBLISHED_WWWROOT':'/publication','AUTHORING_RUNTIME_DIR':'/runtime','PDF_RUNTIME_DIR':'/pdf','RESULT_DIR':'/results',
         'BUNDLED_RUNTIME':'1','XDG_CONFIG_HOME':'/tmp/config','XDG_CACHE_HOME':'/tmp/cache'}
    if fixture:
        require(set(fixture.get('environment',{}))<={'PDF_DOWNLOAD_CHECKS','SOURCE_PDF_CHECKS','CYRILLIC_FIXTURE'},'unsupported fixture environment flag')
        env.update(fixture.get('environment',{}));env.update(CASE=fixture['case'],COMPILER_CHOICE=ENGINE[args.engine],SELECTED_MAIN_PATH=selected)
    if font:env['LUAHARFBUZZ_SOURCE_DIR']='/upstream'
    for key,value in env.items():command+=['-e',key+'='+value]
    command+=['--entrypoint','node',image['resolvedId']if image else args.browser_image,'/source/scripts/test-local-authoring-ui.cjs']
    if fixture is None:command=None
    receipt={'schemaVersion':1,'milestone':'R1','workPackage':'R1-F','status':'preflight-pass'if args.preflight else'running',
             'baselineSourceDigest':source_digest,'changedFiles':[],'manifest':pin(manifest_path),
             'runner':pin(Path(__file__)),
             'fixture':fixture['id']if fixture else None,'engine':args.engine,'backend':args.backend,'selectedRoot':selected,
             'synthetic':True,'realCorpus':{'status':'not-supplied','acceptance':'not-run'},'publication':publication_fact,
             'runtime':runtime_fact,'pdfRuntime':pdf_fact,'toolchain':{'pinnedBase':base,'image':image},
             'limits':{'containerCpus':2,'containerMemoryBytes':4294967296,'sharedMemoryBytes':1073741824,
                       'pids':256,'outerDeadlineSeconds':420,'harnessDeadlineSeconds':360},
             'command':command,'browserNativeProof':False,'applicationAdoptionPerformed':False,
             'remainingGates':['supplied representative real thesis corpus','remaining engine/backend/stress and R2/R3/R4 gates are not inferred from this selected case'],
             'knownLimitations':fixture.get('knownLimitations',[])if fixture else manifest['limitations'],
             'acceptance':[{'id':'R1-F-05','status':'not-run','scope':'static identities checked; actual wrapper/browser execution still required','command':'explicit source/runtime/publication hash preflight','receipt':'acceptance-handoff.json'},
                           {'id':'R1-F-01','status':'not-run','command':'full required matrix','receipt':None}],
             'proposedSharedChanges':[],'nextAction':'master executes reviewed command for this cell'if args.preflight else'review actual browser result'}
    output=results/'acceptance-handoff.json'
    def save():output.write_text(json.dumps(receipt,ensure_ascii=False,indent=2)+'\n')
    save()
    if args.preflight:
        print(json.dumps({'status':'preflight-pass','receipt':str(output),'browserNativeProof':False}));return 0
    started=time.monotonic();code=1
    try:
        with(results/'browser.stdout.log').open('wb')as stdout,(results/'browser.stderr.log').open('wb')as stderr:
            run=subprocess.run(command,stdout=stdout,stderr=stderr,timeout=420);code=run.returncode
        candidate=browser/'local-authoring-receipt.json'
        if candidate.exists():receipt['browserReceipt']=load_json(candidate);receipt['browserReceiptIdentity']=pin(candidate)
        passed=code==0 and receipt.get('browserReceipt',{}).get('result')=='PASS'
        if passed:
            try:verify_browser_receipt(receipt['browserReceipt'],fixture,args,selected,manifest,runtime_fact,pdf_fact,publication_fact)
            except Refusal as error:
                passed=False;receipt['correlationError']=str(error)
        receipt.update(status='pass'if passed else'fail',browserNativeProof=passed,exitCode=code)
        receipt['acceptance'][0].update(status='pass'if passed else'fail',scope='selected fixture only; full matrix and real corpus are separate',command=command)
        return 0 if passed else 1
    except (subprocess.TimeoutExpired,KeyboardInterrupt):
        cleanup=subprocess.run(['docker','rm','-f',name],capture_output=True,timeout=15)
        receipt.update(status='cancelled-or-timeout',exitCode=124,containerCleanupExit=cleanup.returncode);return 124
    except (Refusal,OSError,ValueError) as error:
        receipt.update(status='fail',exitCode=2,executionError=str(error));return 2
    finally:
        receipt['elapsedSeconds']=time.monotonic()-started
        receipt['artifacts']=[{'name':str(p.relative_to(results)),**pin(p)}for p in sorted(browser.rglob('*'))if p.is_file()and not p.is_symlink()]
        save()
        print(json.dumps({'status':receipt['status'],'receipt':str(output),'browserNativeProof':receipt['browserNativeProof']}))

if __name__=='__main__':
    try:sys.exit(main())
    except (Refusal,OSError,ValueError,KeyError,TypeError)as error:
        print('Refused compiler acceptance: '+str(error),file=sys.stderr);sys.exit(2)
