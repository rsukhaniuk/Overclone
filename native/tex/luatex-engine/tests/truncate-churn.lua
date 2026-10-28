local bytes = string.rep('X',1024*1024)
local refused = false
for i=1,32 do
  -- Actual native O_TRUNC, followed by repeated direct first-write allocations.
  -- Logical high-water growth stays small; allocation/copy work cannot be refunded.
  local f = io.open('/work/output/churn.aux','wb')
  if not f then refused=true; break end
  local ok, value = pcall(f.write, f, bytes)
  local closed, value2 = pcall(f.close, f)
  if not ok or value == nil or not closed or value2 == nil then refused=true; break end
end
assert(refused, 'truncate/rewrite churn escaped its allocation budget')
