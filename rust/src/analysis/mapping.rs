//! Pure byte-level helpers: BOM detection, newline-kind classification,
//! Unicode-scalar-boundary validation and display line/column. No DOM, no
//! UTF-16, no clock — those are C#/JS concerns.

use crate::analysis::dto::NewlineKind;

/// UTF-8 BOM bytes.
pub const BOM: &[u8] = &[0xEF, 0xBB, 0xBF];

/// Returns true iff `bytes` begins with a UTF-8 BOM.
pub fn has_bom(bytes: &[u8]) -> bool {
    bytes.len() >= 3 && &bytes[..3] == BOM
}

/// Classify the newline style of valid UTF-8 bytes.
///
/// - `Lf` when only bare `\n` (no preceding `\r`) occurs.
/// - `Crlf` when only `\r\n` occurs.
/// - `Mixed` when both `\r\n` and bare `\n` (or a lone `\r`) occur.
/// - `Lf` (default) when no newlines occur.
pub fn classify_newlines(bytes: &[u8]) -> NewlineKind {
    let mut crlf = false;
    let mut bare_lf = false;
    let mut bare_cr = false;
    let mut i = 0;
    while i < bytes.len() {
        let b = bytes[i];
        if b == b'\n' {
            if i > 0 && bytes[i - 1] == b'\r' {
                crlf = true;
            } else {
                bare_lf = true;
            }
        } else if b == b'\r' && (i + 1 >= bytes.len() || bytes[i + 1] != b'\n') {
            bare_cr = true;
        }
        i += 1;
    }
    match (crlf, bare_lf, bare_cr) {
        (true, true, _) | (true, _, true) | (_, true, true) => NewlineKind::Mixed,
        (true, false, false) => NewlineKind::Crlf,
        (false, true, false) => NewlineKind::Lf,
        (false, false, true) => NewlineKind::Mixed,
        (false, false, false) => NewlineKind::Lf,
    }
}

/// True iff `offset` is a Unicode-scalar boundary in valid UTF-8 `bytes`.
///
/// Mirrors the standard library `str::is_char_boundary` semantics: the byte
/// **at** `offset` (not before it) must not be a UTF-8 continuation byte.
/// Out-of-range offsets do not panic: `offset == bytes.len()` is a boundary;
/// `offset > bytes.len()` is not.
pub fn is_scalar_boundary(bytes: &[u8], offset: usize) -> bool {
    match offset {
        0 => true,
        n if n >= bytes.len() => n == bytes.len(),
        _ => bytes[offset] & 0xC0 != 0x80,
    }
}

/// Length (in bytes) of the UTF-8 scalar starting at a lead byte `b`.
/// 1 for ASCII/continuation bytes, 2..4 for multi-byte leads.
pub fn utf8_scalar_len(b: u8) -> usize {
    if b & 0xE0 == 0xC0 {
        2
    } else if b & 0xF0 == 0xE0 {
        3
    } else if b & 0xF8 == 0xF0 {
        4
    } else {
        1
    }
}

/// Compute a 1-based line and 0-based byte column (from line start) for a byte
/// offset into `bytes`. Display-only; not authoritative.
///
/// A CRLF counts as ONE completed newline, but only once the WHOLE `\r\n`
/// pair precedes `offset`. At an offset between the `\r` and the following
/// `\n`, the pair has not completed, so no line break is counted there; the
/// line break is counted at the `\n`. A lone `\r` is a newline at the `\r`.
/// This never panics or underflows for valid boundaries.
pub fn line_column(bytes: &[u8], offset: usize) -> (u32, u32) {
    let mut line: u32 = 1;
    let mut line_start: usize = 0;
    let mut i = 0;
    while i < offset {
        let b = bytes[i];
        if b == b'\n' {
            line += 1;
            line_start = i + 1;
            i += 1;
        } else if b == b'\r' {
            if i + 1 < bytes.len() && bytes[i + 1] == b'\n' {
                // Defer the line break to the \n: the CRLF is complete only once
                // the whole pair precedes `offset`. Skip the \r here; the \n
                // is processed on the next iteration (which increments the line).
                i += 1;
            } else {
                // lone \r: a completed newline at the \r.
                line += 1;
                line_start = i + 1;
                i += 1;
            }
        } else {
            i += 1;
        }
    }
    // `offset >= line_start` always holds: line_start only ever advances to a
    // position <= the loop's current `i`, and `i <= offset` when the loop exits.
    let column = (offset - line_start) as u32;
    (line, column)
}
