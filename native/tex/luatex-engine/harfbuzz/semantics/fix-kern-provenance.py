#!/usr/bin/env python3
"""Exact source-only candidate correction of the genuine kern writer's node ABI.
No source tag/line is invented or normalized. The actual native kern fields are
read at kern_node_size, rather than glue_node_size beyond the kern allocation.
The master must build/verify both control and semantic resource with this fix.
"""
import hashlib,json,pathlib,sys
source,out=map(pathlib.Path,sys.argv[1:]);raw=source.read_bytes()
expected={
 'c484955177f3e37a67aaecc4586592777ee4ccfaada1c08a7a9dc47e19238ecb':'official approved TeX Live source',
 '77b4fb3882d1722e517847dbfa15bd4e9ea37f900221d84013f513d0888f0127':'accepted graphics4 prepared source, association 8004aad4e299019db9e3850884b5c2959c1d489475794bd6a229054bc3096112',
}
assert hashlib.sha256(raw).hexdigest()in expected,'exact approved source/prepared preimage required'
assert not out.exists(),'fresh output copy required'
s=raw.decode('latin1');start=s.index('static inline void synctex_record_node_kern(halfword p)\n{');end=s.index('\n}\n',start)+3
body=s[start:end];assert body.count('SYNCTEX_TAG_MODEL(p,glue)')==2;assert body.count('SYNCTEX_LINE_MODEL(p,glue)')==2
fixed=body.replace('SYNCTEX_TAG_MODEL(p,glue)','SYNCTEX_TAG_MODEL(p,kern)').replace('SYNCTEX_LINE_MODEL(p,glue)','SYNCTEX_LINE_MODEL(p,kern)')
b=(s[:start]+fixed+s[end:]).encode('latin1');out.parent.mkdir(parents=True,exist_ok=True);out.write_bytes(b)
report={'schemaVersion':1,'status':'source-candidate-not-built-or-adopted','description':'four glue-to-kern source metadata access corrections in genuine kern writer only','source':{'length':len(raw),'sha256':hashlib.sha256(raw).hexdigest()},'candidate':{'length':len(b),'sha256':hashlib.sha256(b).hexdigest()},'preservesCoordinatesAndNativeContext':True,'outputFilteringOrTagSynthesis':False}
pathlib.Path(str(out)+'.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report))
