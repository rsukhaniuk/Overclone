local refused = false
for i=1,256 do
  local name = '/work/output/node-' .. i .. '.aux'
  local f = io.open(name,'wb')
  if not f then refused=true; break end
  f:close()
  assert(os.remove(name)) -- deletion must not refund lifetime node attempts
end
assert(refused, 'new-node budget was not enforced')
