"""Genuine complete-PDF Unicode/CMap regressions; no installed fonts required."""
from pathlib import Path
import runpy,hashlib,json
base=runpy.run_path(str(Path(__file__).resolve().parents[1]/"actualtext/make_fixtures.py"))
out,pdf,unmarked,fixtures=(base[k] for k in ("out","pdf","unmarked","fixtures"))
def cmap_range(codes, definitions):
 cmap='/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def /CMapName /Fixture def /CMapType 2 def 1 begincodespacerange <00> <FF> endcodespacerange '+definitions+' endcmap CMapName currentdict /CMap defineresource pop end end'
 return pdf('BT /F1 12 Tf 10 20 Td <'+codes+'> Tj ET',cmap=cmap)
fixtures.update({
 'normal-single-emoji':(unmarked('🙂'),'🙂'),
 'range-supplementary':(cmap_range('2122','1 beginbfrange <21> <22> <D83DDE42> endbfrange'),'🙂🙃'),
 'range-bmp-ligature':(cmap_range('2122','1 beginbfrange <21> <22> <00660069> endbfrange'),'fifj'),
 'range-array-supplementary':(cmap_range('2122','1 beginbfrange <21> <22> [<D83DDE42> <D83DDE43>] endbfrange'),'🙂🙃'),
 'range-highplane-boundary':(cmap_range('2122','1 beginbfrange <21> <22> [<D83FDFFF> <D840DC00>] endbfrange'),'\U0001ffff\U00020000'),
})
manifest={}
for name,(data,expected) in fixtures.items():
 (out/(name+'.pdf')).write_bytes(data);manifest[name]={'sha256':hashlib.sha256(data).hexdigest(),'bytes':len(data),'expected':expected,'normalControl':name.startswith('normal-')}
(out/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n')
for name,mapping in [('malformed-high','D83D'),('malformed-low','DE42'),('malformed-pair','D83D0041')]:
 data=cmap_range('21','1 beginbfchar <21> <'+mapping+'> endbfchar');(out/(name+'.pdf')).write_bytes(data)
