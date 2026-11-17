#!/usr/bin/env python3
"""Self-generated incompressible RGB PNG for genuine8MiB-output refusal."""
from pathlib import Path
import argparse,random,struct,zlib,json,hashlib
p=argparse.ArgumentParser(description=__doc__);p.add_argument('out',type=Path);a=p.parse_args();assert not a.out.exists();a.out.mkdir(parents=True)
w=h=1673;r=random.Random(20261118)
def chunk(kind,data):return struct.pack('>I',len(data))+kind+data+struct.pack('>I',zlib.crc32(kind+data)&0xffffffff)
scan=b''.join(b'\0'+r.randbytes(w*3)for _ in range(h));compressed=zlib.compress(scan,6)
png=b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',w,h,8,2,0,0,0))+chunk(b'IDAT',compressed)+chunk(b'IEND',b'')
assert 8388608<len(compressed)<16777216
(a.out/'figure.png').write_bytes(png)
# Keep this metadata outside the actual project input directory.
(a.out.parent/(a.out.name+'-oracle.json')).write_text(json.dumps({'kind':'synthetic-valid-RGB-PNG','width':w,'height':h,'encodedBytes':len(png),'encodedSha256':hashlib.sha256(png).hexdigest(),'rgbBytes':w*h*3,'compressedRasterBytes':len(compressed),'pdfOutputCap':8388608,'nativePdfOutputExpected':'refused-and-cleared'},indent=2)+'\n')
