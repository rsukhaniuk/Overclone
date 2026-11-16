#!/usr/bin/env python3
"""Apply exact bounded standalone-WASM build overrides to acquired sources."""
from pathlib import Path
import shutil
import sys

from actualtext import apply as repair_actualtext

source = Path(sys.argv[1]).resolve()
repair_actualtext(source)
def replace(relative, before, after):
    path = source / relative
    text = path.read_text()
    if text.count(before) != 1:
        raise SystemExit(f"unexpected source/prepared state: {relative}")
    path.write_text(text.replace(before, after, 1))

replace(".gn", 'script_executable = "//third_party/cpython3/host/bin/python3"', 'script_executable = "/usr/bin/python3"')
replace("build/config/BUILDCONFIG.gn", '''  assert(
      false,
      "emscripten is not a supported target_os. It is available only as secondary toolchain.")''',
    '  _default_toolchain = "//build/toolchain/wasm:wasm"')
replace("BUILD.gn", "  defines = []\n", '  defines = []\n  if (is_wasm) { defines += [ "_GNU_SOURCE=1" ] }\n')
with (source / "BUILD.gn").open("a") as out:
    out.write('\ngroup("r2_wasm") { deps = [ ":pdfium(//build/toolchain/wasm:wasm)" ] }\n')
(source / "build/config/gclient_args.gni").write_text("build_with_chromium = false\ncheckout_android = false\ncheckout_skia = false\ncheckout_libpng = true\n")
output = source / "out/r2"
output.mkdir(parents=True, exist_ok=True)
shutil.copyfile(Path(__file__).with_name("args.gn"), output / "args.gn")
