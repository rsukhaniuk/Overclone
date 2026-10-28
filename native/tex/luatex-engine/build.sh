#!/bin/bash
# Offline source build. All writes, generated files and caches are private.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
source=${TL_SOURCE:-/inputs/texlive}
export TL_SOURCE=$source TL_BUILD=${TL_BUILD:-/control/build/wasm}
export EM_CACHE=${EM_CACHE:-/control/cache} TMPDIR=/control/build EM_FROZEN_CACHE=1
export http_proxy=http://127.0.0.1:9 https_proxy=http://127.0.0.1:9 all_proxy=http://127.0.0.1:9
export HTTP_PROXY=$http_proxy HTTPS_PROXY=$https_proxy ALL_PROXY=$all_proxy no_proxy= NO_PROXY=
export PATH=/control/build/generators:/inputs/generators/texk/web2c:$PATH
ulimit -v 3145728
mkdir -p "$TL_BUILD" "$EM_CACHE" /control/results /control/build/generators
if [[ ! -e "$EM_CACHE/sysroot_install.stamp" ]]; then cp -a /inputs/sdk-cache/. "$EM_CACHE/"; fi
flags='-O1 -Wno-error=incompatible-pointer-types -Wno-error=incompatible-function-pointer-types -Wno-error=implicit-function-declaration'
if [[ ! -e "$TL_BUILD/Makefile" ]]; then
 (cd "$TL_BUILD"; timeout 180 emconfigure "$source/configure" \
 --build=x86_64-pc-linux-gnu --host=wasm32-unknown-emscripten --disable-all-pkgs --enable-web2c --enable-luatex \
 --disable-luajittex --disable-luahbtex --disable-luajithbtex --disable-tex --disable-etex --disable-pdftex \
 --disable-xetex --disable-ptex --disable-eptex --disable-uptex --disable-euptex --disable-mf --disable-mflua \
 --disable-mfluajit --disable-mp --disable-web-progs --without-x --disable-shared --disable-native-texlive-build \
 --disable-synctex --with-system-zlib=no --with-system-libpng=no --with-system-pplib=no \
 --with-system-zziplib=no --with-system-kpathsea=no CFLAGS="$flags" CXXFLAGS=-O1)
fi
# LuaJIT's configure unconditionally probes a native 32-bit compiler, even when
# LuaJIT is disabled. Select only the upstream prerequisites actually needed.
timeout 180 emmake make -C "$TL_BUILD" -j2 recurse 'CONF_SUBDIRS=texk/kpathsea texk/ptexenc'
timeout 240 emmake make -C "$TL_BUILD/libs" -j2 recurse 'CONF_SUBDIRS=zlib lua53 libpng pixman cairo zziplib pplib' CFLAGS="$flags"
# Build the missing cross-build configure prerequisite via genuine TANGLE/Web2C.
if [[ ! -x /control/build/generators/otangle ]]; then
 (cd /control/build/generators
  WEBINPUTS="$source/texk/web2c/omegaware" timeout 20 /inputs/generators/texk/web2c/tangle otangle otangle
  [[ -e web2c ]] || ln -s /inputs/generators/texk/web2c/web2c web2c
  srcdir="$source/texk/web2c" timeout 20 sh "$source/texk/web2c/web2c/convert" otangle
  timeout 30 gcc -O1 -DHAVE_CONFIG_H -I. -I/inputs/generators/texk/web2c -I"$source/texk/web2c" \
   -I/inputs/generators/texk -I"$source/texk" otangle.c /inputs/generators/texk/web2c/lib/lib.a \
   /inputs/generators/texk/kpathsea/.libs/libkpathsea.a -lm -o otangle)
fi
# Upstream make invokes the approved native CTANGLE for mplib's CWEB inputs.
if [[ ! -e "$TL_BUILD/texk/web2c/Makefile" ]]; then
 timeout 240 emmake make -C "$TL_BUILD/texk" -j2 recurse CONF_SUBDIRS=web2c
fi
# Do not propagate Clang-only command-line CFLAGS into native GCC Web2C helpers.
# WASM targets use their configured CFLAGS; native helpers use their own config.
timeout 240 emmake make -C "$TL_BUILD/texk/web2c" -j2 luatex
if [[ ! -x /control/build/generators/lua53 ]]; then
 sources=()
 for file in "$source"/libs/lua53/lua53-src/src/*.c; do [[ $file = */luac.c ]] || sources+=("$file"); done
 timeout 60 gcc -O1 -DLUA_USE_POSIX -DLUA_USE_DLOPEN "${sources[@]}" -lm -ldl -o /control/build/generators/lua53
fi
bash "$here/link.sh"
