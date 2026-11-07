#!/usr/bin/env python3
"""Build-only, exact-source candidate resource overlay. No runtime adoption."""
import hashlib,json,pathlib,sys
upstream,out=map(pathlib.Path,sys.argv[1:])
out.mkdir(parents=True,exist_ok=False)
raw=upstream.read_bytes()
expected='7fdbe5cb95eabb4e8cebccfa8347e877d7fc4ef87904406c90709a798eb9abd8'
assert hashlib.sha256(raw).hexdigest()==expected,'exact accepted Luaotfload 3.26 source required'
s=raw.decode()
def once(a,b):
    global s
    assert s.count(a)==1,a[:70]
    s=s.replace(a,b)
# Keep existing shaping-source line numbers intact for genuine node provenance.
once('local process\n','local process; local semantic_cluster, semantic_spans\n')
once('  local rtl = dir:is_backward()\n','  local rtl = dir:is_backward(); local semantic_token = {}\n')
once('          props.glyph_info = glyph.string or ""\n','          props.glyph_info = glyph.string or ""; semantic_cluster(node, semantic_token, glyph.string or "", rtl)\n')
once('      elseif id == glue_t and getsubtype(node) == spaceskip_t then\n','      elseif id == glue_t and getsubtype(node) == spaceskip_t then semantic_cluster(node, semantic_token, " ", rtl)\n')
once('local function post_process(head)\n','local function post_process(head, semantic_lines)\n')
once('  for n in traverse(head) do\n    local props = properties[n]\n','  if semantic_lines then head = semantic_spans(head) end; for n in traverse(head) do\n    local props = properties[n]\n')
once('      setlist(n, post_process(list))\n','      setlist(n, post_process(list, true))\n')
fragment=(pathlib.Path(__file__).parent/'rtl-spans.lua').read_text()
once("fonts.handlers.otf.registerplugin('harf', process)\n",fragment+"\nfonts.handlers.otf.registerplugin('harf', process)\n")
b=s.encode();name='luaotfload-harf-plug.lua';(out/name).write_bytes(b)
manifest={'schemaVersion':1,'status':'candidate-not-adopted','upstream':{'name':name,'length':len(raw),'sha256':expected,'version':'3.26','license':'GPL-2.0','copyright':'Luaotfload Development Team','authors':['Khaled Hosny','Marcel Krüger']},'files':[{'name':name,'length':len(b),'sha256':hashlib.sha256(b).hexdigest()}],'patches':[{'name':p.name,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()}for p in [pathlib.Path(__file__),pathlib.Path(__file__).parent/'rtl-spans.lua']]}
(out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(json.dumps(manifest))
