local f = assert(io.open('/work/output/oversized.aux','wb'))
local ok, result = pcall(f.write, f, string.rep('X',8*1024*1024+1))
assert(not ok or result == nil, 'oversized write unexpectedly succeeded')
pcall(f.close, f)
