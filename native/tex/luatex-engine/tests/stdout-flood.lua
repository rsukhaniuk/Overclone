local ok, result = pcall(io.write,string.rep('F',300*1024)) -- no newline: also test TTY buffering
local flushed, result2 = pcall(io.flush)
assert(not ok or result == nil or not flushed or result2 == nil)
