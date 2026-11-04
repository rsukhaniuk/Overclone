#!/usr/bin/env python3
"""Offline C ABI link of the separately built, pinned genuine Perl/XS tree.

No downloader or package installer is called here. Run in the locked build image
with network disabled; --build-root is the output of the source build recipe.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shlex
import shutil
import subprocess


def digest(path):
    data = path.read_bytes()
    return {"length": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-root", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    root, out = args.build_root.resolve(), args.out.resolve()
    if out.exists():
        raise SystemExit("fresh output directory required")
    out.mkdir(parents=True)
    source = Path(__file__).resolve().parent
    perl = root / "src/perl"
    config = dict(re.findall(r"^(\w+)='(.*)'$", (perl / "config.sh").read_text(), re.M))
    if config["version"] != "5.38.2" or config["archname"] != "wasm":
        raise SystemExit("unexpected Perl source configuration")
    main_source = (perl / "perlmain.c").read_text()
    marker = "emperl_end_perl(void)\n{\n"
    if main_source.count(marker) != 1:
        raise SystemExit("unsupported genuine Perl cleanup shape")
    instrumented = main_source.replace(marker, marker + "    extern void oc_biber_cleanup_started(void);\n    oc_biber_cleanup_started();\n")
    (out / "perlmain.c").write_text(instrumented)
    commands = []

    def run(command, cwd=out):
        commands.append([str(v) for v in command])
        subprocess.run(command, cwd=cwd, check=True)

    common = ["-O2", "-fstack-protector-strong"]
    run(["emcc", *common, *shlex.split(config["ccflags"]), "-I"+str(perl),
         "-Dmain=oc_genuine_perl_main", "-c", out/"perlmain.c", "-o", out/"perlmain.o"])
    run(["em++", *common, "-std=c++17", "-Wall", "-Wextra", "-Werror", "-fno-exceptions",
         "-c", source/"engine.cpp", "-o", out/"engine.o"])
    run(["emcc", *common, "-Wall", "-Wextra", "-Werror", "-c", source/"restrictions.c", "-o", out/"restrictions.o"])
    # Replace all Biber code AND data with the exact genuine release. Ubuntu's
    # pure-Perl dependency closure supplies dependencies, never a different Biber.
    stage = root / "prefix-stage"
    site = stage / "lib/site_perl/5.38.2"
    genuine = root / "src/biber"
    shutil.rmtree(site / "Biber")
    (site / "Biber.pm").unlink()
    shutil.copytree(genuine / "lib", site, dirs_exist_ok=True)
    shutil.copyfile(genuine / "bin/biber", stage / "bin/biber")
    data_files = {
        "data/schemata/config.rnc": "config.rnc", "data/schemata/config.rng": "config.rng",
        "data/schemata/bcf.rnc": "bcf.rnc", "data/schemata/bcf.rng": "bcf.rng",
        "data/biber-tool.conf": "biber-tool.conf", "data/bcf.xsl": "bcf.xsl",
    }
    for src, dst in data_files.items():
        shutil.copyfile(genuine/src, site/"Biber"/dst)
    archives = [perl / "lib/auto" / ext / (ext.rsplit("/", 1)[-1]+".a")
                for ext in config["static_ext"].split()]
    if not archives or any(not path.is_file() for path in archives):
        raise SystemExit("missing static XS archive")
    exports = ["_oc_biber_"+name for name in ["abi", "configure", "add_file", "run", "output_size", "copy_output", "error"]]+["_malloc", "_free"]
    methods = ["FS", "HEAPU8", "UTF8ToString", "stringToUTF8", "lengthBytesUTF8", "ccall"]
    wraps = ["exit", "_exit", "system", "popen", "fork", "vfork", "execve", "execv", "execvp",
             "socket", "socketpair", "connect", "bind", "listen", "accept"]
    run(["em++", *common, "--no-entry", out/"engine.o", out/"restrictions.o", out/"perlmain.o",
         *archives, perl/"libperl.a", *shlex.split((perl/"ext.libs").read_text()),
         "-Wl,--threads=1", *["-Wl,--wrap="+name for name in wraps],
         "-sERROR_ON_UNDEFINED_SYMBOLS=1", "-sALLOW_MEMORY_GROWTH=1", "-sMAXIMUM_MEMORY=536870912",
         "-sMODULARIZE=1", "-sEXPORT_NAME=createBiber", "-sLZ4=1", "-sENVIRONMENT=web,worker,node",
         "-sEXPORTED_FUNCTIONS="+json.dumps(exports), "-sEXPORTED_RUNTIME_METHODS="+json.dumps(methods),
         "--pre-js", source/"vfs-budget.js", "--preload-file", str(stage)+"@/work/runtime/perl", "-o", out/"biber.js"])
    loader = (out/"biber.js").read_text()
    # SDK package glue runs before --pre-js. Remove its complete network/Node
    # fallback before freezing the loader, then require explicit trusted inputs
    # at the factory entrance. No IndexedDB/preload cache is linked.
    begin = 'if(!fetched)fetchPromise=async function(packageName,packageSize){'
    end = '}(REMOTE_PACKAGE_NAME,REMOTE_PACKAGE_SIZE);'
    if loader.count(begin) != 1 or loader.count(end) != 1:
        raise SystemExit("unsupported SDK6 bootstrap loader")
    at = loader.index(begin); stop = loader.index(end, at)+len(end)
    removed = loader[at:stop]
    loader = loader[:at]+'if(!(fetched instanceof ArrayBuffer)||fetched.byteLength!==REMOTE_PACKAGE_SIZE)throw Error("invalid trusted Biber bootstrap");'+loader[stop:]
    marker = 'var Module=moduleArg;'
    if loader.count(marker) != 1:
        raise SystemExit("unsupported modular loader entrance")
    loader = loader.replace(marker, marker+'if(!(Module["wasmBinary"] instanceof Uint8Array)||typeof Module["getPreloadedPackage"]!=="function")throw Error("explicit trusted Biber artifacts required");')
    (out/"biber.js").write_text(loader)
    inputs = {str(path.relative_to(source)): digest(path) for path in source.rglob("*")
              if path.is_file() and path.suffix in (".cpp", ".h", ".c", ".js", ".py", ".json", ".patch", ".sh")}
    receipt = {"schemaVersion": 1, "status": "genuine-source-C-ABI-linked-not-accepted", "abi": 1,
               "program": "Biber 2.19", "interpreter": "Perl 5.38.2", "networkDuringBuild": False,
               "inputs": inputs, "commands": commands, "overlays": {
                   "genuineMainBefore": hashlib.sha256(main_source.encode()).hexdigest(),
                   "genuineMainAfter": digest(out/"perlmain.c"),
                   "removedBootstrapFallback": {"length": len(removed), "sha256": hashlib.sha256(removed.encode()).hexdigest()},
                   "description": "rename genuine main; observe cleanup once; deny process/network; require trusted bootstrap; exact official Biber data_files"},
               "files": [{"name": name, **digest(out/name)} for name in ["biber.js", "biber.wasm", "biber.data"]]}
    (out/"receipt.json").write_text(json.dumps(receipt, indent=2)+"\n")
    print(json.dumps({"status": receipt["status"], "files": receipt["files"]}))


if __name__ == "__main__":
    main()
