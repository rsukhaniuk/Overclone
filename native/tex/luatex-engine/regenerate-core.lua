-- Execute the pinned LuaTeX self-generator, not a hand-edited generated C array.
-- Run with the private source-built Lua 5.3 interpreter in overlay/luatexdir/lua.
local f = assert(io.open('luatex-core.lua', 'rb'))
local source = f:read('*a'); f:close()
local body = assert(source:match('%-%- start omit\n(.-)%-%- stop omit'))
io.loaddata = function(name)
  local input = assert(io.open(name, 'rb'))
  local bytes = input:read('*a'); input:close(); return bytes
end
io.savedata = function(name, bytes)
  local output = assert(io.open(name, 'wb'))
  assert(output:write(bytes)); assert(output:close())
end
-- Upstream computes `s` only for an unused stripped-Lua sidecar. The C array
-- encodes `data`, not `s`. Identity here deliberately preserves every source byte.
utilities = { merger = { compact = function(data) return data end } }
assert(load(body, '@upstream-core-self-generator', 't'))()
