#!/usr/bin/env python3
"""Exact guarded wrapper-only16MiB project/8MiB PDF derivative of P15."""
import hashlib
from pathlib import Path

PINS = {
    'xe': {'manifest':'796ea44df922cf9059910c06ff038efccd8e523a2dbc5440cea0a5eef7e9d6d2','wrapper':'b2f45454ac0b99500393bba717a3769106ec539e14dd1818f32f049463d00837','header':'fc357b0aa4d3a81719b5fc93d71306ac0a6095517d8b3644403e3a8534fd5379'},
    'dvi': {'manifest':'316d5e23438eaf28ebba6192d1511b13c8659f4fce3fdc927d3291ab3fef35a0','wrapper':'04d55d642ed7937f4d241e68164b9b35f5b7e38e4830377eb6df318064146417','header':'9d242dc3fcc0a58ef0cf21e14b9f71f769feede3eec77a52b92f7c1827108f19'},
}
EXPORTS = ['_oc_dvi_input_budget_abi','_oc_dvi_project_input_limit','_oc_dvi_pdf_output_limit']
def sha(path): return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def patched(text):
    for before,after in [
        ('constexpr size_t cap = 8 * 1024 * 1024, resource_cap = 96 * 1024 * 1024;',
         'constexpr size_t project_input_cap = 16 * 1024 * 1024;\nconstexpr size_t pdf_output_cap = 8 * 1024 * 1024, resource_cap = 96 * 1024 * 1024;'),
        ('if (n > cap - pdf.size())','if (n > pdf_output_cap - pdf.size())'),
        ('const size_t limit = resource ? resource_cap : cap;','const size_t limit = resource ? resource_cap : project_input_cap;'),
        ('uint32_t oc_dvi_abi_version(void) { return OC_DVI_ABI; }',
         'uint32_t oc_dvi_abi_version(void) { return OC_DVI_ABI; }\nuint32_t oc_dvi_input_budget_abi(void) { return 1; }\nuint32_t oc_dvi_project_input_limit(void) { return project_input_cap; }\nuint32_t oc_dvi_pdf_output_limit(void) { return pdf_output_cap; }'),
    ]:
        if text.count(before)!=1: raise SystemExit('unexpected project-input overlay source context')
        text=text.replace(before,after,1)
    return text

def prepare(parent,out,kind):
    pin=PINS[kind];source=parent/'geometry-overlay/oc_dvi_origin.cpp';header=parent/'geometry-overlay/oc_dvi.h'
    if sha(source)!=pin['wrapper'] or sha(header)!=pin['header']: raise SystemExit('unexpected exact P15 wrapper/header')
    expected=patched(source.read_text()).encode();out.mkdir(parents=True,exist_ok=True)
    for name,data in [('oc_dvi_origin.cpp',expected),('oc_dvi.h',header.read_bytes())]:
        target=out/name
        if target.exists() and target.read_bytes()!=data: raise SystemExit('refusing unrelated prepared output')
        target.write_bytes(data)
    return hashlib.sha256(expected).hexdigest()
