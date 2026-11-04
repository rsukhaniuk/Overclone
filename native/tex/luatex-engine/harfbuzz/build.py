#!/usr/bin/env python3
"""Build a private LuaHBTeX candidate from approved Lua and HarfBuzz inputs.

The four upstream LuaHBTeX-specific units are rebuilt with the real HarfBuzz
module. Existing browser restrictions, ABI5 maps and graphics observers remain.
This command does not change runtime pins, formats or application defaults.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import shlex
import subprocess
import sys


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("source", "headers", "lua-build", "geometry-build", "graphics-build",
                 "hb-dependencies", "hb-includes", "cache", "work", "out"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--font-closure", action="store_true",
                        help="candidate-only 128MiB runtime/32MiB work inputs; heap stays 512MiB")
    args = parser.parse_args()
    values = {name: value.resolve() for name, value in vars(args).items() if isinstance(value, Path)}
    work, out = values["work"], values["out"]
    if work == out or work in out.parents or out in work.parents:
        parser.error("build and output directories must be disjoint")
    for target in (work, out):
        if target.exists():
            parser.error("fresh output directory required: " + str(target))
        for name, root in values.items():
            if name not in ("work", "out") and (target == root or root in target.parents):
                parser.error("output overlaps a retained input")
    source, headers = values["source"], values["headers"]
    lua, geometry = values["lua_build"], values["geometry_build"]
    graphics, hbdeps = values["graphics_build"], values["hb_dependencies"]
    here = Path(__file__).resolve().parent
    parent = here.parent
    if digest(parent / "prepare.py") != "764f3b425638e9046c64c7f4effa78f0ec02f05d62e9b645a59697277c2583c5":
        raise SystemExit("browser restriction recipe changed")
    proof = json.loads((hbdeps / "proof.json").read_text())
    if proof.get("status") != "pass":
        raise SystemExit("complete HarfBuzz dependency proof required")
    for name, entry in proof["archives"].items():
        if digest(hbdeps / (name + ".a")) != entry["actualSha256"]:
            raise SystemExit("dependency archive changed: " + name)
    # Reusing an archive requires equality of the source/header closure that
    # produced it, including generated configuration and SDK headers.
    checked_headers = {}
    for unit in proof["units"].values():
        if digest(unit["source"]) != unit["sourceSha256"]:
            raise SystemExit("dependency source changed")
        for name, expected in unit["headers"].items():
            if name in checked_headers and checked_headers[name] != expected:
                raise SystemExit("inconsistent dependency header proof: " + name)
            checked_headers[name] = expected
    for name, expected in checked_headers.items():
        if digest(name) != expected:
            raise SystemExit("dependency header changed: " + name)
    work.mkdir(parents=True)
    out.mkdir(parents=True)
    env = dict(os.environ, EM_CACHE=str(values["cache"]), EM_FROZEN_CACHE="1", EMCC_CORES="1")
    commands = []

    def run(label, command):
        command = list(map(str, command))
        with (work / (label + ".log")).open("wb") as log:
            result = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=180)
        commands.append({"argv": command, "exit": result.returncode})
        (work / "commands.json").write_text(json.dumps(commands, indent=2) + "\n")
        if result.returncode:
            raise SystemExit(label + " failed; inspect its retained log")

    run("browser-overlay", [sys.executable, parent / "prepare.py", source, work / "overlay"])
    web = source / "texk/web2c"
    hbsource = source / "libs/harfbuzz/harfbuzz-src/src"
    inc = ["-DHAVE_CONFIG_H", "-DLUATEX_HARFBUZZ_ENABLED", "-DOC_LUATEX_ABI=5",
           "-DOC_LUA_GRAPHICS=1", "-DLUA_COMPAT_MODULE", "-DLUA_COMPAT_5_2",
           "-DLUAI_HASHLIMIT=6", "-DLUA_USE_POSIX", "-DpdfTeX",
           "-Dextra_version_info=-browser-harfbuzz1", "-DSYNCTEX_ENGINE_H=<synctex-luatex.h>"]
    include_dirs = [headers / "texk/web2c", headers / "texk/web2c/w2c", web,
                    headers / "texk", source / "texk", web / "libmd5",
                    web / "luatexdir", web / "mplibdir", web / "synctexdir",
                    values["hb_includes"], hbsource]
    include_dirs += [headers / ("libs/" + name + "/include") for name in ("zlib", "libpng", "pplib", "lua53")]
    inc += ["-I" + str(path) for path in include_dirs]
    font_flags = ["-DOC_LUA_FONT_CLOSURE=1"] if args.font_closure else []
    inc += font_flags
    flags = ["-O1", "-MMD", "-ffile-prefix-map=" + str(work / "overlay") + "=/inputs/texlive/texk/web2c",
             "-Wno-error=incompatible-pointer-types",
             "-Wno-error=incompatible-function-pointer-types", "-Wno-error=implicit-function-declaration"]
    shutil.copyfile(lua / "browser-specific.a", work / "browser-harfbuzz-specific.a")
    sources = {
        "utils": web / "luatexdir/utils/utils.c",
        "luastuff": work / "overlay/luatexdir/lua/luastuff.c",
        "luainit": work / "overlay/luatexdir/lua/luainit.c",
        "printing": web / "luatexdir/tex/printing.c",
    }
    for name, path in sources.items():
        obj = work / ("libluatexspecific_a-" + name + ".o")
        run(name, ["emcc", *inc, *flags, "-c", path, "-o", obj])
        run(name + "-archive", ["emar", "r", work / "browser-harfbuzz-specific.a", obj])
    module_objects = []
    for path in sorted((web / "luatexdir/luaharfbuzz/src/luaharfbuzz").glob("*.c")):
        obj = work / ("harfbuzz-" + path.stem + ".o")
        run("harfbuzz-" + path.stem, ["emcc", *inc, *flags, "-c", path, "-o", obj])
        module_objects.append(obj)
    run("entry", ["emcc", *inc, *flags, "-Dmain=oc_engine_main", "-c",
                  web / "luatexdir/luatex.c", "-o", work / "luahbtex-entry.o"])
    run("capability", ["em++", "-std=c++17", "-O1", *font_flags, "-c", here / "capability.cpp",
                       "-o", work / "capability.o"])
    run("engine", ["em++", "-std=c++17", *inc, *flags, "-DOC_LUA_HARFBUZZ=1",
                   "-c", parent / "engine.cpp", "-o", work / "engine.o"])
    deps = lua / "wasm"
    archives = [deps / "texk/web2c/mplibdir/luatex-lmplib.o",
                work / "browser-harfbuzz-specific.a", graphics / "browser-luatex.a"]
    archives += [deps / name for name in (
        "texk/web2c/libff.a", "texk/web2c/libluamisc.a", "libs/lua53/.libs/libtexlua53.a",
        "texk/web2c/libmplibcore.a", "libs/zziplib/libzzip.a", "libs/libpng/libpng.a",
        "libs/pplib/libpplib.a", "libs/zlib/libz.a", "texk/web2c/lib/lib.a",
        "texk/kpathsea/.libs/libkpathsea.a", "texk/web2c/libmputil.a",
        "texk/web2c/libunilib.a", "texk/web2c/libmd5.a")]
    archives += [hbdeps / (name + ".a") for name in ("harfbuzz", "graphite", "freetype", "icu-i18n", "icu-common", "icu-stub")]
    exports = ["malloc", "free", "oc_luatex_abi", "oc_lua_graphics_abi", "oc_lua_harfbuzz_abi",
               "oc_luatex_version", "oc_luatex_configure", "oc_luatex_context", "oc_luatex_add_file",
               "oc_luatex_declare_output", "oc_luatex_named_output_size", "oc_luatex_copy_named_output",
               "oc_luatex_compile_format", "oc_luatex_compile_latex", "oc_luatex_output_size",
               "oc_luatex_copy_output", "oc_luatex_error", "oc_luatex_budget_json", "oc_mem_configure",
               "oc_synctex_abi", "oc_synctex_status", "oc_synctex_length", "oc_synctex_copy",
               "oc_synctex_geometry_length", "oc_synctex_geometry_copy"]
    vfs_budget = parent / "vfs-budget.js"
    if args.font_closure:
        exports.append("oc_lua_font_closure_abi")
        old = "if (baselineBytes > 100663296 + 4096)"
        text = vfs_budget.read_text()
        if text.count(old) != 1:
            raise SystemExit("VFS baseline budget seam changed")
        vfs_budget = work / "font-vfs-budget.js"
        old_work = "cap = 16777216, logCap = 262144"
        if text.count(old_work) != 1:
            raise SystemExit("VFS allocation work seam changed")
        text = text.replace(old, "if (baselineBytes > 167772160 + 4096)")
        # Normal SDK growth of an actual 2.5MiB PDF exhausted the old 16MiB
        # cumulative backing/copy-work allowance. Keep each output at 8MiB,
        # the native heap at 512MiB, and bound total allocation work explicitly.
        vfs_budget.write_text(text.replace(old_work, "cap = 67108864, logCap = 262144"))
    objects = [work / "engine.o", graphics / "writer_geometry.o", graphics / "writer_graphics.o",
               geometry / "lua_maps.o", geometry / "sink.o", work / "luahbtex-entry.o",
               work / "capability.o", *module_objects]
    run("link", ["em++", "-O1", *objects, *archives,
                 "-Wl,--wrap=exit", "-Wl,--wrap=system", "-Wl,--wrap=popen",
                 "-sMODULARIZE=1", "-sEXPORT_NAME=createLuaTeX", "-sENVIRONMENT=web,worker,node",
                 "-sINVOKE_RUN=0", "-sALLOW_MEMORY_GROWTH=1", "-sINITIAL_MEMORY=33554432",
                 "-sMAXIMUM_MEMORY=536870912", "-sSTACK_SIZE=5242880", "-sFORCE_FILESYSTEM=1",
                 "-sFILESYSTEM=1", "-sALLOW_TABLE_GROWTH=0", "--pre-js", parent / "vfs-shapes.js",
                 "--pre-js", vfs_budget,
                 '-sEXPORTED_RUNTIME_METHODS=["ccall","cwrap","UTF8ToString","FS","HEAPU8"]',
                 "-sEXPORTED_FUNCTIONS=" + json.dumps(["_" + name for name in exports]),
                 "-o", out / "luatex.js"])
    consumed_headers = {}
    for depfile in work.glob("*.d"):
        dependencies = depfile.read_text().replace("\\\n", "").split(":", 1)[1]
        for name in shlex.split(dependencies):
            path = Path(name)
            consumed_headers[str(path)] = digest(path)
    sources.update({"entry": web / "luatexdir/luatex.c", "engine": parent / "engine.cpp",
                    "capability": here / "capability.cpp"})
    for path in (web / "luatexdir/luaharfbuzz/src/luaharfbuzz").glob("*.c"):
        sources["module-" + path.stem] = path
    manifest = {"schemaVersion": 1, "status": "candidate-not-adopted", "nativeAbi": 5,
                "capabilities": {"graphics": 1, "harfbuzz": 1, "fontClosure": int(args.font_closure)},
                "inputLimits": {"runtime": 134217728 if args.font_closure else 100663296,
                                "work": 33554432 if args.font_closure else None,
                                "aggregate": 167772160 if args.font_closure else 100663296,
                                "allocationWork": 67108864 if args.font_closure else 16777216,
                                "heap": 536870912},
                "vfsBudgetSha256": digest(vfs_budget), "commands": commands,
                "recipeSha256": digest(__file__), "dependencyProofSha256": digest(hbdeps / "proof.json"),
                "inputs": {str(path): digest(path) for path in objects + archives if path not in module_objects and work not in path.parents},
                "sources": {str(path): digest(path) for path in sources.values()},
                "consumedHeaders": consumed_headers,
                "outputs": {name: {"length": (out / name).stat().st_size, "sha256": digest(out / name)}
                            for name in ("luatex.js", "luatex.wasm")}}
    (out / "harfbuzz-build.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps(manifest["outputs"], indent=2))


if __name__ == "__main__":
    main()
