# Genuine browser Biber, ABI 1

This component builds **genuine Biber 2.19 on Perl 5.38.2**, including 20
statically linked XS extensions. It matches the accepted biblatex 3.19 / BCF 3.10
/ BBL 3.2 resources. The user approved the required source acquisitions and the
GPL/AGPL source-sharing route. Application adoption remains root-owned.

The original cache-only audit and initial acquisition proposal are historical.
The official Biber release and dependencies have since been acquired, verified
and built with Emscripten 6.0.13 in network-disabled Docker. The first real
source-built ABI run produces the same 1,493-byte BBL as the independent genuine
Biber feasibility baseline (SHA256
`6d33fa3a1479358fcbd9c828f53c689e560adb02adbeeb4ae4a668d40c0e0b6d`).
A successful finite fixture is not full browser, offline or all-format acceptance.

## Source and license provenance

- [source-lock.json](source-lock.json): exact URLs, archive lengths/SHA256,
  expanded sizes and original notices for Biber, Perl, 20 XS distributions,
  libxml2, sombok and the WebPerl helper source. Biber's complete metadata requires
  Perl **5.32 or newer**; the `use v5.24` declaration alone is insufficient.
- [bootstrap-lock.json](bootstrap-lock.json): immutable Emscripten image and exact
  build-only Ubuntu packages. No host tools are installed. Native package binaries
  are never used as the product runtime.
- [upstream/texlyre-biber/UPSTREAM.json](upstream/texlyre-biber/UPSTREAM.json):
  unchanged TeXlyre port at commit `46ebb6216a5c1fe63dd1dc885831d1a8cef2dc86`,
  with original AGPL LICENSE/NOTICE and embedded WebPerl notices preserved.
- [notices](notices): original source and package copyright/license documents.
  [package-source-inventory.json](package-source-inventory.json) associates the
  pure-Perl source files with their exact package inputs. The Biber release
  README declares Artistic 2.0, while META.json labels `perl_5`; both declarations
  are retained. Its full Artistic 2.0 text is also in the official package notice.

The project overlays omit the unused WebPerl JavaScript-evaluation extension,
rename the genuine interpreter main for C ABI entry, observe cleanup exactly
once, disable process/network operations and preload the exact official Biber
source/data tree. They do not reimplement Biber, change its algorithms/version or
fall back to BibTeX. Every link receipt records source hashes, commands and guarded
preimage/postimage changes. Preserve these sources, locks, notices and required
build inputs with any distributed component; final distribution packaging remains
an acceptance gate.

## Rebuild offline

Supply the existing authorized archives as `NAME.archive` according to
`source-lock.json`. Prepare an exact build image using already acquired packages:

```sh
python3 prepare-bootstrap.py --packages "$BIBER_DEBS" \
  --base-packages "$TOOLCHAIN_DEBS" --out "$BOOTSTRAP_CONTEXT"
docker build --network none -t biber-build "$BOOTSTRAP_CONTEXT"
```

`prepare-bootstrap.py` checks every package before copying it. The Dockerfile
uses the pinned public Emscripten digest and performs only verified offline dpkg
installation. The previously cached equivalent build image can be reused when
its exact package/toolchain identities match the lock.

Run the build from a fresh private output mount:

```sh
docker run --rm --network none --cpus 2 --memory 1g --pids-limit 256 \
  --read-only --tmpfs /tmp:rw,nosuid,size=128m \
  --tmpfs /work:rw,nosuid,size=256m \
  -v "$ARCHIVES:/archives:ro" -v "$BUILD_OUTPUT:/build" \
  -v "$PWD/native/tex/biber-engine:/component:ro" biber-build \
  python3 /component/build.py --archives /archives --output /build
```

No CPAN/APT/network installer runs during the source build. Archives are validated
before extraction, including path/type/count/expanded bounds and safe same-archive
hardlinks. The SDK cache is copied into the private build tree. The measured first
source build took 407 seconds; the complete tree plus candidate links occupied
about 1.2 GiB. Allow a 4 GiB private workspace and at most 1 GiB container memory;
these observations are not a cross-device performance promise.

`link.py` can relink an already built source tree into a **fresh** output directory
without repeating the interpreter/dependency build. Output is exactly
`biber.js`, `biber.wasm`, `biber.data`, plus private build receipts/object files.
Generated artifacts must not be committed.

## Runtime contract and confinement

The three artifacts form one integrity-pinned component. The JS factory is
`createBiber`; provide exact `wasmBinary` and a synchronous `getPreloadedPackage`
returning the already verified bootstrap ArrayBuffer before initialization.
The data file is trusted runtime data, excluded from project/TeX/converter input
roles. Missing/incorrect explicit bootstrap inputs are refused; the package loader
has no fetch/Node-file fallback or IndexedDB cache. The optimized loader fits the
existing 1 MiB loader cap. WASM maximum memory is 512 MiB.

[engine.h](engine.h) freezes ABI 1: configure a relative UTF-8 BCF entry path and
opaque context, capture input copies, then run once. Role 0 is refused. Role 1
contains explicit BIB/data files; role 2 is only the exact configured BCF. Combined
inputs are at most 64 files / 8 MiB, each at most 8 MiB. Output is aggregate BBL+BLG
at most 8 MiB; captured terminal output has its separate 256 KiB limit. Only a
successful run exposes a nonempty BBL. Errors use fixed safe codes.

The C++ wrapper clears ambient environment/options, uses fixed `--noconf` argv,
sets genuine Perl `-CA` to decode Unicode argv, and owns cleanup/exit handling.
Runtime and captured inputs become readonly before genuine Perl entry. The SDK
MEMFS/LZ4FS guard checks saved references and shared lower operation tables,
allows only exact BBL/BLG/terminal output paths plus bounded private cache writes,
preflights vector staging and latches all quota failures before throwing. Private
cache growth is at most 8 MiB; backing/copy allocation work is at most 32 MiB.
Network/process imports refuse and latch even if Perl catches the failure. No
NODEFS, host filesystem mount, shell, application backend or WebPerl JS bridge is
used. Root's browser worker must physically terminate a cancelled operation.

## Tests and known compatibility boundaries

```sh
node test-api.cjs COMPONENT_DIR ACTUAL_FIXTURE_JSON FRESH_RESULTS_DIR
```

Run this command in network-disabled bounded Docker. Each case uses a fresh Node
worker. The input manifest records actual producer BCF/BIB lengths/hashes and
expected bibliography keys/text. Normal mode verifies the genuine 2.19 banner;
`controlled` mode is explicitly wrapper-only lifecycle testing and is never
Biber proof. Source fixture inputs and provenance are in `fixtures/`.

Current matching BCF 3.10 plus UTF-8 BibTeX input is the core supported path under
acceptance. Build.PL also lists XML::LibXSLT and legacy Encode::EUCJPASCII,
Encode::HanExtra and Encode::JIS2K; their static XS support is absent from this
20-extension build. Biber's optional `--convert-control` diagnostic BCF-to-HTML
XSLT operation, those legacy encodings, XML input variants and other untested
Biber formats are explicit compatibility gaps. The fixed product argv does not
expose `--convert-control`; matching BCF processing does not use it. Remote
HTTPS and subprocess-backed inputs are intentionally refused. Do not equate the
20-extension build with the entire Build.PL dependency closure or infer support
for untested input formats.

Full native boundaries, Unicode filenames/content, multiple BIB/refsection cases,
all four final TeX PDFs, published-browser worker cancellation and cold offline
restart remain separate required evidence. Audit tests (`test-audit.py`) inspect
inputs only and never establish runtime acceptance.
