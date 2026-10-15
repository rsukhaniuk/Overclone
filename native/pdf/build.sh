#!/bin/sh
# Optional standalone build; run INSIDE the pinned SDK, never root default build.
set -eux
[ "$#" -eq 3 ] || { echo 'usage: sh build.sh PDFIUM_INCLUDE_DIR PDFIUM_LIBRARY OUTPUT_DIR' >&2; exit 2; }
include=$1
library=$2
out=$3
[ -f "$include/fpdf_doc.h" ] && [ -f "$library" ] && [ -d "$out" ]
source=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
em++ --version
em++ -std=c++17 -O2 -Wall -Wextra -Werror -fno-exceptions -I"$include" -c "$source/e1.cpp" -o "$out/e1.o"
em++ -std=c++17 -O2 -Wall -Wextra -Werror -fno-exceptions "$out/e1.o" "$library" \
  -sMODULARIZE=1 -sEXPORT_NAME=E1 '-sEXPORTED_RUNTIME_METHODS=["HEAPU8"]' \
  '-sINCOMING_MODULE_JS_API=["wasmBinary","print","printErr"]' \
  -sENVIRONMENT=worker,node -sALLOW_MEMORY_GROWTH=1 -sINITIAL_MEMORY=33554432 \
  -sMAXIMUM_MEMORY=268435456 -sSTACK_SIZE=1048576 -sFILESYSTEM=0 \
  -sERROR_ON_UNDEFINED_SYMBOLS=1 -Wl,-Map,"$out/e1.map" -Wl,--trace -o "$out/e1.js"
