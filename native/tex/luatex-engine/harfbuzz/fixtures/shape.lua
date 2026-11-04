local hb = require("harfbuzz")
texio.write_nl("LUAHBVERSION=" .. hb.version())
local cases = {
  {"urdu", "notonastaliq.ttf", "یہ", "urd", "Arab", "rtl"},
  {"digits", "amiri-regular.ttf", "123", "eng", "Latn", "ltr"},
  {"numerators", "amiri-regular.ttf", "123", "eng", "Latn", "ltr", "+numr"},
}
for _, case in ipairs(cases) do
  local font = hb.Font.new(hb.Face.new("/work/project/fonts/" .. case[2]))
  local buffer = hb.Buffer.new()
  buffer:add_utf8(case[3])
  assert(hb.shape(font, buffer, {
    language = hb.Language.new(case[4]), script = hb.Script.new(case[5]),
    direction = hb.Direction.new(case[6]), features = case[7], shapers = {"ot"},
  }))
  for index, glyph in ipairs(buffer:get_glyphs()) do
    local fields = {glyph.codepoint, glyph.cluster, glyph.x_advance,
      glyph.y_advance, glyph.x_offset, glyph.y_offset, glyph.flags or -1}
    texio.write_nl("LUAHBSHAPE=" .. case[1] .. ":" .. index .. ":" .. table.concat(fields, ","))
  end
end
