-- Native test observer: exactly one original spelling must exist in each pair.
local function read(path)
  local f = io.open(path, 'rb')
  if not f then return nil end
  local bytes = assert(f:read('*a'))
  assert(f:close())
  return bytes
end
local function unique(a, b)
  local first, second = read(a), read(b)
  assert((first ~= nil) ~= (second ~= nil), 'exactly one original spelling: ' .. a)
  return first and a or b, first or second
end
local root = unique('/work/project/тези/й-é.tex', '/work/project/тези/й-é.tex')
local chapter, bytes = unique('/work/project/розділи/й-é.tex', '/work/project/розділи/й-é.tex')
assert(bytes == read('/work/project/expected-chapter.bin'))
local font, fontbytes = unique('/work/runtime/fonts/й-é.ttf', '/work/runtime/fonts/й-é.ttf')
assert(fontbytes == read('/work/project/expected-font.bin'))
texio.write_nl('term and log', 'EXACT-PATH-ROOT ' .. root)
texio.write_nl('term and log', 'EXACT-PATH-INCLUDE ' .. chapter)
texio.write_nl('term and log', 'EXACT-PATH-FONT ' .. font)
tex.sprint('\\input{' .. chapter .. '}')
