//! Iterative LaTeX lexer over a conservative subset.
//!
//! Not a TeX expander. Produces `Token`s with half-open UTF-8 byte spans into
//! the file's exact BOM-inclusive bytes, on Unicode-scalar boundaries. The
//! brace balance is tracked for the single `ENGINE-STRUCT-001` unmatched
//! opening-brace diagnostic; unmatched closing braces are facts only.
//!
//! Per-file and project counters are enforced at EACH token emission via a
//! central checked `emit`; exhausting a token/depth budget stops the file
//! and marks it `Indeterminate`. Only a FULL, trustworthy scan to EOF may
//! diagnose unmatched openings.

use crate::analysis::dto::{
    EngineDiagnostic, Location, NewlineKind, Span, Token, TokenKind, MAX_TOKEN_TEXT_BYTES,
};
use crate::analysis::mapping;

/// Per-file/project budget state. `tokens_per_file_used` is RESET at the
/// start of each [`scan_file`]; project counters persist across files.
pub struct FileBudget {
    pub tokens_per_file_cap: u32,
    pub tokens_per_file_used: u32,
    pub tokens_per_project_cap: u32,
    pub tokens_per_project_used: u32,
    pub brace_depth_cap: u32,
}

/// Outcome of a bounded lookahead helper. `Exhausted` is DISTINCT from
/// `NotFound`: exhaustion means the shared byte-work budget ran out before a
/// delimiter could be confirmed or ruled out — the caller must stop the file
/// (truncated/Indeterminate), never guess.
pub enum LookaheadResult<T> {
    Found(T),
    NotFound,
    Exhausted,
}

/// ONE shared per-file LOOKAHEAD BYTE-WORK budget. Bounded lookahead helpers
/// (`find_group_close`, `find_optional_close`, `skip_to_group_open`) consume
/// it for every byte/scalar/marker they examine, so repeated incomplete
/// commands cannot rescan the same tail quadratically. A proposed
/// implementation limit (not measured performance): `bytes.len()*4` with a
/// hard 32 MiB ceiling, computed with saturating arithmetic.
pub struct LookaheadBudget {
    remaining: u64,
}

impl LookaheadBudget {
    pub fn new(file_len: usize) -> Self {
        let cap = 32 * 1024 * 1024u64;
        let work = (file_len as u64).saturating_mul(4).min(cap);
        Self { remaining: work }
    }
    /// Returns `false` iff `n` cannot be consumed (budget exhausted); sets
    /// `remaining` to 0 so subsequent calls fail fast.
    pub fn consume(&mut self, n: usize) -> bool {
        let n = n as u64;
        if n > self.remaining {
            self.remaining = 0;
            false
        } else {
            self.remaining -= n;
            true
        }
    }
}

/// Result of scanning one file.
pub struct ScanResult {
    pub tokens: Vec<Token>,
    pub diagnostics: Vec<EngineDiagnostic>,
    pub truncated: bool,
    pub brace_depth_exceeded: bool,
    pub newline_kind: NewlineKind,
    /// True iff this file had at least one unmatched opening brace on a FULL
    /// scan (regardless of whether the diagnostic was stored).
    pub had_unmatched_open: bool,
    /// True iff some unmatched-open diagnostic was dropped because the project
    /// diagnostic cap was hit while scanning this file. The file is then
    /// `Indeterminate`.
    pub diagnostic_dropped: bool,
    /// True iff an include/label/ref argument exceeded the bounded text length.
    pub argument_too_long: bool,
    /// True iff a verbatim/`\verb` span ran to EOF without a terminator.
    pub verbatim_unterminated: bool,
    /// True iff an optional `[...]` argument ran to EOF without a matching `]`.
    pub optional_unterminated: bool,
    /// True iff a literal argument fact could not be extracted (escaped
    /// braces/comments/nested groups inside the argument).
    pub nonliteral_argument: bool,
    /// True iff the shared per-file lookahead byte-work budget was exhausted;
    /// the file scan stopped early (truncated/Indeterminate) without guessing
    /// unmatched braces or fabricating facts.
    pub lookahead_exhausted: bool,
}

