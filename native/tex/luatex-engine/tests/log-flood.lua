local f = assert(io.open('/work/output/hostile.log','wb'))
local ok, result = pcall(f.write,f,string.rep('L',300*1024))
local closed, result2 = pcall(f.close,f)
assert(not ok or result == nil or not closed or result2 == nil)
