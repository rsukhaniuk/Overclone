#!/usr/bin/env python3
"""Private, fail-closed source overlay + real TIE/TANGLE/Web2C; NEVER emcc/link.
Only --out is written. Existing output is refused. No acquisition/network/cache.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

HERE = Path(__file__).resolve().parent

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--port', type=Path, required=True)
    ap.add_argument('--texlive', type=Path, required=True)
    ap.add_argument('--generators', type=Path, required=True,
                    help='directory containing tie, tangle, web2c/{web2c,fixwrites,splitup,makecpool}')
    ap.add_argument('--out', type=Path, required=True)
    args = ap.parse_args()
    roots = {'port': args.port.resolve(), 'texlive': args.texlive.resolve(),
             'tools': args.generators.resolve()}
    try:
        pins = json.loads((HERE / 'inputs.json').read_text())
    except (OSError, ValueError) as error:
        raise SystemExit(f'Invalid input pins: {error}') from error
    for group, files in pins.items():
        for rel, expected in files.items():
            path = roots[group] / rel
            if not path.is_file() or digest(path) != expected:
                raise SystemExit(f'Preimage mismatch/missing: {group}/{rel}')
    out = args.out.resolve()
    if out.exists():
        raise SystemExit('Output must not exist; use a fresh private directory')
    # No writing inside source/input trees, repository or overlay itself.
    for root in [*roots.values(), HERE, Path('/work')]:
        if out == root or root in out.parents:
            raise SystemExit(f'Output overlaps protected source: {root}')
    out.mkdir(parents=True)
    logdir = out / 'logs'
    logdir.mkdir()
    commands = []
    env = dict(os.environ, WEBINPUTS='.:', LC_ALL='C')

    def run(command, cwd, log, data=None):
        commands.append({'cwd': str(cwd), 'argv': list(map(str, command)), 'log': log})
        result = subprocess.run(list(map(str, command)), cwd=cwd, env=env, input=data,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        (logdir / (log + '.stdout')).write_bytes(result.stdout)
        (logdir / (log + '.stderr')).write_bytes(result.stderr)
        (out / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
        if result.returncode:
            raise SystemExit(f'{log}: exit {result.returncode}, see {logdir}')
        return result.stdout

    port = roots['port']
    web = roots['texlive'] / 'texk/web2c'
    helpers = out / 'helpers'
    helpers.mkdir()
    for rel in pins['texlive']:
        if not rel.startswith('texk/web2c/'):
            continue
        local = helpers / rel.removeprefix('texk/web2c/')
        local.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(roots['texlive'] / rel, local)
    run(['patch', '--batch', '--fuzz=0', '-p1', '-i', HERE / 'patches/tex-helpers.patch'],
        helpers, 'helpers-patch')
    try:
        order = json.loads((HERE / 'order.json').read_text())
    except (OSError, ValueError) as error:
        raise SystemExit(f'Invalid change order: {error}') from error
    receipts = {}
    for mode in ('off', 'on'):
        dest = out / mode
        shutil.copytree(port, dest)
        # Original generated files are not trusted as regeneration outputs.
        try:
            shutil.rmtree(dest / 'tex')
        except OSError as error:
            raise SystemExit(f'Cannot clear private generation target: {error}') from error
        (dest / 'tex').mkdir()
        run(['patch', '--batch', '--fuzz=0', '-p1', '-i', HERE / 'patches/selected-web.patch'],
            dest, mode + '-web-patch')
        for name in ('oc_synctex_memory_sink.h', 'oc_synctex_memory_sink.cpp',
                     'oc_synctex_producer.h', 'oc_synctex_internal.h',
                     'oc_synctex_port.c', 'oc_synctex_engine.h'):
            shutil.copyfile(HERE / name, dest / name)
        if mode == 'on':
            shutil.copytree(helpers / 'synctexdir', dest / 'synctexdir')
            for name in ('synctex-memory.patch', 'driver.patch', 'port-header.patch'):
                run(['patch', '--batch', '--fuzz=0', '-p1', '-i', HERE / 'patches' / name],
                    dest, mode + '-' + name)
        generate = dest / 'regenerate'
        generate.mkdir()
        sequence = order['prefix'] + (order['synctex'] if mode == 'on' else []) + order['suffix']
        paths = [(dest if rel in ('pdftexdir/pdftex.web', 'pdftexdir/pdftex.ch') else helpers) / rel
                 for rel in sequence]
        paths += [HERE / 'port-abi.ch']
        if mode == 'on':
            paths += [HERE / 'producer.ch']
        run([roots['tools'] / 'tie', '-c', 'pdftex-final.ch', *paths], generate, mode + '-tie')
        run([roots['tools'] / 'tangle', dest / 'pdftexdir/pdftex.web', 'pdftex-final.ch'],
            generate, mode + '-tangle')
        definitions = [helpers / 'web2c/common.defines', helpers / 'web2c/texmf.defines',
                       helpers / 'synctexdir/synctex.defines', dest / 'pdftexdir/pdftex.defines']
        if mode == 'on':
            definitions += [HERE / 'producer.defines']
        data = b''.join(f.read_bytes() for f in definitions) + (generate / 'pdftex.p').read_bytes()
        data = run([roots['tools'] / 'web2c/web2c', '-htexmfmp.h', '-t', '-cpdftexcoerce'],
                   generate, mode + '-web2c', data)
        data = run([roots['tools'] / 'web2c/fixwrites', '-t', 'pdftex'],
                   generate, mode + '-fixwrites', data)
        run([roots['tools'] / 'web2c/splitup', '-i', '-l', '65000', 'pdftex'],
            generate, mode + '-splitup', data)
        coerce = generate / 'pdftexcoerce.h'
        coerce.write_bytes(coerce.read_bytes() + (helpers / 'web2c/coerce.h').read_bytes())
        pool = run([roots['tools'] / 'web2c/makecpool', 'pdftex'], generate, mode + '-makecpool')
        (generate / 'pdftex-pool.c').write_bytes(pool)
        for name in ('pdftex0.c', 'pdftexini.c', 'pdftexd.h', 'pdftexcoerce.h', 'pdftex-pool.c'):
            shutil.copyfile(generate / name, dest / 'tex' / name)
        receipts[mode] = {str(f.relative_to(dest)): digest(f)
                          for f in sorted(dest.rglob('*')) if f.is_file()}
    (out / 'candidate-hashes.json').write_text(json.dumps(receipts, indent=2) + '\n')
    print(f'READY FOR MASTER REVIEW ONLY: {out}; no product build/format/acceptance performed')

if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, shutil.Error) as error:
        raise SystemExit(f'Preparation refused: {error}') from error