/// Supported verbatim environment names (case-sensitive). The argument of
/// `\begin{...}` must equal the name (no macro expansion) to be recognized.
const VERBATIM_ENVS: &[&[u8]] = &[b"verbatim", b"lstlisting", b"Verbatim"];

fn is_special_ascii(b: u8) -> bool {
    matches!(b, b'{' | b'}' | b'\\' | b'%' | b'\n' | b'\r')
}

fn is_ascii_letter(b: u8) -> bool {
    b.is_ascii_uppercase() || b.is_ascii_lowercase()
}

fn find_byte(body: &[u8], target: &[u8], from: usize) -> Option<usize> {
    let mut i = from;
    while i + target.len() <= body.len() {
        if &body[i..i + target.len()] == target {
            return Some(i);
        }
        i += 1;
    }
    None
}

/// Find the matching `}` for a group opened just before `from`, brace-aware,
/// escape-aware AND comment-aware. Returns the byte index of the closing `}`
/// (at relative depth 0). `\` consumes one whole following scalar; a `%` not
/// consumed by a preceding escape starts a comment through CR/LF/EOF, so
/// braces inside that comment cannot change depth or close the group.
/// Every byte/scalar examined consumes the shared `budget`.
fn find_group_close(
    bytes: &[u8],
    from: usize,
    budget: &mut LookaheadBudget,
) -> LookaheadResult<usize> {
    let mut depth = 1usize;
    let mut i = from;
    while i < bytes.len() {
        let b = bytes[i];
        if b == b'\\' {
            let n = if i + 1 < bytes.len() {
                mapping::utf8_scalar_len(bytes[i + 1])
            } else {
                1
            };
            if !budget.consume(1 + n) {
                return LookaheadResult::Exhausted;
            }
            i += 1 + n;
            continue;
        }
        if !budget.consume(1) {
            return LookaheadResult::Exhausted;
        }
        if b == b'%' {
            // comment to end of line/EOF; the line terminator is NOT consumed
            // here (it is processed normally on the next iteration).
            i += 1;
            while i < bytes.len() && bytes[i] != b'\r' && bytes[i] != b'\n' {
                if !budget.consume(1) {
                    return LookaheadResult::Exhausted;
                }
                i += 1;
            }
            continue;
        }
        if b == b'{' {
            depth += 1;
        } else if b == b'}' {
            depth -= 1;
            if depth == 0 {
                return LookaheadResult::Found(i);
            }
        }
        i += 1;
    }
    LookaheadResult::NotFound
}

/// Find the matching `]` for an optional `[...]` argument opened just before
/// `from`. The close is the first `]` at relative brace depth 0. `\`
/// consumes one whole scalar; a `%` not consumed by a preceding escape starts a
/// comment through CR/LF/EOF, so a `]` inside that comment is NOT the close; a
/// `]` inside a nested `{...}` is not the close either. Every byte/scalar
/// examined consumes the shared `budget`.
fn find_optional_close(
    bytes: &[u8],
    from: usize,
    budget: &mut LookaheadBudget,
) -> LookaheadResult<usize> {
    let mut depth = 0usize;
    let mut i = from;
    while i < bytes.len() {
        let b = bytes[i];
        if b == b'\\' {
            let n = if i + 1 < bytes.len() {
                mapping::utf8_scalar_len(bytes[i + 1])
            } else {
                1
            };
            if !budget.consume(1 + n) {
                return LookaheadResult::Exhausted;
            }
            i += 1 + n;
            continue;
        }
        if !budget.consume(1) {
            return LookaheadResult::Exhausted;
        }
        if b == b'%' {
            i += 1;
            while i < bytes.len() && bytes[i] != b'\r' && bytes[i] != b'\n' {
                if !budget.consume(1) {
                    return LookaheadResult::Exhausted;
                }
                i += 1;
            }
            continue;
        }
        if b == b'{' {
            depth += 1;
        } else if b == b'}' {
            if depth > 0 {
                depth = depth.saturating_sub(1);
            }
        } else if b == b']' && depth == 0 {
            return LookaheadResult::Found(i);
        }
        i += 1;
    }
    LookaheadResult::NotFound
}

