#!/usr/bin/env bash
# One cohesive actual native graphics1 source batch; no shared/app builds.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${RESULTS:?fresh results root required}"
: "${CANDIDATE:?actual Lua graphics1 directory required}"
: "${DVI_CONVERTER:?actual DVI graphics1 directory required}"
: "${XE_CONVERTER:?actual Xe graphics1 directory required}"
[[ ! -e "$RESULTS" ]] || { echo 'refuse existing results'; exit 1; }
mkdir -p "$RESULTS"
run() { local label=$1; shift; printf '%q ' "$@" >>"$RESULTS/commands.txt"; printf '\n' >>"$RESULTS/commands.txt"; "$@" >"$RESULTS/$label.log" 2>&1; echo "$label exit=0"; }
for engine in dvi xe lua; do
 for stage in pass1 bib pass2 pass3; do
  op=latex;from=;tag=801
  case "$stage" in bib) op=bibtex;from="$RESULTS/$engine-pass1";tag=802;; pass2) from="$RESULTS/$engine-bib";tag=803;; pass3) from="$RESULTS/$engine-pass2";tag=804;; esac
  run "$engine-$stage" env LUA_CANDIDATE="$CANDIDATE" PASS_TAG="$tag" node "$here/reproduce.cjs" "$engine" "$op" "$RESULTS/$engine-$stage" ${from:+"$from"}
 done
 if [[ "$engine" != lua ]]; then
  converter=$DVI_CONVERTER;[[ "$engine" != xe ]] || converter=$XE_CONVERTER
  run "$engine-convert" env CONVERTER="$converter" PASS_TAG=805 node "$here/reproduce.cjs" "$engine" convert "$RESULTS/$engine-convert" "$RESULTS/$engine-pass3"
 fi
done
for mode in scoped-axis raw-balanced matrix-suffix matrix-exponent matrix-short matrix-singular matrix-reflect matrix-shear missing-restore missing-save misplaced-restore; do
 run "lua-state-$mode" env RESULTS="$RESULTS/lua-states" node "$here/test-lua-states.cjs" "$mode"
done
for mode in literal lua-print lua-literal setmatrix colorstack attributes duplicate-media lua-attributes form; do
 run "lua-unsafe-$mode" env LUA_EXPECTED_ABI=5 RESULTS="$RESULTS/lua-unsafe" node "$here/../synctex-fixtures/lua/test-geometry.cjs" "$mode"
done
for mode in cap native-fail invalid-config repeat-config; do
 run "lua-boundary-$mode" env LUA_EXPECTED_ABI=5 RESULTS="$RESULTS/lua-boundaries" node "$here/../synctex-fixtures/lua/test-runtime.cjs" "$mode"
done
for engine in dvi xe; do
 converter=$DVI_CONVERTER;extension=dvi;[[ "$engine" != xe ]] || { converter=$XE_CONVERTER;extension=xdv; }
 run "$engine-unsafe-specials" env KIND="$engine" CONVERTER="$converter" INTERMEDIATE="$RESULTS/$engine-pass3/output.$extension" RESOURCES=/control/r2-graphics-navigation/build/converter-resources RESULTS="$RESULTS/$engine-unsafe" node "$here/test-converter-states.cjs"
 for mode in empty bad-header truncated bad-postamble unknown-unit invalid-config post-config mutate-caller-buffer; do
  run "$engine-boundary-$mode" env CONVERTER="$converter" DVI_INPUT=/control/r2-dvi-maps/evidence/final/runtime/positive.dvi XDV_INPUT=/control/r2-xe-maps/evidence/final/runtime/positive.xdv RESULTS="$RESULTS/$engine-boundaries" node "$here/../synctex-fixtures/$engine/test-convert.cjs" "$mode"
 done
done
run cancellation env RECOVERY_ROOT="$RESULTS" RESULTS="$RESULTS/cancellation" node "$here/test-cancel.cjs"
