#!/usr/bin/env python3
"""Explicit Xe maps candidate. Real Xe-specific TIE/OTANGLE/Web2C regeneration.
Only fresh --out is written; defaults and supplied sources are never modified.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
HERE=Path(__file__).resolve().parent
SHARED=HERE.parent/'synctex-producer'
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--port',type=Path,required=True)
p.add_argument('--texlive',type=Path,required=True)
p.add_argument('--generators',type=Path,required=True)
p.add_argument('--otangle',type=Path,required=True,help='genuine native OTANGLE; bootstrap from approved omegaware source if absent')
p.add_argument('--out',type=Path,required=True)
a=p.parse_args();port,tl,tools,otangle,out=[x.resolve() for x in [a.port,a.texlive,a.generators,a.otangle,a.out]]
if out.exists():raise SystemExit('fresh output required')
for root in [port,tl,tools,HERE,Path('/work'),Path('/inputs'),Path('/control/r2-xe-maps/inputs')]:
 if out==root or root in out.parents:raise SystemExit('protected output overlap')
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
try:pins=json.loads((HERE/'maps-inputs.json').read_text())
except (OSError,ValueError) as error:raise SystemExit('missing/invalid maps input pins') from error
for group,files in pins.items():
 for n,h in files.items():
  if sha((port if group=='port' else tl)/n)!=h:raise SystemExit('preimage mismatch: '+group+'/'+n)
out.mkdir(parents=True);commands=[];env=dict(os.environ,LC_ALL='C',WEBINPUTS='.:')
def run(label,cmd,cwd=out,data=None):
 r=subprocess.run(list(map(str,cmd)),cwd=cwd,input=data,env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=120)
 (out/(label+'.stdout')).write_bytes(r.stdout);(out/(label+'.stderr')).write_bytes(r.stderr)
 commands.append({'argv':list(map(str,cmd)),'cwd':str(cwd),'exit':r.returncode});(out/'commands.json').write_text(json.dumps(commands,indent=2)+'\n')
 if r.returncode:raise SystemExit(label+' failed; see '+str(out))
 return r.stdout
src=out/'source';run('base-prepare',['python3',HERE/'prepare.py',port,src])
helpers=out/'helpers';shutil.copytree(tl/'synctexdir',helpers/'synctexdir');shutil.copyfile(tl/'tex.ch',helpers/'tex.ch')
# The retained Xe WEB uses the same older base-TeX spelling as the accepted
# pdfTeX port. Reuse ONLY the exact tex.ch helper patch section, not the pdfTeX
# engine-specific tex.ch0 change. This is a change-file overlay, not generated C.
patch=(SHARED/'patches/tex-helpers.patch').read_text().split('--- a/pdftexdir/tex.ch0')[0]
(out/'tex-helper.patch').write_text(patch);run('tex-helper-patch',['patch','--batch','--fuzz=0','-p1','-i',out/'tex-helper.patch'],helpers)
shutil.copytree(helpers/'synctexdir',src/'synctexdir')
run('memory-sink-patch',['patch','--batch','--fuzz=0','-p1','-i',SHARED/'patches/synctex-memory.patch'],src)
for n in ['oc_synctex_memory_sink.h','oc_synctex_memory_sink.cpp','oc_synctex_producer.h','oc_synctex_internal.h']:shutil.copyfile(SHARED/n,src/n)
# Only this private Xe copy changes the checked closer to return its actual IO
# status for native dviclose's existing return contract. Accepted sink sources
# and all default/pdfTeX/DVI/Lua variants remain byte-for-byte untouched.
header=src/'oc_synctex_internal.h';header.write_text(header.read_text().replace('void oc_synctex_pdf_close(FILE *stream);','int oc_synctex_pdf_close(FILE *stream);'))
sink=src/'oc_synctex_memory_sink.cpp';text=sink.read_text()
old_close='''extern "C" void oc_synctex_pdf_close(FILE *stream)
{
    if (!attempted || !stream || pdf_closed_ok) {
        fail();
        if (stream) std::fclose(stream);
        return;
    }'''
new_close='''extern "C" int oc_synctex_pdf_close(FILE *stream)
{
    if (!attempted || !stream || pdf_closed_ok) {
        fail();
        return stream ? std::fclose(stream) : -1;
    }'''
if text.count(old_close)!=1:raise SystemExit('private sink closer preimage mismatch')
text=text.replace(old_close,new_close)
text=text.replace('    if (!pdf_closed_ok) fail();\n}', '    if (!pdf_closed_ok) fail();\n    return pdf_closed_ok ? 0 : -1;\n}')
sink.write_text(text)
shutil.copyfile(HERE/'maps-guard.h',src/'maps-guard.h')
(src/'oc_synctex_engine.h').write_text('#include "synctexdir/synctex-xetex.h"\n#include "oc_synctex_memory_sink.h"\n#include "oc_synctex_internal.h"\nextern char *oc_synctex_current_name(void);\n#define SYNCTEX_GET_CURRENT_NAME() oc_synctex_current_name()\n')
(src/'oc_synctex_port.c').write_text('''/* Genuine Xe UTF16 pool, complete native opened filename. Only the fixed
 * private /work/ mount prefix is translated to the captured relative namespace;
 * no basename matching, path resolution, source scan or Unicode normalization.
 * This also preserves the existing relative-only query ABI1 input contract. */
#define EXTERN extern
#include "xetexd.h"
char *oc_synctex_current_name(void) {
    char *name=gettexstring(fullsourcefilenamestack[inopen]);
    if (name && !strncmp(name,"/work/",6)) memmove(name,name+6,strlen(name+6)+1);
    return name;
}
''')
def patch_file(n,old,new):
 path=src/n;t=path.read_text()
 if t.count(old)!=1:raise SystemExit('owned seam preimage mismatch: '+n+': '+old[:60])
 path.write_text(t.replace(old,new))
patch_file('texmfmp.h','#include <string.h>','#include <string.h>\n#include <sys/types.h>\n#include "oc_synctex_internal.h"\n')
patch_file('main.c','static int oc_attempt_started = 0;','static int oc_attempt_started = 0;\n#include "oc_synctex_internal.h"\n#include "oc_synctex_memory_sink.h"\n#include "maps-guard.h"\nstatic int oc_maps_entry_set = 0;\nint oc_xe_maps_abi(void) { return 1; }')
patch_file('main.c','  if (oc_attempt_started++) return -10; /* One attempt per fresh module. */','  if (oc_attempt_started) { oc_mem_abort(); return -10; }\n  oc_attempt_started = 1;')
patch_file('main.c','int compileLaTeX() {\n    nopdfoutput = 1;','int compileLaTeX() {\n    if (oc_attempt_started || !oc_synctex_begin()) { oc_mem_abort(); return -10; }\n    synctexoption = oc_synctex_enabled() ? -1 : 0;\n    nopdfoutput = 1;')
patch_file('main.c','    if (strlen(main_entry_file) == 0) {\n      return -1;','    if (strlen(main_entry_file) == 0) {\n      oc_synctex_finish(-1);\n      oc_attempt_started = 1;\n      return -1;')
patch_file('main.c','    return _compile();\n}\n\nint compileFormat()', '    exit_code = -1;\n    const int actual = _compile();\n    oc_synctex_finish(actual == 0 && !oc_xe_native_output_valid() ? -5 : actual);\n    return actual;\n}\n\nint compileFormat()')
patch_file('main.c','int compileFormat() {','int compileFormat() {\n    oc_mem_discard();\n    synctexoption = 0;')
patch_file('main.c','    if (oc_attempt_started++) return -10;','    oc_mem_discard();\n    synctexoption = 0;\n    if (oc_attempt_started) { oc_mem_abort(); return -10; }\n    oc_attempt_started = 1;')
patch_file('main.c','int setMainEntry(const char *p) {','int setMainEntry(const char *p) {\n    if (oc_attempt_started || oc_maps_entry_set) { oc_mem_abort(); return -2; }\n    oc_maps_entry_set = 1;')
patch_file('xetexdir/XeTeX_ext.c', '''#ifdef WEBASSEMBLY_BUILD
    if (fclose(fptr) != 0)
        return errno;
    return 0;
#else
    if (nopdfoutput) {''', '''#ifdef WEBASSEMBLY_BUILD
    /* Native dviclose reached only after actual final XDV postamble/flush. */
    return oc_synctex_pdf_close(fptr) == 0 ? 0 : (errno ? errno : EIO);
#else
    if (nopdfoutput) {''')
# The newer common.defines already declares this C ABI type; the retained Xe
# definitions repeat it, which is a Web2C syntax error (not a generated-C edit).
patch_file('xetexdir/xetex.defines','@define type gzFile;','{ gzFile is supplied by the pinned common.defines. }')
# Genuine XeTeX change order from xetex.am/synctex.am. tracingstacklevels is a
# later unrelated integer-parameter ABI and conflicts with this retained old Xe
# start_input preimage. It is intentionally absent, as in the existing port.
s=helpers/'synctexdir';w=tl
sequence=[src/'xetexdir/xetex.web',w/'xetexdir/tex.ch0',helpers/'tex.ch']+[s/n for n in ['synctex-xe-def.ch0','synctex-mem.ch0','synctex-e-mem.ch0','synctex-e-mem.ch1','synctex-rec.ch0','synctex-e-rec.ch0']]+[src/'xetexdir/xetex.ch',s/'synctex-xe-rec.ch3',w/'xetexdir/char-warning-xetex.ch',w/'tex-binpool.ch',HERE/'maps-abi.ch']
gen=out/'regenerate';gen.mkdir();run('tie',[tools/'tie','-c','xetex-final.ch',*sequence],gen)
run('otangle',[otangle,src/'xetexdir/xetex.web','xetex-final.ch'],gen)
defs=[w/'web2c/common.defines',w/'web2c/texmf.defines',s/'synctex.defines',src/'xetexdir/xetex.defines',SHARED/'producer.defines']
data=b''.join(q.read_bytes() for q in defs)+(gen/'xetex.p').read_bytes()
data=run('web2c',[tools/'web2c/web2c','-htexmfmp.h','-t','-cxetexcoerce'],gen,data)
data=run('fixwrites',[tools/'web2c/fixwrites','-t','xetex'],gen,data)
run('splitup',[tools/'web2c/splitup','-i','-l','65000','xetex'],gen,data)
coerce=gen/'xetexcoerce.h';coerce.write_bytes(coerce.read_bytes()+(w/'web2c/coerce.h').read_bytes())
(gen/'xetex-pool.c').write_bytes(run('makecpool',[tools/'web2c/makecpool','xetex'],gen))
for n in ['xetex0.c','xetexini.c','xetexd.h','xetexcoerce.h','xetex-pool.c']:shutil.copyfile(gen/n,src/'tex'/n)
(out/'source-hashes.json').write_text(json.dumps({str(q.relative_to(src)):sha(q) for q in sorted(src.rglob('*')) if q.is_file()},indent=2)+'\n')
(out/'generation-inputs.json').write_text(json.dumps({'schemaVersion':1,'changeOrder':[{ 'path':str(q),'sha256':sha(q)} for q in sequence],'tools':{str(q):sha(q) for q in [otangle,tools/'tie',tools/'web2c/web2c',tools/'web2c/fixwrites',tools/'web2c/splitup',tools/'web2c/makecpool']},'recipeSha256':sha(__file__),'sharedReadOnlyReuse':{str(q):sha(q) for q in [SHARED/'prepare.py',SHARED/'producer.defines'] if q.exists()}},indent=2)+'\n')
print('Prepared actual Xe-specific regenerated source; genuine new format required')