/// Skip LaTeX ASCII whitespace and comment-to-end-of-line sequences between a
/// control word (and any applicable optional argument) and its literal
/// mandatory `{...}` group. Returns `Found(idx)` of the `{`. Stops (returns
/// `NotFound`) on any other byte (including `\`, `[`, `}`, non-whitespace) or
/// at EOF. This is a pure lookahead: the caller does NOT advance the main scan
/// past it, so all skipped whitespace/comments/braces are still visited and
/// brace-balanced by the main scan. Every byte examined consumes the budget.
/// `Exhausted` means the budget ran out before a `{` could be confirmed.
fn skip_to_group_open(
    bytes: &[u8],
    from: usize,
    budget: &mut LookaheadBudget,
) -> LookaheadResult<usize> {
    let mut i = from;
    while i < bytes.len() {
        let b = bytes[i];
        if !budget.consume(1) {
            return LookaheadResult::Exhausted;
        }
        if b == b' ' || b == b'\t' || b == b'\n' {
            i += 1;
        } else if b == b'\r' {
            i += 1;
            if i < bytes.len() && bytes[i] == b'\n' {
                if !budget.consume(1) {
                    return LookaheadResult::Exhausted;
                }
                i += 1;
            }
        } else if b == b'%' {
            i += 1;
            while i < bytes.len() && bytes[i] != b'\r' && bytes[i] != b'\n' {
                if !budget.consume(1) {
                    return LookaheadResult::Exhausted;
                }
                i += 1;
            }
            // consume the line terminator (CR, LF, or CRLF)
            if i < bytes.len() {
                if bytes[i] == b'\r' {
                    if !budget.consume(1) {
                        return LookaheadResult::Exhausted;
                    }
                    i += 1;
                    if i < bytes.len() && bytes[i] == b'\n' {
                        if !budget.consume(1) {
                            return LookaheadResult::Exhausted;
                        }
                        i += 1;
                    }
                } else {
                    // \n
                    i += 1;
                }
            }
        } else if b == b'{' {
            return LookaheadResult::Found(i);
        } else {
            return LookaheadResult::NotFound;
        }
    }
    LookaheadResult::NotFound
}

/// Kind of the SEPARATE literal-argument fact for input/include/label/ref.
/// Returns `None` for ordinary commands (no copied argument text).
fn fact_kind(name: &[u8]) -> Option<TokenKind> {
    match name {
        b"input" | b"include" => Some(TokenKind::IncludePath),
        b"label" => Some(TokenKind::Label),
        b"ref" | b"eqref" | b"pageref" => Some(TokenKind::Reference),
        _ => None,
    }
}

/// Scan a single file's exact bytes (BOM-inclusive). The caller has already
/// validated strict UTF-8 and BOM consistency. `diag_cap_remaining` is the
/// remaining project diagnostic budget; the lexer decrements it as it stores
/// diagnostics and sets `diagnostic_dropped` when it cannot store one.
pub fn scan_file(
    file_id: &str,
    bytes: &[u8],
    byte_length: u64,
    has_bom: bool,
    budget: &mut FileBudget,
    diag_cap_remaining: &mut u32,
) -> ScanResult {
    // RESET only the per-file counter; project counters persist across files.
    budget.tokens_per_file_used = 0;
    let newline_kind = mapping::classify_newlines(bytes);

    let mut s = Scanner {
        file_id,
        bytes,
        has_bom,
        tokens: Vec::new(),
        diagnostics: Vec::new(),
        budget,
        diag_cap_remaining,
        lookahead: LookaheadBudget::new(bytes.len()),
        truncated: false,
        brace_depth_exceeded: false,
        had_unmatched_open: false,
        diagnostic_dropped: false,
        argument_too_long: false,
        verbatim_unterminated: false,
        optional_unterminated: false,
        nonliteral_argument: false,
        lookahead_exhausted: false,
        brace_stack: Vec::new(),
        depth: 0,
    };
    s.run();
    let Scanner {
        tokens,
        diagnostics,
        truncated,
        brace_depth_exceeded,
        had_unmatched_open,
        diagnostic_dropped,
        argument_too_long,
        verbatim_unterminated,
        optional_unterminated,
        nonliteral_argument,
        lookahead_exhausted,
        budget: _,
        diag_cap_remaining: _,
        lookahead: _,
        file_id: _,
        bytes: _,
        has_bom: _,
        brace_stack: _,
        depth: _,
    } = s;
    let _ = byte_length; // echoed into SourceFileFacts by the caller
    ScanResult {
        tokens,
        diagnostics,
        truncated,
        brace_depth_exceeded,
        newline_kind,
        had_unmatched_open,
        diagnostic_dropped,
        argument_too_long,
        verbatim_unterminated,
        optional_unterminated,
        nonliteral_argument,
        lookahead_exhausted,
    }
}

