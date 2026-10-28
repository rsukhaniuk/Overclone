local f = assert(io.open('/work/output/seek.aux','wb'))
assert(f:seek('set',8*1024*1024-1))
local ok, result = pcall(f.write, f, 'XY')
-- fclose flushes buffered bytes too: the failure latch must survive either route.
local closed, result2 = pcall(f.close, f)
assert(not ok or result == nil or not closed or result2 == nil)
