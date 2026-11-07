-- Candidate Luaotfload resource patch: complete logical line provenance.
-- Source/shaping/glyph metrics remain unchanged; markers follow linebreaking.
local semantic_p = 'overclone_harf_logical_cluster_v1'
function semantic_cluster(n, token, text, rtl)
  setprop(n, semantic_p, {token = token, text = text, rtl = rtl})
end
function semantic_spans(head)
  local first, last, have_rtl, unsupported, parts, units = nil, nil, false, false, {}, 0
  local covered, visited = {}, 0
  local function collect(list, outer, depth)
  if depth > 32 then unsupported = true; return end
  for n, id in traverse(list) do
    visited = visited + 1
    if visited > 65536 then unsupported = true; return end
    local top = outer or n
    local props = properties[n]
    local p = props and rawget(props, semantic_p)
    if id == glyph_t then
      first, last = first or top, top
      if not p then unsupported = true end
    elseif id == disc_t then
      -- After linebreaking only the retained replacement is actually drawn.
      -- Selected pre/post branches are already part of the physical line.
      local replace = getfield(n, 'replace')
      if replace then collect(replace, top, depth + 1) end
    elseif id == hlist_t and getfield(n, 'list') then
      unsupported = true
    end
    if p and (id == glyph_t or id == glue_t) then
      have_rtl = have_rtl or p.rtl
      for _, cp in utf8.codes(p.text) do
        local encoded = to_utf16_hex(cp)
        units = units + #encoded // 4
        if units > 16384 then unsupported = true; break end
        parts[#parts + 1] = encoded
      end
      covered[#covered + 1] = props
    end
  end
  end
  collect(head, nil, 0)
  if not have_rtl or not first then return head end
  if unsupported then
    tex.error('Overclone HarfBuzz unsupported or oversized logical line provenance')
    return head
  end
  for _, props in ipairs(covered) do
    props[startactual_p], props[endactual_p], props[semantic_p] = nil, nil, nil
  end
  head = insertbefore(head, first, pageliteral('/Span<</ActualText<FEFF'..table.concat(parts)..'>>>BDC', first))
  head = insertafter(head, last, pageliteral('EMC', last))
  return head
end