struct Scanner<'a> {
    file_id: &'a str,
    bytes: &'a [u8],
    has_bom: bool,
    tokens: Vec<Token>,
    diagnostics: Vec<EngineDiagnostic>,
    budget: &'a mut FileBudget,
    diag_cap_remaining: &'a mut u32,
    lookahead: LookaheadBudget,
    truncated: bool,
    brace_depth_exceeded: bool,
    had_unmatched_open: bool,
    diagnostic_dropped: bool,
    argument_too_long: bool,
    verbatim_unterminated: bool,
    optional_unterminated: bool,
    nonliteral_argument: bool,
    lookahead_exhausted: bool,
    brace_stack: Vec<usize>,
    depth: u32,
}

impl<'a> Scanner<'a> {
    /// Central checked emit. Returns `false` iff a token cap is already
    /// exhausted; the caller must stop scanning the file (mark truncated).
    /// Never appends beyond either the per-file or project cap.
    #[allow(clippy::too_many_arguments)]
    fn emit(&mut self, kind: TokenKind, start: usize, end: usize) -> bool {
        self.emit_note(kind, start, end, None, None)
    }

    #[allow(clippy::too_many_arguments)]
    fn emit_note(
        &mut self,
        kind: TokenKind,
        start: usize,
        end: usize,
        text: Option<String>,
        note: Option<String>,
    ) -> bool {
        // Enforce caps at EACH emission, not just at the loop boundary.
        if self.budget.tokens_per_file_used >= self.budget.tokens_per_file_cap
            || self.budget.tokens_per_project_used >= self.budget.tokens_per_project_cap
        {
            self.truncated = true;
            return false;
        }
        // Spans are scalar boundaries by construction; assert defensively.
        debug_assert!(mapping::is_scalar_boundary(self.bytes, start));
        debug_assert!(mapping::is_scalar_boundary(self.bytes, end));
        self.budget.tokens_per_file_used += 1;
        self.budget.tokens_per_project_used += 1;
        self.tokens.push(Token {
            kind,
            start_byte: start as u64,
            end_byte: end as u64,
            text,
            note,
        });
        true
    }

