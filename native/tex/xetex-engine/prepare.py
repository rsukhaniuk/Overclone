#!/usr/bin/env python3
"""Patch only the retained port's handwritten seams, never generated engine C."""
import hashlib
import json
from pathlib import Path
import shutil
import sys

source, target = map(Path, sys.argv[1:])
shutil.copytree(source, target, dirs_exist_ok=True)

def patch(name, before, after):
    path = target/name
    text = path.read_text()
    if text.count(before) != 1:
        raise RuntimeError(f'preimage mismatch: {name}: {before[:70]}')
    path.write_text(text.replace(before, after))

patch('main.c', '#include "bibtex.h"', '#include "bibtex.h"\nextern int oc_path_ok(const char*, int);\nextern int oc_check_inputs(void);\nextern int oc_prepare_output(const char*);\nextern int oc_output_ready(int);\nextern void oc_initialize(void);\nstatic int oc_attempt_started = 0;')
patch('main.c', 'const char *DEFAULT_FMT_NAME = " swiftlatexxetex.fmt";',
      'const char *DEFAULT_FMT_NAME = " xelatex.fmt";')
patch('main.c', 'const char *DEFAULT_DUMP_NAME = "swiftlatexxetex";',
      'const char *DEFAULT_DUMP_NAME = "xelatex";')
patch('main.c', '''  while (*ptr) {
    buffer[k++] = *(ptr++);
  }''', '''  /* topenin's buffer is Unicode scalars, not UTF-8 bytes. */
  while (*ptr) {
    unsigned int c = (unsigned char)*ptr++;
    if (c >= 0xc0) {
      int n = c < 0xe0 ? 1 : c < 0xf0 ? 2 : 3;
      c &= (1u << (6 - n)) - 1;
      while (n--) c = (c << 6) | ((unsigned char)*ptr++ & 63);
    }
    buffer[k++] = c;
  }''')
patch('main.c', 'int _compile() {\n  haltonerrorp = 1;', '''int _compile() {
  if (oc_attempt_started++) return -10; /* One attempt per fresh module. */
  if (!oc_check_inputs()) return -4;
  if (!oc_prepare_output(iniversion ? NULL : main_entry_file)) return -5;
  oc_initialize();
  exit_code = 0;
  shellenabledp = 0;
  restrictedshell = 1;
  output_directory = "/work";
  haltonerrorp = 1;''')
patch('main.c', '    strncpy(bootstrapcmd, main_entry_file, MAXMAINFILENAME);\n    bootstrapcmd[MAXMAINFILENAME - 1] = 0;',
      '    snprintf(bootstrapcmd, MAXMAINFILENAME, "\\\"%s\\\"", main_entry_file);')
patch('main.c', '  return exit_code;',
      '  return exit_code == 0 && !oc_output_ready(iniversion) ? -5 : exit_code;')
patch('main.c', 'int compileFormat() {\n    iniversion = 1;',
      'int compileFormat() {\n    nopdfoutput = 1;\n    iniversion = 1;')
patch('main.c', 'int compileBibtex() {', '''int compileBibtex() {
    if (oc_attempt_started++) return -10;
    if (!oc_check_inputs()) return -4;
    oc_initialize();''')
patch('main.c', 'int setMainEntry(const char *p) {\n    strncpy(main_entry_file, p, MAXMAINFILENAME);', '''int setMainEntry(const char *p) {
    if (!p || strlen(p) > 240 || !oc_path_ok(p, 2)) {
        main_entry_file[0] = 0;
        return -2;
    }
    strncpy(main_entry_file, p, MAXMAINFILENAME);''')
# The first identical line belongs to the unused native CLI, the second to the WASM ABI.
path = target/'main.c'
text = path.read_text(); text = text.replace('  filelineerrorstylep = 0;', '  filelineerrorstylep = 1;')
path.write_text(text)
patch('kpseemu.c', '#include <libgen.h>', '#include <libgen.h>\nextern int oc_path_ok(const char*, int);')
patch('kpseemu.c', 'boolean kpse_in_name_ok(const_string fname) { return true; }',
      'boolean kpse_in_name_ok(const_string fname) { return oc_path_ok(fname, 0); }')
patch('kpseemu.c', 'boolean kpse_out_name_ok(const_string fname) { return true; }',
      'boolean kpse_out_name_ok(const_string fname) { return oc_path_ok(fname, 1); }')
patch('kpseemu.c', '  if (name == NULL) {', '  if (name == NULL || !oc_path_ok(name, 0)) {')
patch('kpseemu.c', '''  // Head to network search
  return kpse_find_file_js(name, format, must_exist);''', '''  /* Provisioned MEMFS only. No JS callback, URL lookup, or host filesystem. */
  char provisioned[MAX_PATH_LEN + 40];
  snprintf(provisioned, sizeof(provisioned), "/tex/%s", name);
  if (access(provisioned, R_OK) == 0) return xstrdup(provisioned);
  if (strchr(xbasename(name), '.') == NULL) {
    fix_extension(provisioned, format);
    if (access(provisioned, R_OK) == 0) return xstrdup(provisioned);
  }
  return NULL;''')
patch('texfile.c', '  string fname = NULL;',
      '  string fname = NULL;\n  if (!kpse_in_name_ok(nameoffile + 1)) { *f_ptr = NULL; return false; }')
patch('texfile.c', '''  string fname;
  boolean absolute''', '''  string fname;
  if (!kpse_out_name_ok(nameoffile + 1)) { *f_ptr = NULL; return false; }
  boolean absolute''')
# Retained pdfimage.cpp uses a Poppler class name despite carrying Xpdf headers.
# Adapt its handwritten API use to the genuine approved Xpdf dependency.
path = target/'xetexdir/pdfimage.cpp'
text = path.read_text(); assert text.count('GooString') == 4
path.write_text(text.replace('GooString', 'GString'))
patch('xetexdir/XeTeXFontMgr_FC.cpp', '''    int count = 0;
    std::ifstream ifs''', '''    if (!fontlist_url) { cachedAll = true; return; }
    int count = 0;
    std::ifstream ifs''')
# Pipe output invokes a converter subprocess upstream: disallow it even if TeX
# special flags were somehow enabled. The ordinary no-pdf XDV branch is retained.
patch('xetexdir/XeTeX_ext.c', '        *fptr = popen(cmd, "w");',
      '        *fptr = NULL; /* No child processes in the browser engine. */')
manifest = {str(p.relative_to(source)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(source.rglob('*')) if p.is_file()}
(target.parent/'port-inputs.json').write_text(json.dumps(manifest, indent=2)+'\n')
