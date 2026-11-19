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
set -- -std=c++17
# Explicit source-associated build only. The generated build flags and source
# tree must match this exact archive; the public-header fallback cannot claim
# physical units. No runtime acquisition or parser fallback is introduced.
if [ -n "${E1_PDFIUM_SOURCE:-}" ]; then
  [ -f "$E1_PDFIUM_SOURCE/core/fpdfapi/page/cpdf_page.h" ]
  [ -d "${E1_PDFIUM_GENERATED:?matching PDFium generated-header directory required}" ]
  set -- -std=c++20 -DE1_PDFIUM_SOURCE_USERUNIT=1 -DPDF_ENABLE_FREETYPE=1 -isystem "$E1_PDFIUM_SOURCE" -isystem "$E1_PDFIUM_GENERATED" \
    -isystem "$E1_PDFIUM_SOURCE/third_party/freetype/include" -isystem "$E1_PDFIUM_SOURCE/third_party/freetype/src/include"
fi
case "${E1_PDF_INPUT_CAPACITY:-8-mib}" in
  8-mib) ;;
  12-mib) set -- "$@" -DE1_PDF_INPUT_CAPACITY_12_MIB=1 ;;
  *) echo "invalid E1 PDF input capacity" >&2; exit 2 ;;
esac
em++ "$@" -O2 -Wall -Wextra -Werror -fno-exceptions -I"$include" -c "$source/e1.cpp" -o "$out/e1.o"
if [ -n "${E1_PDFIUM_SOURCE:-}" ]; then
  em++ "$@" -O2 -Wall -Wextra -Werror -fno-exceptions -I"$include" -c "$source/pdfium_wasm_platform.cpp" -o "$out/platform.o"
  set -- "$out/e1.o" "$out/platform.o"
else
  set -- "$out/e1.o"
fi
em++ -std=c++17 -O2 -Wall -Wextra -Werror -fno-exceptions "$@" "$library" \
  -sMODULARIZE=1 -sEXPORT_NAME=E1 '-sEXPORTED_RUNTIME_METHODS=["HEAPU8"]' \
  '-sINCOMING_MODULE_JS_API=["wasmBinary","print","printErr"]' \
  -sENVIRONMENT=worker,node -sALLOW_MEMORY_GROWTH=1 -sINITIAL_MEMORY=33554432 \
  -sMAXIMUM_MEMORY=268435456 -sSTACK_SIZE=1048576 -sFILESYSTEM=0 \
  -sERROR_ON_UNDEFINED_SYMBOLS=1 -Wl,-Map,"$out/e1.map" -Wl,--trace -o "$out/e1.js"