    fn run(&mut self) {
        // BOM is skipped (no token emitted). Spans reference BOM-inclusive
        // offsets, so the first content scalar begins at byte 3 with a BOM.
        let mut i = if self.has_bom { 3 } else { 0 };

        while i < self.bytes.len() {
            // A cheap pre-check; emit() re-checks at each emission too.
            if self.truncated {
                break;
            }
            let b = self.bytes[i];

            // ---- comment: % to end of line/EOF ----
            if b == b'%' {
                let start = i;
                let mut j = i + 1;
                while j < self.bytes.len() && self.bytes[j] != b'\n' && self.bytes[j] != b'\r' {
                    j += 1;
                }
                if !self.emit(TokenKind::Comment, start, j) {
                    break;
                }
                i = j;
                continue;
            }

            // ---- backslash: command / escape / verbatim ----
            if b == b'\\' {
                let start = i;
                if i + 1 >= self.bytes.len() {
                    if !self.emit_note(
                        TokenKind::Escape,
                        start,
                        i + 1,
                        None,
                        Some("unterminated-escape".to_string()),
                    ) {
                        break;
                    }
                    i += 1;
                    continue;
                }
                let next = self.bytes[i + 1];

                // Parse the FULL ASCII control-word name first.
                if is_ascii_letter(next) {
                    let mut j = i + 1;
                    while j < self.bytes.len() && is_ascii_letter(self.bytes[j]) {
                        j += 1;
                    }
                    let mut end = j;
                    if end < self.bytes.len() && self.bytes[end] == b'*' {
                        end += 1;
                    }
                    let name = &self.bytes[i + 1..j];

                    // `\verb` (exact) with optional `*`: single verbatim token with
                    // a delimiter that is ONE WHOLE UTF-8 scalar. The optional `*`
                    // was ALREADY consumed into `end` during control-word parsing,
                    // so the delimiter is the scalar at `end` (do NOT re-check `*`).
                    if name == b"verb" {
                        let delim_idx = end;
                        if delim_idx < self.bytes.len() {
                            let delim_len = mapping::utf8_scalar_len(self.bytes[delim_idx]);
                            if delim_idx + delim_len <= self.bytes.len() {
                                let delim_bytes = &self.bytes[delim_idx..delim_idx + delim_len];
                                // The delimiter must be a non-whitespace scalar.
                                if !delim_bytes
                                    .iter()
                                    .any(|&c| c == b' ' || c == b'\t' || c == b'\n' || c == b'\r')
                                {
                                    if let Some(close) =
                                        find_byte(self.bytes, delim_bytes, delim_idx + delim_len)
                                    {
                                        let verb_end = close + delim_len;
                                        if !self.emit(TokenKind::Verbatim, start, verb_end) {
                                            break;
                                        }
                                        i = verb_end;
                                        continue;
                                    } else {
                                        // unterminated \verb: verbatim to EOF, limitation note.
                                        if !self.emit_note(
                                            TokenKind::Verbatim,
                                            start,
                                            self.bytes.len(),
                                            None,
                                            Some("verbatim-unterminated".to_string()),
                                        ) {
                                            break;
                                        }
                                        self.verbatim_unterminated = true;
                                        i = self.bytes.len();
                                        continue;
                                    }
                                }
                            }
                        }
                    }

                    // `\begin{verbatim|lstlisting|Verbatim}` — verbatim environment.
                    if name == b"begin" && end < self.bytes.len() && self.bytes[end] == b'{' {
                        if let Some((env, env_end)) = match_verbatim_env(self.bytes, end + 1) {
                            // `\begin{env}` is a command token.
                            if !self.emit(TokenKind::Command, start, env_end) {
                                break;
                            }
                            let end_marker = end_env_marker(env);
                            let end_marker_bytes = end_marker.as_bytes();
                            if let Some(pos) = find_byte(self.bytes, end_marker_bytes, env_end) {
                                let body_end = pos;
                                // verbatim body covers env_end..body_end
                                if !self.emit(TokenKind::Verbatim, env_end, body_end) {
                                    break;
                                }
                                // the `\end{env}` is a verbatim token (not a command)
                                if !self.emit(
                                    TokenKind::Verbatim,
                                    body_end,
                                    body_end + end_marker_bytes.len(),
                                ) {
                                    break;
                                }
                                i = body_end + end_marker_bytes.len();
                            } else {
                                // unterminated verbatim environment: body to EOF, limitation note
                                if !self.emit_note(
                                    TokenKind::Verbatim,
                                    env_end,
                                    self.bytes.len(),
                                    None,
                                    Some("verbatim-unterminated".to_string()),
                                ) {
                                    break;
                                }
                                self.verbatim_unterminated = true;
                                i = self.bytes.len();
                            }
                            continue;
                        }
                    }

                    // Ordinary control word (incl. \verbatim, \verbatiminput, ...).
                    // The command token is always `Command`; literal-argument
                    // facts (input/include/label/ref) are emitted separately.
                    if !self.emit(TokenKind::Command, start, end) {
                        break;
                    }

                    // Optional `[...]` argument: record an optionalArg metadata
                    // span via a brace- and comment-aware bounded lookahead, but
                    // do NOT skip lexical analysis of the contents — the main scan
                    // continues into the brackets and still tracks inner braces.
                    // `group_cursor` is the position after this command (or after
                    // the optional arg, if present) from which the literal
                    // mandatory-group lookahead begins.
                    let mut group_cursor = end;
                    if end < self.bytes.len() && self.bytes[end] == b'[' {
                        match find_optional_close(self.bytes, end + 1, &mut self.lookahead) {
                            LookaheadResult::Found(close) => {
                                if !self.emit(TokenKind::OptionalArg, end, close + 1) {
                                    break;
                                }
                                group_cursor = close + 1;
                            }
                            LookaheadResult::NotFound => {
                                // unterminated optional argument: limitation, no OptionalArg.
                                self.optional_unterminated = true;
                            }
                            LookaheadResult::Exhausted => {
                                self.truncated = true;
                                self.lookahead_exhausted = true;
                                break;
                            }
                        }
                    }

                    // Literal-argument fact for include/input/label/ref: skip
                    // LaTeX ASCII whitespace and comment-to-end-of-line sequences
                    // (after the command and any optional argument) to the literal
                    // mandatory `{...}` group, then emit a SEPARATE fact whose span
                    // covers the argument interior. This is a pure lookahead — the
                    // main scan still visits/preserves every skipped byte and does
                    // normal brace balancing; `i` is not advanced past `end`.
                    if let Some(fact_kind) = fact_kind(name) {
                        match skip_to_group_open(self.bytes, group_cursor, &mut self.lookahead) {
                            LookaheadResult::Found(open_idx) => {
                                let arg_start = open_idx + 1;
                                match find_group_close(self.bytes, arg_start, &mut self.lookahead) {
                                    LookaheadResult::Found(close) => {
                                        let interior = &self.bytes[arg_start..close];
                                        // Literal only if it contains no
                                        // escape/comment/brace constructs.
                                        let literal = interior.iter().all(|&c| {
                                            c != b'\\' && c != b'%' && c != b'{' && c != b'}'
                                        });
                                        if literal && interior.len() <= MAX_TOKEN_TEXT_BYTES {
                                            let text =
                                                String::from_utf8_lossy(interior).into_owned();
                                            if !self.emit_note(
                                                fact_kind,
                                                arg_start,
                                                close,
                                                Some(text),
                                                None,
                                            ) {
                                                break;
                                            }
                                        } else if !literal {
                                            if !self.emit_note(
                                                fact_kind,
                                                arg_start,
                                                close,
                                                None,
                                                Some("nonliteral-argument".to_string()),
                                            ) {
                                                break;
                                            }
                                            self.nonliteral_argument = true;
                                        } else {
                                            // interior too long for a bounded fact text.
                                            if !self.emit_note(
                                                fact_kind,
                                                arg_start,
                                                close,
                                                None,
                                                Some("argument-too-long".to_string()),
                                            ) {
                                                break;
                                            }
                                            self.argument_too_long = true;
                                        }
                                        // Do not advance `i`; the main scan processes the group.
                                    }
                                    LookaheadResult::NotFound => {
                                        // unterminated group after \input/\label/etc: limitation.
                                        self.nonliteral_argument = true;
                                    }
                                    LookaheadResult::Exhausted => {
                                        self.truncated = true;
                                        self.lookahead_exhausted = true;
                                        break;
                                    }
                                }
                            }
                            LookaheadResult::NotFound => {
                                // no mandatory group follows: no fact, no error.
                            }
                            LookaheadResult::Exhausted => {
                                self.truncated = true;
                                self.lookahead_exhausted = true;
                                break;
                            }
                        }
                    }

                    i = end;
                    continue;
                }

                // Control symbol / escape: `\` + ONE WHOLE following scalar.
                let n = mapping::utf8_scalar_len(next);
                let end = i + 1 + n;
                if !self.emit(TokenKind::Escape, start, end) {
                    break;
                }
                i = end;
                continue;
            }

            // ---- braces ----
            if b == b'{' {
                if self.depth < self.budget.brace_depth_cap {
                    self.depth += 1;
                    self.brace_stack.push(i);
                } else {
                    // Depth cap breached: STOP file scanning (do not continue).
                    self.brace_depth_exceeded = true;
                    self.truncated = true;
                    break;
                }
                if !self.emit(TokenKind::BraceOpen, i, i + 1) {
                    break;
                }
                i += 1;
                continue;
            }
            if b == b'}' {
                if self.depth > 0 {
                    self.depth -= 1;
                    self.brace_stack.pop();
                } else {
                    // unmatched closing brace: fact note only, NO diagnostic.
                    if !self.emit_note(
                        TokenKind::BraceClose,
                        i,
                        i + 1,
                        None,
                        Some("unpaired-close".to_string()),
                    ) {
                        break;
                    }
                    i += 1;
                    continue;
                }
                if !self.emit(TokenKind::BraceClose, i, i + 1) {
                    break;
                }
                i += 1;
                continue;
            }

            // ---- literal text run (collect a run of ordinary bytes) ----
            let start = i;
            let mut j = i;
            while j < self.bytes.len() {
                let c = self.bytes[j];
                if is_special_ascii(c) {
                    break;
                }
                if c & 0x80 != 0 {
                    // skip a whole UTF-8 scalar so the run end is a scalar boundary
                    let n = mapping::utf8_scalar_len(c);
                    j = (j.saturating_add(n)).min(self.bytes.len());
                } else {
                    j += 1;
                }
            }
            if j == start {
                // Should not happen (specials handled above); advance one to progress.
                j = i + 1;
            }
            if !self.emit(TokenKind::Text, start, j) {
                break;
            }
            i = j;
        }

        // ---- EOF: diagnose unmatched openings ONLY on a FULL, trustworthy scan ----
        // On token/depth truncation or an opaque unterminated construct,
        // remaining stack entries are uncertain, not confirmed unmatched.
        let suppress = self.truncated
            || self.brace_depth_exceeded
            || self.verbatim_unterminated
            || self.optional_unterminated;
        if !suppress {
            while let Some(open_off) = self.brace_stack.pop() {
                self.had_unmatched_open = true;
                if *self.diag_cap_remaining > 0 {
                    *self.diag_cap_remaining -= 1;
                    let (line, column) = mapping::line_column(self.bytes, open_off);
                    self.diagnostics.push(EngineDiagnostic {
                        id: diagnostic_id(self.file_id, open_off),
                        rule_id: "ENGINE-STRUCT-001".to_string(),
                        origin: "engine".to_string(),
                        severity: "warning".to_string(),
                        file_id: self.file_id.to_string(),
                        span: Some(Span {
                            start_byte: open_off as u64,
                            end_byte: (open_off + 1) as u64,
                        }),
                        message: "Unmatched opening brace (group never closed).".to_string(),
                        primary_location: Some(Location { line, column }),
                    });
                } else {
                    self.diagnostic_dropped = true;
                }
            }
        }
    }
}

/// Match a verbatim environment argument starting at `idx` (just after `begin{`).
/// Returns `(env_name, end_index)` where `end_index` is just past the closing `}`.
fn match_verbatim_env(bytes: &[u8], idx: usize) -> Option<(&'static [u8], usize)> {
    for env in VERBATIM_ENVS {
        if bytes.len() > idx + env.len() && &bytes[idx..idx + env.len()] == *env {
            let after = idx + env.len();
            if after < bytes.len() && bytes[after] == b'}' {
                return Some((env, after + 1));
            }
        }
    }
    None
}

/// Build the `\end{env}` marker bytes for a recognized env name.
fn end_env_marker(env: &[u8]) -> String {
    let mut s = String::from("\\end{");
    s.push_str(std::str::from_utf8(env).unwrap_or(""));
    s.push('}');
    s
}

/// Deterministic diagnostic id from file id + rule + span.
fn diagnostic_id(file_id: &str, open_off: usize) -> String {
    format!("ENGINE-STRUCT-001:{}:{}", file_id, open_off)
}
