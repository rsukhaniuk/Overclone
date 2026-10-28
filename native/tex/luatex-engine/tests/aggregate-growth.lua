local bytes = string.rep('A',6*1024*1024)
local refused = false
for i=1,4 do
  local f = io.open('/work/output/aggregate-' .. i .. '.aux','wb')
  if not f then refused=true; break end
  local ok, result = pcall(f.write,f,bytes)
  local closed, result2 = pcall(f.close,f)
  if not ok or result == nil or not closed or result2 == nil then refused=true; break end
end
assert(refused, 'aggregate budget was not enforced')
