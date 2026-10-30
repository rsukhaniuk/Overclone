# Ukrainian candidate resources (review only)

Owned source recipe/test tooling; no engine rebuild, application integration,
publication, acquisition or user-project rewrite. Base suite remains immutable.

## Reproduce with supplied inputs

The exact `/inputs/suite/pins.json` and retained input bytes are checked by
`source.lock.json`. Root must supply its 528-byte `/control/root-latex-dvi.ini`
with bookmark's `dvipdfmx` option (SHA-256
`27c03e72a58bbcd5d58bdb62c1c223e0cd420bb0957f0d6e8e67e9d853247e33`).

```sh
python3 native/tex/ukrainian-resources/prepare.py
for engine in pdftex dvi xetex luatex; do
  node native/tex/ukrainian-resources/run.cjs "$engine" format || exit 1
  case "$engine" in xetex|luatex) fixture=unicode;; *) fixture=t2a;; esac
  node native/tex/ukrainian-resources/run.cjs "$engine" compile \
    "native/tex/ukrainian-resources/fixtures/$fixture.tex" || exit 1
done
python3 native/tex/ukrainian-resources/check.py
node native/tex/ukrainian-resources/run.cjs pdftex compile \
  docs/examples/t2a-project-font-main.tex
python3 native/tex/ukrainian-resources/finalize.py
```

Output: `/control/ukrainian-overlay/files/`, strict `manifest.json`, exact old/new
`replacements.json`, `provenance.json`, separate `notices/`, per-engine genuine
logs/outputs and `checks/summary.json`. No notice or qualified path is included
in the runtime manifest. Format bytes embed creation metadata; use the emitted
pins, not an assumption of byte-identical regeneration on a later date.

The minimal JavaScript runner exists solely to load the actual Emscripten WASM
and exercise its FS/native ABI. It does not alter loader/engine bytes, expose
host files to TeX, invoke host TeX engines, or fetch packages. pdfTeX's worker
loader is evaluated unchanged with local-byte WASM fetch and offline XHR misses;
its approved FS lookup aliases include extensionless TFM/TeX names. The selected
pdfTeX/DVI format is aliased to native `swiftlatexpdftex.fmt`. Lua uses ABI2 file
seeding/output-copy APIs and never receives the unrelated 33MiB Xe format.

## Meaning of the checks

Stock retained Babel `hyphen.cfg` plus the two-line `language.dat` replace the
private English-only config. English is first and remains language0. pdfTeX,
LaTeX/DVI and XeTeX embed Ukrainian as language1. Standard Babel/LuaTeX stores
language0 in the format, then allocates Ukrainian as language2 and loads its
patterns at document runtime from `language.dat`; this is not a fake format ID
or a claim that Lua dumps nonzero-language patterns.

Retained `loadhyph-uk.tex` selects genuine `ukrhyph.tex`/`ukrhypmp.tex` with
`catlcy.tex`, `lcy2t2a.tex`, `hypht2.tex` for 8-bit engines, and `hyph-uk.tex` plus
`hyph-quote-uk.tex` for Unicode engines. Already-present loaders/patterns are
reused from the hash-verified suite. Their exact pins and every mounted suite
input appear in per-engine `result.json`. The upstream mp default is preserved;
no invented patterns or forced Babel dialect ID.

Tests assert actual showhyphens breaks, English controls, unhyphenated Ukrainian
under English language0, Ukrainian `Зміст` captions (literal in Unicode logs;
exact Cyrillic macro spelling in 8-bit logs), no fallback/missing-glyph messages,
and genuine PDF/DVI/XDV magic. Xe's kernel showhyphens hardcodes Latin Modern,
so the synthetic diagnostic box explicitly selects the same DejaVu test font;
this is not an injected edit to imported source. The original supplied T2A
fixture is compiled unchanged as an additional control.

## Limits and missing fonts

Cached T2A/cmr has genuine **m/n10pt and bx/n10pt only**: `larm1000.tfm`,
`labx1000.tfm`, `sfrm1000.pfb`, `sfbx1000.pfb`, existing map/encoding and unmodified
FD. No other shape/design size is claimed. Merely adding unmatched cached metrics
would create false readiness. `missing-fonts.json` lists all 138 missing FD
shape/design-size pairs, matching PFB names from the accepted map, cached-metric
availability and upstream cached-index byte lengths. Additional metrics require
LH data; additional outlines require CM-Super, with source/license review and
acquisition authorization. Immediate 12pt/heading gaps include
`larm1200.tfm`/`sfrm1200.pfb`, `labx1200.tfm`/`sfbx1200.pfb`,
`labx1440.tfm`/`sfbx1440.pfb`, `labx1728.tfm`/`sfbx1728.pfb`.

The original Cyrillic І/і versus Latin I/i extraction failure is **not fixed**.
No blanket glyph mapping or source changes are proposed. Compiler success and
font embedding are not Unicode extraction proof. Root owns mixed-script PDF
text assertions, renderer conversion, browser/offline acceptance and integration.
The resource checks do not establish full TeX Live support or redistribution
clearance. Upstream inline notices are preserved; retained GPL COPYING/INSTALL,
LPPL and ruhyphen notice source are supplied separately from runtime files.
