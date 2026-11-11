#!/usr/bin/env python3
"""Regenerate an explicit non-BMP Xe candidate from the accepted maps source.

Inputs and existing runtimes are read-only. No downloads or generated-C edits.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

HERE = Path(__file__).resolve().parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--base', type=Path, required=True,
                    help='accepted prepare-maps output, with source and generation receipts')
parser.add_argument('--runtime', type=Path, required=True,
                    help='project-owned xetex-engine/runtime.cpp')
parser.add_argument('--out', type=Path, required=True)
parser.add_argument('--work-files', type=int, choices=[192, 256], default=192,
                    help='explicit new candidate limit; historical P13 generation remains 192')
args = parser.parse_args()
base, runtime, out = (p.resolve() for p in (args.base, args.runtime, args.out))
if out.exists():
    raise SystemExit('fresh output required')
if base == out or base in out.parents or HERE == out or HERE in out.parents:
    raise SystemExit('protected output overlap')


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


lock = json.loads((HERE / 'inputs.lock.json').read_text())
for relative, key in [('source-hashes.json', 'baseSourceManifestSha256'),
                      ('generation-inputs.json', 'baseGenerationManifestSha256'),
                      ('regenerate/xetex-final.ch', 'baseCombinedChangeSha256')]:
    if sha(base / relative) != lock[key]:
        raise SystemExit('base receipt mismatch: ' + relative)
for relative, expected in json.loads((base / 'source-hashes.json').read_text()).items():
    if sha(base / 'source' / relative) != expected:
        raise SystemExit('base source mismatch: ' + relative)
generation = json.loads((base / 'generation-inputs.json').read_text())
for item in generation['changeOrder']:
    if sha(item['path']) != item['sha256']:
        raise SystemExit('base generation input mismatch: ' + item['path'])
for path, expected in generation['tools'].items():
    if sha(path) != expected:
        raise SystemExit('generator mismatch: ' + path)

out.mkdir(parents=True)
source = out / 'source'
shutil.copytree(base / 'source', source)
commands = []
env = dict(os.environ, LC_ALL='C', WEBINPUTS='.:')


def run(label, command, cwd=out, data=None):
    command = list(map(str, command))
    result = subprocess.run(command, cwd=cwd, input=data, env=env,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120)
    (out / (label + '.stdout')).write_bytes(result.stdout)
    (out / (label + '.stderr')).write_bytes(result.stderr)
    commands.append({'argv': command, 'cwd': str(cwd), 'exit': result.returncode})
    (out / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
    if result.returncode:
        raise SystemExit(label + ' failed; see ' + str(out))
    return result.stdout


def patch(relative, before, after):
    path = source / relative
    text = path.read_text()
    if text.count(before) != 1:
        raise SystemExit('preimage mismatch: ' + relative + ': ' + before[:65])
    path.write_text(text.replace(before, after))


# The retained handwritten encoder also omitted the 0x10000 surrogate offset.
# Bounds-check before consuming a low surrogate; invalid TeX-created surrogates
# become U+FFFD, while original malformed UTF-8 paths still fail oc_path_ok.
patch('texmfmp.c', '''    if (c >= 0xD800 && c <= 0xDBFF) {
      unsigned lo = strpool[++i + strstart[s - 65536L]];
      if (lo >= 0xDC00 && lo <= 0xDFFF)
        c = (c - 0xD800) * 0x0400 + lo - 0xDC00;
      else
        c = 0xFFFD;
    }''', '''    if (c >= 0xD800 && c <= 0xDBFF) {
      if (i + 1 < len) {
        unsigned lo = strpool[i + 1 + strstart[s - 65536L]];
        if (lo >= 0xDC00 && lo <= 0xDFFF) {
          ++i;
          c = 0x10000 + (c - 0xD800) * 0x0400 + lo - 0xDC00;
        } else c = 0xFFFD;
      } else c = 0xFFFD;
    } else if (c >= 0xDC00 && c <= 0xDFFF) c = 0xFFFD;''')

# Candidate-only native capability: accepted runtime.cpp and old builders retain
# the BMP guard. Removing it here is conditional on the genuine regenerated scan.
text = runtime.read_text()
before = '''        // This retained engine's generated filename scanner accepts UTF-16 BMP
        // characters only. Reject unsupported main names instead of truncating.
        if (writing == 2 && value > 0xffff) return 0;'''
if text.count(before) != 1:
    raise SystemExit('runtime admission preimage mismatch')
text = text.replace(before, '        // Non-BMP scanning is proved by this explicit candidate ABI.')
text += '\nextern "C" int oc_xe_nonbmp_abi(void) { return 1; }\n'
if args.work_files == 256:
    before = 'scan("/work", 0, count, bytes, 192, 8388608, 33554432)'
    if text.count(before) != 1:
        raise SystemExit('exact combined-work file-limit preimage required')
    text = text.replace(before, 'scan("/work", 0, count, bytes, 256, 8388608, 33554432)')
    text += '\nextern "C" int oc_xe_work_file_limit(void) { return 256; }\n'

(source / 'oc_nonbmp_runtime.cpp').write_text(text)

tools = {Path(p).name: Path(p) for p in generation['tools']}
texlive = next(Path(item['path']).parent.parent for item in generation['changeOrder']
               if item['path'].endswith('/xetexdir/tex.ch0'))
shared = HERE.parent.parent / 'synctex-producer'
work = out / 'regenerate'
work.mkdir()
web = source / 'xetexdir/xetex.web'
run('tie', [tools['tie'], '-c', 'xetex-final.ch', web,
            base / 'regenerate/xetex-final.ch', HERE / 'nonbmp.ch'], work)
run('otangle', [tools['otangle'], web, 'xetex-final.ch'], work)
definitions = [texlive / 'web2c/common.defines', texlive / 'web2c/texmf.defines',
               base / 'helpers/synctexdir/synctex.defines',
               source / 'xetexdir/xetex.defines', shared / 'producer.defines']
data = b''.join(p.read_bytes() for p in definitions) + (work / 'xetex.p').read_bytes()
data = run('web2c', [tools['web2c'], '-htexmfmp.h', '-t', '-cxetexcoerce'], work, data)
data = run('fixwrites', [tools['fixwrites'], '-t', 'xetex'], work, data)
run('splitup', [tools['splitup'], '-i', '-l', '65000', 'xetex'], work, data)
coerce = work / 'xetexcoerce.h'
coerce.write_bytes(coerce.read_bytes() + (texlive / 'web2c/coerce.h').read_bytes())
(work / 'xetex-pool.c').write_bytes(run('makecpool', [tools['makecpool'], 'xetex'], work))
for name in ['xetex0.c', 'xetexini.c', 'xetexd.h', 'xetexcoerce.h', 'xetex-pool.c']:
    shutil.copyfile(work / name, source / 'tex' / name)
result = {
    'schemaVersion': 1, 'capability': 'candidate-only oc_xe_nonbmp_abi()==1',
    'baseSourceManifestSha256': sha(base / 'source-hashes.json'),
    'baseGenerationManifestSha256': sha(base / 'generation-inputs.json'),
    'baseCombinedChangeSha256': sha(base / 'regenerate/xetex-final.ch'),
    'recipeSha256': sha(__file__), 'overlaySha256': sha(HERE / 'nonbmp.ch'),
    'runtimeSourceSha256': sha(runtime), 'combinedWorkFileLimit': args.work_files,
    'tools': generation['tools'], 'commands': commands,
    'source': {str(p.relative_to(source)): sha(p) for p in sorted(source.rglob('*')) if p.is_file()}
}
(out / 'manifest.json').write_text(json.dumps(result, indent=2) + '\n')
print('Regenerated explicit non-BMP Xe candidate; no runtime/profile adoption')
