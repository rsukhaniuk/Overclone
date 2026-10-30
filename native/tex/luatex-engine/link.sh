#!/bin/bash
# Inputs: approved TL source, built private TL WASM tree, native Lua 5.3 generator.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
source=${TL_SOURCE:-/inputs/texlive}
build=${TL_BUILD:-/control/build/wasm}
overlay=${TL_OVERLAY:-/control/build/overlay}
out=${OUT:-/control/candidate}
export EM_CACHE=${EM_CACHE:-/control/cache} TMPDIR=/control/build
export EM_FROZEN_CACHE=1
export http_proxy=http://127.0.0.1:9 https_proxy=http://127.0.0.1:9 all_proxy=http://127.0.0.1:9
export HTTP_PROXY=$http_proxy HTTPS_PROXY=$https_proxy ALL_PROXY=$all_proxy no_proxy= NO_PROXY=
ulimit -v 3145728
mkdir -p "$out"
if [[ ! -e "$overlay" ]]; then python3 "$here/prepare.py" "$source" "$overlay"; fi
w2c=$source/texk/web2c
b=$build/texk/web2c
if [[ ${RELINK_ONLY:-0} != 1 ]]; then
 (
  cd "$overlay/luatexdir/lua"
  /control/build/generators/lua53 "$here/regenerate-core.lua"
 )
 includes=(-DHAVE_CONFIG_H -I"$b" -I"$b/w2c" -I"$w2c" -I"$build/texk" -I"$source/texk"
  -I"$build/libs/zlib/include" -I"$build/libs/libpng/include" -I"$build/libs/pplib/include"
  -I"$build/libs/lua53/include" -DLUA_COMPAT_MODULE -DLUA_COMPAT_5_2 -DLUAI_HASHLIMIT=6 -DLUA_USE_POSIX
  -I"$w2c/libmd5" -DpdfTeX -I"$overlay/luatexdir" -I"$w2c/luatexdir" -I"$w2c/mplibdir"
  -I"$w2c/synctexdir" '-DSYNCTEX_ENGINE_H=<synctex-luatex.h>' '-Dextra_version_info=-browser-abi3')
 flags=(-O1 -Wno-error=incompatible-pointer-types -Wno-error=incompatible-function-pointer-types -Wno-error=implicit-function-declaration)
 for name in luastuff luainit luatex-core; do
  timeout 60 emcc "${includes[@]}" "${flags[@]}" -c "$overlay/luatexdir/lua/$name.c" -o "/control/build/$name.o"
 done
 timeout 60 emcc "${includes[@]}" "${flags[@]}" -Dmain=oc_engine_main -c "$w2c/luatexdir/luatex.c" -o /control/build/luatex-entry.o
 # Use private copies of archives; replace members by exact upstream member name.
 cp "$b/libluatexspecific.a" /control/build/browser-specific.a
 cp "$b/libluatex.a" /control/build/browser-luatex.a
 for name in luastuff luainit; do
  cp "/control/build/$name.o" "/control/build/libluatexspecific_a-$name.o"
  emar r /control/build/browser-specific.a "/control/build/libluatexspecific_a-$name.o"
 done
 cp /control/build/luatex-core.o /control/build/libluatex_a-luatex-core.o
 emar r /control/build/browser-luatex.a /control/build/libluatex_a-luatex-core.o
fi
em++ -std=c++17 -O1 -I"$build/libs/lua53/include" -c "$here/engine.cpp" -o /control/build/engine.o
# Neither FFI nor socket archives are linked into the candidate.
timeout 180 em++ -O1 /control/build/engine.o /control/build/luatex-entry.o "$b/mplibdir/luatex-lmplib.o" \
 /control/build/browser-specific.a /control/build/browser-luatex.a "$b/libff.a" "$b/libluamisc.a" \
 "$build/libs/lua53/.libs/libtexlua53.a" "$b/libmplibcore.a" \
 "$build/libs/zziplib/libzzip.a" "$build/libs/libpng/libpng.a" "$build/libs/pplib/libpplib.a" \
 "$build/libs/zlib/libz.a" "$b/lib/lib.a" "$build/texk/kpathsea/.libs/libkpathsea.a" \
 "$b/libmputil.a" "$b/libunilib.a" "$b/libmd5.a" \
 -Wl,--wrap=exit -Wl,--wrap=system -Wl,--wrap=popen \
 -sMODULARIZE=1 -sEXPORT_NAME=createLuaTeX -sENVIRONMENT=web,worker,node -sINVOKE_RUN=0 \
 -sALLOW_MEMORY_GROWTH=1 -sINITIAL_MEMORY=33554432 -sMAXIMUM_MEMORY=536870912 -sSTACK_SIZE=5242880 \
 -sFORCE_FILESYSTEM=1 -sFILESYSTEM=1 -sALLOW_TABLE_GROWTH=0 \
 --pre-js "$here/vfs-shapes.js" --pre-js "$here/vfs-budget.js" \
 '-sEXPORTED_RUNTIME_METHODS=["ccall","cwrap","UTF8ToString","FS","HEAPU8"]' \
 '-sEXPORTED_FUNCTIONS=["_malloc","_free","_oc_luatex_abi","_oc_luatex_version","_oc_luatex_configure","_oc_luatex_context","_oc_luatex_add_file","_oc_luatex_declare_output","_oc_luatex_named_output_size","_oc_luatex_copy_named_output","_oc_luatex_compile_format","_oc_luatex_compile_latex","_oc_luatex_output_size","_oc_luatex_copy_output","_oc_luatex_error","_oc_luatex_budget_json"]' \
 -o "$out/luatex.js"
sha256sum "$out/luatex.js" "$out/luatex.wasm" "$overlay/preimages.json" >"$out/SHA256SUMS"
