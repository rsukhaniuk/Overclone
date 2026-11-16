"""Pinned PDFium logical ActualText owner-span ordering repair.

Project-authored patch for the BSD-licensed PDFium source. The source archive
retains its copyright/notices. Only the exact reviewed source hash is accepted.
No PDF-byte rewriting, guessed mappings, JS reversal or source-document edits.
"""
from pathlib import Path
import hashlib

BEFORE_SHA256 = 'c4dc07072e6bb2d2223b58ce94db3d5e5d01514f8c609896dc53c1d0cc1a1c23'
AFTER_SHA256 = 'b66e39269b6bb94436da0f7802b0a0496e6191ddc814a2fe7c52de56bbbfc5fd'

BEFORE_MARKED = '''      if (ProcessMarkedContent(obj)) {
        // TODO(thestig): It would be nicer if there was a way to avoid doing
        // this second reversal.
        ReverseTempTextBufs(orig_char_list_index, orig_buf_index);
      }'''
AFTER_MARKED = '''      // /ActualText is logical replacement text. CloseTempLine orders each
      // owner span atomically; never reverse its internal Unicode sequence.
      ProcessMarkedContent(obj);'''
BEFORE_LINE = '''  const WideString str = temp_text_buf_.MakeString();
  CFX_BidiString bidi(str, /*auto_order=*/true);
  if (rtl_) {
    bidi.SetOverallDirectionRight();
  }
  CFX_BidiChar::Direction current_direction = bidi.OverallDirection();
  for (const auto& segment : bidi) {
    auto str_span = str.span().subspan(segment.start, segment.count);
    auto char_span =
        pdfium::span(temp_char_list_).subspan(segment.start, segment.count);
    if (segment.direction == CFX_BidiChar::Direction::kRight ||
        (segment.direction == CFX_BidiChar::Direction::kNeutral &&
         current_direction == CFX_BidiChar::Direction::kRight)) {
      current_direction = CFX_BidiChar::Direction::kRight;
      const bool is_actual_text =
          !char_span.empty() &&
          char_span[0].char_type() == CharType::kActualText;
      if (is_actual_text) {
        // Since /ActualText strings are already in logical order, pass their
        // characters in forward order instead of reversing them.
        for (auto [c, info] : fxcrt::Zip(str_span, char_span)) {
          AddCharInfo(c, info, /*is_rtl=*/true);
        }
      } else {
        for (size_t i = segment.count; i > 0; --i) {
          AddCharInfo(str_span[i - 1], char_span[i - 1], /*is_rtl=*/true);
        }
      }
    } else {
      if (segment.direction != CFX_BidiChar::Direction::kLeftWeak) {
        current_direction = CFX_BidiChar::Direction::kLeft;
      }
      for (auto [c, info] : fxcrt::Zip(str_span, char_span)) {
        AddCharInfo(c, info, /*is_rtl=*/false);
      }
    }
  }
'''
AFTER_LINE = '''  const WideString str = temp_text_buf_.MakeString();
  struct LogicalUnit {
    size_t start;
    size_t count;
    bool actual_text;
  };
  std::vector<LogicalUnit> units;
  units.reserve(temp_char_list_.size());
  WideString order_text;
  order_text.Reserve(temp_char_list_.size());
  for (size_t i = 0; i < temp_char_list_.size();) {
    const size_t start = i;
    const bool actual = temp_char_list_[i].char_type() == CharType::kActualText;
    if (actual) {
      const auto* owner = temp_char_list_[i].text_object();
      do {
        ++i;
      } while (i < temp_char_list_.size() &&
               temp_char_list_[i].char_type() == CharType::kActualText &&
               temp_char_list_[i].text_object() == owner);
      const WideString logical = str.Substr(start, i - start);
      const bool right = CFX_BidiString(logical, /*auto_order=*/true)
                             .OverallDirection() ==
                         CFX_BidiChar::Direction::kRight;
      // Direction orders this replacement with neighboring objects. These
      // representative codepoints never enter extracted text.
      order_text += right ? static_cast<wchar_t>(0x0627) : L'A';
    } else {
      ++i;
      order_text += str[start];
    }
    units.push_back({start, i - start, actual});
  }
  CFX_BidiString bidi(order_text, /*auto_order=*/true);
  if (rtl_) {
    bidi.SetOverallDirectionRight();
  }
  auto append_unit = [&](const LogicalUnit& unit, bool right) {
    for (size_t i = unit.start; i < unit.start + unit.count; ++i) {
      // A logical replacement must not be mirrored or reversed again.
      // Ordinary visual glyph text keeps its existing normalization path.
      AddCharInfo(str[i], temp_char_list_[i], unit.actual_text ? false : right);
    }
  };
  CFX_BidiChar::Direction current_direction = bidi.OverallDirection();
  for (const auto& segment : bidi) {
    if (segment.direction == CFX_BidiChar::Direction::kRight ||
        (segment.direction == CFX_BidiChar::Direction::kNeutral &&
         current_direction == CFX_BidiChar::Direction::kRight)) {
      current_direction = CFX_BidiChar::Direction::kRight;
      for (size_t i = segment.start + segment.count; i > segment.start; --i) {
        append_unit(units[i - 1], /*right=*/true);
      }
    } else {
      if (segment.direction != CFX_BidiChar::Direction::kLeftWeak) {
        current_direction = CFX_BidiChar::Direction::kLeft;
      }
      for (size_t i = segment.start; i < segment.start + segment.count; ++i) {
        append_unit(units[i], /*right=*/false);
      }
    }
  }
'''

def apply(source: Path) -> None:
    target = source / "core/fpdftext/cpdf_textpage.cpp"
    data = target.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest == AFTER_SHA256:
        return
    if digest != BEFORE_SHA256:
        raise SystemExit("unexpected PDFium text source for ActualText repair")
    text = data.decode("utf-8")
    for before, after in [(BEFORE_MARKED, AFTER_MARKED), (BEFORE_LINE, AFTER_LINE)]:
        if text.count(before) != 1:
            raise SystemExit("unexpected PDFium ActualText patch context")
        text = text.replace(before, after, 1)
    if hashlib.sha256(text.encode("utf-8")).hexdigest() != AFTER_SHA256:
        raise SystemExit("PDFium ActualText patched source digest mismatch")
    target.write_text(text)
