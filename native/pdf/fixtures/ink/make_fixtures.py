from pathlib import Path
import struct,math,json,hashlib,sys
out=Path(sys.argv[1]);out.mkdir(parents=True,exist_ok=True)
H=lambda *v:struct.pack('>'+'H'*len(v),*v)
h=lambda *v:struct.pack('>'+'h'*len(v),*v)
I=lambda *v:struct.pack('>'+'I'*len(v),*v)
def font(curve=False,cancelled=False):
 n=8 if cancelled else 3 if curve else 4;ymax=1000 if curve else 800
 glyph= h(1,0,0,600,ymax)+H(n-1,0)+bytes([1,0,1] if curve else [1,1,1,1])+h(*( [0,300,300] if curve else [0,600,0,-600]))+h(*( [0,1000,-1000] if curve else [0,0,800,0]))
 if cancelled:
  glyph=h(2,0,0,600,800)+H(3,7,0)+bytes([1]*8)+h(0,600,0,-600,0,0,600,0)+h(0,0,800,0,-800,800,0,-800)
 glyph+=bytes((-len(glyph))%4);glyf=bytes(12)+glyph
 namebytes=b'';records=b''
 for nameid,text in [(1,'SyntheticInk'),(2,'Regular'),(4,'SyntheticInk Regular'),(6,'SyntheticInk')]:
  value=text.encode('utf-16-be');records+=H(3,1,0x409,nameid,len(value),len(namebytes));namebytes+=value
 names=H(0,4,6+len(records))+records+namebytes
 format4=H(4,32,0,4,4,1,0,65,65535,0,65,65535)+h(-64,1)+H(0,0)
 tables={
 'head':I(0x10000,0x10000,0,0x5f0f3cf5)+H(3,1000)+bytes(16)+h(0,0,600,ymax)+H(0,8)+h(2,0,0),
 'hhea':I(0x10000)+h(ymax,0,0)+H(600)+h(0,0,600,1,0,0,0,0,0,0,0)+H(2),
 'maxp':I(0x10000)+H(2,n,2 if cancelled else 1,0,0,2,0,0,0,0,0,0,0,0),
 'hmtx':H(600,0,600,0),'cmap':H(0,1,3,1)+I(12)+format4,
 'loca':H(0,6,len(glyf)//2),'glyf':glyf,'name':names,
 'post':I(0x30000,0)+h(-75,50)+I(0,0,0,0,0),
 'OS/2':H(0)+h(600)+H(400,5,0)+h(650,600,0,75,650,600,0,350,50,300,0)+bytes(10)+I(1,0,0,0)+b'TEST'+H(0x40,65,65)+h(ymax,0,0)+H(ymax,0),
 }
 def checksum(data):
  data+=bytes((-len(data))%4);return sum(struct.unpack('>'+'I'*(len(data)//4),data))&0xffffffff
 tags=sorted(tables);size=12+16*len(tags);records=b'';body=b'';head=None
 for tag in tags:
  data=tables[tag];offset=size+len(body)
  if tag=='head':head=offset
  records+=tag.encode()+I(checksum(data),offset,len(data));body+=data+bytes((-len(data))%4)
 power=2**int(math.log2(len(tags)));f=bytearray(I(0x10000)+H(len(tags),power*16,int(math.log2(power)),len(tags)*16-power*16)+records+body)
 struct.pack_into('>I',f,head+8,(0xb1b0afba-checksum(f))&0xffffffff)
 assert checksum(f)==0xb1b0afba
 return bytes(f)
def pdf(content,curve=False,tail=b'',cancelled=False,media='0 0 72 144',crop=None,form=False,pagegroup=False,mapping=None):
 original=content
 if form:content=b'q /Fm Do Q'
 f=font(curve,cancelled);objects=[b'<< /Type /Catalog /Pages 2 0 R >>',b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',f'<< /Type /Page /Parent 2 0 R /MediaBox [{media}] '.encode()+(f'/CropBox [{crop}] '.encode() if crop else b'')+(b'/Group << /S /Transparency >> ' if pagegroup else b'')+b'/Resources << /Font << /F1 5 0 R >> '+(b'/XObject << /Fm 8 0 R >> ' if form else b'')+b'>> /Contents 4 0 R >>',
 f'<< /Length {len(content+tail)} >>\nstream\n'.encode()+content+tail+b'\nendstream',
 b'<< /Type /Font /Subtype /TrueType /BaseFont /SyntheticInk /FirstChar 65 /LastChar 65 /Widths [600] /FontDescriptor 6 0 R /Encoding /WinAnsiEncoding '+(b'/ToUnicode 8 0 R ' if mapping else b'')+b'>>',
 f'<< /Type /FontDescriptor /FontName /SyntheticInk /FontBBox [0 0 600 {1000 if curve else 800}] /Flags 32 /Ascent 1000 /Descent 0 /CapHeight 800 /ItalicAngle 0 /StemV 80 /FontWeight 400 /FontFile2 7 0 R >>'.encode(),
 f'<< /Length {len(f)} /Length1 {len(f)} >>\nstream\n'.encode()+f+b'\nendstream']
 if mapping:
  cmap=f'/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def /CMapName /InkMapping def /CMapType 2 def 1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <{mapping}> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end'.encode()
  objects.append(f'<< /Length {len(cmap)} >>\nstream\n'.encode()+cmap+b'\nendstream')
 if form:objects.append(f'<< /Type /XObject /Subtype /Form /BBox [0 0 72 144] /Resources << /Font << /F1 5 0 R >> >> /Length {len(original)} >>\nstream\n'.encode()+original+b'\nendstream')
 result=b'%PDF-1.7\n';offsets=[]
 for i,obj in enumerate(objects):offsets.append(len(result));result+=f'{i+1} 0 obj\n'.encode()+obj+b'\nendobj\n'
 xref=len(result);result+=f'xref\n0 {len(objects)+1}\n0000000000 65535 f \n'.encode()
 result+=b''.join(f'{o:010} 00000 n \n'.encode() for o in offsets)+f'trailer\n<< /Root 1 0 R /Size {len(objects)+1} >>\nstartxref\n{xref}\n%%EOF\n'.encode();return result
basic=b'BT /F1 12 Tf 10 20 Td (A) Tj ET'
fixtures={
 'rect':pdf(basic),'curve':pdf(basic,True),
 'painted-space':pdf(basic,mapping='0020'),
 'painted-nbsp':pdf(basic,mapping='00A0'),
 'actualtext-space':pdf(b'/Span << /ActualText <FEFF0020> >> BDC '+basic+b' EMC'),
 'generated-space':pdf(b'BT /F1 12 Tf 10 20 Td (A) Tj 12 0 Td (A) Tj ET'),
 'cross-media':pdf(b'BT /F1 12 Tf 70 20 Td (A) Tj ET'),
 'cross-crop':pdf(basic,crop='0 0 15 144'),
 'inside-crop':pdf(basic,crop='5 15 25 40'),
 'tight-crop':pdf(basic,crop='10 20 17.202 29.601'),
 'nested-form':pdf(basic,form=True),
 'page-group':pdf(basic,pagegroup=True),
 'cancelled':pdf(basic,cancelled=True),
 'deduplicated':pdf(b'BT /F1 12 Tf 10 20 Td (A) Tj .1 0 Td (A) Tj ET'),
 'overprint-objects':pdf(basic+b' BT /F1 12 Tf 10.1 20 Td (A) Tj ET'),
 'rect-mirror':pdf(b'q -1 0 0 1 50 0 cm '+basic+b' Q'),
 'curve-rotate':pdf(b'q 0 1 -1 0 60 10 cm '+basic+b' Q',True),
 'rect-scale':pdf(b'q 2 0 0 3 5 7 cm '+basic+b' Q'),
 'rect-shear':pdf(b'q 1 .2 .3 1 0 0 cm '+basic+b' Q'),
 'curve-shear':pdf(b'q 1 .2 .3 1 0 0 cm '+basic+b' Q',True),
 'clip':pdf(b'q 0 0 1 1 re W n '+basic+b' Q'),
 'invisible':pdf(b'BT /F1 12 Tf 3 Tr 10 20 Td (A) Tj ET'),
 'stroke':pdf(b'BT /F1 12 Tf 2 Tr 10 20 Td (A) Tj ET'),
 'occluded':pdf(basic,tail=b'\n1 g 0 0 72 144 re f'),
 'background':pdf(b'1 g 0 0 72 144 re f 0 g '+basic),
}
records={}
for name,data in fixtures.items():
 (out/(name+'.pdf')).write_bytes(data);records[name]={'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest()}
(out/'fixtures.json').write_text(json.dumps(records,indent=2))
for curve in [False,True]:(out/('curve.ttf' if curve else 'rect.ttf')).write_bytes(font(curve))
