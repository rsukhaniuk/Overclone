"""Project-authored guarded patch for the exact BSD-licensed PDFium input.

Keep corresponding original sources and notices. Full PDFium must be rebuilt
with these headers/sources before linking; mixed-definition archives are invalid.
"""
from pathlib import Path
import hashlib

PATCHES = [('core/fpdftext/cpdf_textpage.cpp', 'b66e39269b6bb94436da0f7802b0a0496e6191ddc814a2fe7c52de56bbbfc5fd', '48f81482a995fe229b068bd12518af6cb68b96107826339ce9c6e47a3c176217', [('  return c == 0x2D || c == 0xAD;\n}\n', '  return c == 0x2D || c == 0xAD;\n}\n\n// ActualText is Unicode, including supplementary scalar values on wchar32.\nbool IsValidActualTextCharacter(wchar_t wc) {\n  const uint32_t value = static_cast<uint32_t>(wc);\n  if (value > 0x10ffff || value == 0xfffd || value == 0xfffe || value == 0xffff) {\n    return false;\n  }\n#if defined(WCHAR_T_IS_32_BIT)\n  if (value >= 0xd800 && value <= 0xdfff) {\n    return false;\n  }\n#endif\n  return true;\n}\n'), ('    wchar_t wc = actual_text[i];\n    if ((wc > 0x80 && wc < 0xFFFD) || (wc <= 0x80 && isprint(wc))) {\n      bExist = true;\n', '    wchar_t wc = actual_text[i];\n    if ((wc > 0x80 && IsValidActualTextCharacter(wc)) ||\n        (wc <= 0x80 && isprint(wc))) {\n      bExist = true;\n'), ('    }\n    if (wc >= 0xFFFD) {\n      continue;\n', '    }\n    if (!IsValidActualTextCharacter(wc)) {\n      continue;\n')]), ('core/fpdfapi/font/cpdf_tounicodemap.cpp', 'bb7c511e523a8d59c6b0d9247d0ce7f6aa5e8329474eddd8135bc332e7c55b13', 'c6b9f10f5c835d8d2b6d8d2b44d77c7090782e6a5291d2f55c607ca0cef39928', [('    base_map_ = CPDF_FontGlobals::GetInstance()->GetCID2UnicodeMap(cid_set);\n  }\n}\n\n', '    base_map_ = CPDF_FontGlobals::GetInstance()->GetCID2UnicodeMap(cid_set);\n  }\n#if defined(WCHAR_T_IS_32_BIT)\n  // CMap hex destinations are UTF-16 code units. Decode only after range\n  // arithmetic is complete so lookup and the public UTF-16 search decoder\n  // share Unicode scalar semantics without changing PDF CMap byte rules.\n  for (auto& text : multi_char_vec_) {\n    const ByteString encoded = text.ToUTF16LE();\n    text = WideString::FromUTF16LE(encoded.unsigned_span().first(encoded.GetLength() - 2));\n  }\n#endif\n}\n\n')])]

def apply(source: Path) -> None:
    for relative, before_sha, after_sha, replacements in PATCHES:
        path = source / relative
        data = path.read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if digest == after_sha:
            continue
        if digest != before_sha:
            raise SystemExit(f"unexpected PDFium source for guarded patch: {relative}")
        text = data.decode("utf-8")
        for before, after in replacements:
            if text.count(before) != 1:
                raise SystemExit(f"unexpected PDFium patch context: {relative}")
            text = text.replace(before, after, 1)
        if hashlib.sha256(text.encode("utf-8")).hexdigest() != after_sha:
            raise SystemExit(f"patched PDFium source digest mismatch: {relative}")
        path.write_text(text)
