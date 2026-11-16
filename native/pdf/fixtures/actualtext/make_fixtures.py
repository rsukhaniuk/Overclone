from pathlib import Path
import hashlib,json,sys
out=Path(sys.argv[1]);out.mkdir(parents=True,exist_ok=True)
def utf(s):return 'FEFF'+s.encode('utf-16-be').hex().upper()
def marked(s,x=10,y=20):return f'/Span << /ActualText <{utf(s)}> >> BDC BT /F1 12 Tf {x} {y} Td ({"A"*len(s)}) Tj ET EMC'
def pdf(content,extra=None,cmap=None,rtl=False):
 font='<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica'+(' /ToUnicode 6 0 R' if cmap else '')+' >>'
 catalog='<< /Type /Catalog /Pages 2 0 R'+(' /ViewerPreferences << /Direction /R2L >>' if rtl else '')+' >>'
 objects=[catalog,'<< /Type /Pages /Kids [3 0 R] /Count 1 >>','<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 200] /Resources << /Font << /F1 5 0 R >> '+(' /Properties << /Shared 6 0 R >> ' if extra else '')+' >> /Contents 4 0 R >>',f'<< /Length {len(content.encode())} >>\nstream\n{content}\nendstream',font]
 if cmap:objects.append(f'<< /Length {len(cmap.encode())} >>\nstream\n{cmap}\nendstream')
 if extra:objects.extend(extra)
 body=b'%PDF-1.7\n';offsets=[]
 for i,obj in enumerate(objects):offsets.append(len(body));body+=f'{i+1} 0 obj\n{obj}\nendobj\n'.encode()
 xref=len(body);body+=f'xref\n0 {len(objects)+1}\n0000000000 65535 f \n'.encode()+b''.join(f'{n:010} 00000 n \n'.encode() for n in offsets)
 return body+f'trailer\n<< /Size {len(objects)+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n'.encode()
def unmarked(text):
 codes=[33+i for i in range(len(text))];pairs='\n'.join(f'<{code:02X}> <{ch.encode("utf-16-be").hex().upper()}>' for code,ch in zip(codes,text))
 cmap='/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def /CMapName /Fixture def /CMapType 2 def 1 begincodespacerange <00> <FF> endcodespacerange '+f'{len(codes)} beginbfchar\n'+pairs+'\nendbfchar endcmap CMapName currentdict /CMap defineresource pop end end'
 return pdf('BT /F1 12 Tf 10 20 Td <'+''.join(f'{c:02X}' for c in codes)+'> Tj ET',cmap=cmap)
logical='مرحبا بالعالم'
fixtures={
 'actual-full':(pdf(marked(logical)),logical),
 'actual-mixed':(pdf(marked('ABC مرحبا 123 (Київ🙂)')),'ABC مرحبا 123 (Київ🙂)'),
 'actual-mixed-bmp':(pdf(marked('ABC مرحبا 123 (Київ)')),'ABC مرحبا 123 (Київ)'),
 'actual-parentheses':(pdf(marked('مرحبا (123) بالعالم')),'مرحبا (123) بالعالم'),
 'actual-hebrew':(pdf(marked('שלום עולם')),'שלום עולם'),
 'actual-word-owners':(pdf(marked('بالعالم',10)+' '+marked('مرحبا',140)),logical),
 'actual-mixed-owners-ltr':(pdf(marked('ABC',10)+' '+marked(logical,140)),'ABC '+logical),
 'actual-mixed-owners-rtl':(pdf(marked('ABC',10)+' '+marked(logical,140),rtl=True),logical+' ABC'),
 'actual-shared-owner':(pdf('/Span /Shared BDC BT /F1 12 Tf 10 20 Td (AAAA) Tj 100 0 Td (AAAA) Tj ET EMC',extra=[f'<< /ActualText <{utf(logical)}> >>']),logical),
 'normal-latin':(pdf('BT /F1 12 Tf 10 20 Td (Latin office ffi 123.) Tj ET'),'Latin office ffi 123.'),
 'normal-ukrainian-emoji':(unmarked('Київ🙂'),'Київ🙂'),
 'normal-arabic-visual':(unmarked(logical[::-1]),logical),
}
manifest={}
for name,(data,expected) in fixtures.items():
 (out/(name+'.pdf')).write_bytes(data);manifest[name]={'sha256':hashlib.sha256(data).hexdigest(),'bytes':len(data),'expected':expected,'normalControl':name.startswith('normal-')}
(out/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n')
