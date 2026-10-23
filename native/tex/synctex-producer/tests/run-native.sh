#!/bin/sh
# Run inside the assigned native-tool Docker image. No emcc, product links or fmt.
set -eu
if [ "$#" -ne 2 ]; then
    echo 'usage: sh run-native.sh FROZEN_ON_CANDIDATE NEW_PRIVATE_OUTPUT_DIR' >&2
    exit 2
fi
candidate=$(realpath "$1")
mkdir "$2" # refuse reuse; never alter the frozen candidate
out=$(realpath "$2")
here=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
set -- -std=c17 -D__SyncTeX__ '-DSYNCTEX_ENGINE_H="oc_synctex_engine.h"' \
    -I"$candidate" -I"$candidate/tex" -I"$candidate/pdftexdir"
gcc "$@" -DWEBASSEMBLY_BUILD -Werror=implicit-function-declaration -fsyntax-only \
    "$candidate/synctexdir/synctex.c" "$candidate/oc_synctex_port.c" \
    "$candidate/main.c" "$candidate/tex/pdftex0.c" "$candidate/tex/pdftexini.c"
g++ -std=c++17 -Wall -Wextra -Werror -fsanitize=address,undefined \
    -fno-omit-frame-pointer -no-pie -I"$here" "$here/tests/formatter_test.cpp" \
    -o "$out/formatter-test"
"$out/formatter-test" --list > "$out/formatter-cases.txt"
while IFS= read -r test; do
    "$out/formatter-test" "$test"
    printf '%s PASS\n' "$test"
done < "$out/formatter-cases.txt"
gcc "$@" -ffunction-sections -fdata-sections -fsanitize=address,undefined \
    -fno-omit-frame-pointer -c "$here/tests/seal_test.c" -o "$out/seal-test.o"
g++ -std=c++17 -Wall -Wextra -Werror -ffunction-sections -fdata-sections \
    -fsanitize=address,undefined -fno-omit-frame-pointer -no-pie -Wl,--gc-sections \
    "$out/seal-test.o" "$here/oc_synctex_memory_sink.cpp" -o "$out/seal-test"
for test in success postamble-only native-error pdf-error no-postamble disabled \
    limit abort discard stale-attempt reconfigure; do
    "$out/seal-test" "$test"
    printf '%s PASS\n' "$test"
done
