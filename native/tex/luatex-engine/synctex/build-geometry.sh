#!/bin/bash
# Offline ABI5 candidate ONLY. ABI3/ABI4 recipes and outputs stay independent.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
source=${TL_SOURCE:-/inputs/texlive}
headers=${TL_HEADERS:-/inputs/previous-lua/build/wasm}
deps=${TL_DEPS:-/control/build/wasm}
work=${LUA_MAPS_BUILD:?set a fresh /control/r2-native-geometry/build directory}
out=${OUT:?set a fresh /control/r2-native-geometry/candidate directory}
[[ "$work" == /control/r2-native-geometry/build/* && "$out" == /control/r2-native-geometry/candidate/* ]] || {
 echo 'refuse output outside ABI5 task'
 exit 1
}
export EM_CACHE=${EM_CACHE:-/control/cache} EM_FROZEN_CACHE=1
export TMPDIR="$work/tmp"
export http_proxy=http://127.0.0.1:9 https_proxy=http://127.0.0.1:9 all_proxy=http://127.0.0.1:9
export HTTP_PROXY=$http_proxy HTTPS_PROXY=$https_proxy ALL_PROXY=$all_proxy no_proxy= NO_PROXY=
ulimit -v 3145728
[[ ! -e "$work" && ! -e "$out/luatex.wasm" && ! -e "$out/luatex.js" ]] || {
 echo 'refuse existing build/output bytes'
 exit 1
}
mkdir -p "$work/tmp" "$out"
python3 "$here/prepare-geometry.py" "$source" "$work/overlay"
w2c=$source/texk/web2c
b=$headers/texk/web2c
shared=$here/../../synctex-producer
includes=(-DOC_LUATEX_ABI=5 -DHAVE_CONFIG_H -I"$b" -I"$b/w2c" -I"$w2c" -I"$headers/texk" -I"$source/texk"
 -I"$headers/libs/zlib/include" -I"$headers/libs/libpng/include" -I"$headers/libs/pplib/include"
 -I"$headers/libs/lua53/include" -DLUA_COMPAT_MODULE -DLUA_COMPAT_5_2 -DLUAI_HASHLIMIT=6 -DLUA_USE_POSIX
 -I"$w2c/libmd5" -DpdfTeX -I"$w2c/luatexdir" -I"$w2c/mplibdir" -I"$w2c/synctexdir"
 -I"$shared" -I"$here" '-DSYNCTEX_ENGINE_H=<lua_engine.h>')
# pdfgen embeds __FILE__ in upstream diagnostics; normalize only the private
# overlay prefix so independent output directories reproduce identical bytes.
flags=(-O1 "-ffile-prefix-map=$work/overlay=/inputs/texlive/texk/web2c" -Wno-error=incompatible-pointer-types -Wno-error=incompatible-function-pointer-types -Wno-error=implicit-function-declaration)
cp /control/build/browser-luatex.a "$work/browser-luatex.a"
for name in synctex pdfgen pdfshipout pdfliteral pdfobj lpdflib luanode; do
 case $name in
 synctex) input="$work/overlay/synctexdir/synctex.c" ;;
 lpdflib | luanode) input="$work/overlay/luatexdir/lua/$name.c" ;;
 *) input="$work/overlay/luatexdir/pdf/$name.c" ;;
 esac
 timeout 60 emcc "${includes[@]}" "${flags[@]}" -c "$input" -o "$work/libluatex_a-$name.o"
 emar r "$work/browser-luatex.a" "$work/libluatex_a-$name.o"
done
em++ -std=c++17 -O1 -DOC_LUATEX_ABI=5 -I"$headers/libs/lua53/include" -c "$here/../engine.cpp" -o "$work/engine.o"
em++ -std=c++17 -O1 -DOC_LUATEX_ABI=5 -c "$here/lua_maps.cpp" -o "$work/lua_maps.o"
em++ -std=c++17 -O1 -c "$here/writer_geometry.cpp" -o "$work/writer_geometry.o"
# Only the sink TU renames readers; Lua's guarded facade owns the public readers.
em++ -std=c++17 -O1 -Doc_synctex_status=oc_lua_sink_status -Doc_synctex_length=oc_lua_sink_length \
 -Doc_synctex_copy=oc_lua_sink_copy -c "$shared/oc_synctex_memory_sink.cpp" -o "$work/sink.o"
# Recompile entry to accurately label the candidate's version suffix.
timeout 60 emcc "${includes[@]}" "${flags[@]}" '-Dextra_version_info=-browser-abi5' \
 -Dmain=oc_engine_main -c "$w2c/luatexdir/luatex.c" -o "$work/luatex-entry.o"
timeout 180 em++ -O1 "$work/engine.o" "$work/writer_geometry.o" "$work/lua_maps.o" "$work/sink.o" "$work/luatex-entry.o" \
 "$deps/texk/web2c/mplibdir/luatex-lmplib.o" /control/build/browser-specific.a "$work/browser-luatex.a" \
 "$deps/texk/web2c/libff.a" "$deps/texk/web2c/libluamisc.a" "$deps/libs/lua53/.libs/libtexlua53.a" \
 "$deps/texk/web2c/libmplibcore.a" "$deps/libs/zziplib/libzzip.a" "$deps/libs/libpng/libpng.a" \
 "$deps/libs/pplib/libpplib.a" "$deps/libs/zlib/libz.a" "$deps/texk/web2c/lib/lib.a" \
 "$deps/texk/kpathsea/.libs/libkpathsea.a" "$deps/texk/web2c/libmputil.a" "$deps/texk/web2c/libunilib.a" "$deps/texk/web2c/libmd5.a" \
 -Wl,--wrap=exit -Wl,--wrap=system -Wl,--wrap=popen \
 -sMODULARIZE=1 -sEXPORT_NAME=createLuaTeX -sENVIRONMENT=web,worker,node -sINVOKE_RUN=0 \
 -sALLOW_MEMORY_GROWTH=1 -sINITIAL_MEMORY=33554432 -sMAXIMUM_MEMORY=536870912 -sSTACK_SIZE=5242880 \
 -sFORCE_FILESYSTEM=1 -sFILESYSTEM=1 -sALLOW_TABLE_GROWTH=0 \
 --pre-js "$here/../vfs-shapes.js" --pre-js "$here/../vfs-budget.js" \
 '-sEXPORTED_RUNTIME_METHODS=["ccall","cwrap","UTF8ToString","FS","HEAPU8"]' \
 '-sEXPORTED_FUNCTIONS=["_malloc","_free","_oc_luatex_abi","_oc_luatex_version","_oc_luatex_configure","_oc_luatex_context","_oc_luatex_add_file","_oc_luatex_declare_output","_oc_luatex_named_output_size","_oc_luatex_copy_named_output","_oc_luatex_compile_format","_oc_luatex_compile_latex","_oc_luatex_output_size","_oc_luatex_copy_output","_oc_luatex_error","_oc_luatex_budget_json","_oc_mem_configure","_oc_synctex_abi","_oc_synctex_status","_oc_synctex_length","_oc_synctex_copy","_oc_synctex_geometry_length","_oc_synctex_geometry_copy"]' \
 -o "$out/luatex.js"
sha256sum "$out/luatex.js" "$out/luatex.wasm" "$work/overlay/association.json" >"$out/SHA256SUMS"
emcc --version >"$work/emcc-version.txt"
