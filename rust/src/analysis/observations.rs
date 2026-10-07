//! Bounded associations over lexer tokens, not a TeX parser/expander.
//! Argument ownership is consumed once. Unknown context remains explicit.
use super::dto::{Outcome, SourceFileFacts, Span, Token, TokenKind, MAX_TOKEN_TEXT_BYTES};
use super::knu_dto::{
    FileObservations, Length, Limitation, Observation, ObservationValue, TitleKind,
};
use super::lexer::LookaheadBudget;
use super::profiles::{HEADING, INDENT};

pub const MAX_EXCERPT_BYTES: usize = 256;

/// Exact accepted grammar; all arithmetic is checked before conversion.
pub fn parse_length(raw: &[u8]) -> Option<Length> {
    let raw = trim(raw);
    let mut i = 0;
    let mut digits = 0;
    let mut fractional = 0;
    let mut numerator = 0u64;
    while i < raw.len() && raw[i].is_ascii_digit() {
        digits += 1;
        if digits > 9 {
            return None;
        }
        numerator = numerator
            .checked_mul(10)?
            .checked_add((raw[i] - b'0') as u64)?;
        i += 1;
    }
    if digits == 0 {
        return None;
    }
    if raw.get(i) == Some(&b'.') {
        i += 1;
        while i < raw.len() && raw[i].is_ascii_digit() {
            fractional += 1;
            digits += 1;
            if digits > 9 || fractional > 3 {
                return None;
            }
            numerator = numerator
                .checked_mul(10)?
                .checked_add((raw[i] - b'0') as u64)?;
            i += 1;
        }
        if fractional == 0 {
            return None;
        }
    }
    while raw.get(i).is_some_and(u8::is_ascii_whitespace) {
        i += 1;
    }
    let unit = match &raw[i..] {
        b"cm" => "cm",
        b"mm" => "mm",
        _ => return None,
    };
    if unit == "cm" {
        numerator = numerator.checked_mul(10)?;
    }
    Some(Length {
        numerator_mm: numerator,
        denominator: 10u64.checked_pow(fractional)?,
        unit: unit.into(),
    })
}

pub fn trim(b: &[u8]) -> &[u8] {
    let start = b
        .iter()
        .position(|c| !c.is_ascii_whitespace())
        .unwrap_or(b.len());
    let end = b
        .iter()
        .rposition(|c| !c.is_ascii_whitespace())
        .map_or(start, |p| p + 1);
    &b[start..end]
}

pub fn excerpt(b: &[u8]) -> (String, bool) {
    let mut end = b.len().min(MAX_EXCERPT_BYTES);
    while end < b.len() && b[end] & 0xc0 == 0x80 {
        end -= 1;
    }
    // Caller has strict-valid source and scalar-boundary spans.
    (
        String::from_utf8_lossy(&b[..end]).into_owned(),
        end < b.len(),
    )
}

fn heading(name: &[u8]) -> bool {
    matches!(
        name,
        b"chapter" | b"section" | b"subsection" | b"subsubsection" | b"paragraph" | b"subparagraph"
    )
}

#[derive(Clone, Copy)]
struct Argument {
    start: usize,
    end: usize,
    next: usize,
    owned_end: usize,
}

struct Pass<'a> {
    bytes: &'a [u8],
    tokens: &'a [Token],
    closes: Vec<Option<usize>>,
    depths: Vec<usize>,
    work: LookaheadBudget,
    file_remaining: u32,
    project_remaining: &'a mut u32,
    result: FileObservations,
    stopped: bool,
}

impl Pass<'_> {
    fn spend(&mut self, n: usize) -> bool {
        if !self.work.consume(n) {
            self.result.limit(Limitation::LookaheadBudget);
            self.result.complete = false;
            self.stopped = true;
            false
        } else {
            true
        }
    }

    fn slice(&self, a: Argument) -> &[u8] {
        &self.bytes[a.start..a.end]
    }

    /// Only trivia tokens can intervene; matching closes come from the lexer.
    fn group(&mut self, mut i: usize) -> Option<Argument> {
        while let Some(t) = self.tokens.get(i) {
            match t.kind {
                TokenKind::Comment => {
                    if !self.spend((t.end_byte - t.start_byte) as usize) {
                        return None;
                    }
                    i += 1;
                }
                TokenKind::Text => {
                    let b = &self.bytes[t.start_byte as usize..t.end_byte as usize];
                    if !self.spend(b.len()) || !b.iter().all(u8::is_ascii_whitespace) {
                        return None;
                    }
                    i += 1;
                }
                TokenKind::IncludePath | TokenKind::Label | TokenKind::Reference => i += 1,
                TokenKind::BraceOpen => {
                    let close = self.closes[i]?;
                    let end = self.tokens[close].start_byte as usize;
                    return Some(Argument {
                        start: t.end_byte as usize,
                        end,
                        next: close + 1,
                        owned_end: end + 1,
                    });
                }
                _ => return None,
            }
        }
        None
    }

    fn bounded_argument(&mut self, a: Argument) -> bool {
        if a.end - a.start > MAX_TOKEN_TEXT_BYTES {
            self.result.limit(Limitation::ArgumentTooLong);
            return false;
        }
        self.spend(a.end - a.start)
    }

    fn literal(&mut self, a: Argument) -> bool {
        self.bounded_argument(a)
            && !trim(self.slice(a)).is_empty()
            && !self.slice(a).iter().any(|b| {
                matches!(
                    b,
                    b'\\' | b'{' | b'}' | b'%' | b'$' | b'&' | b'#' | b'^' | b'_' | b'~'
                )
            })
    }

    fn emit(
        &mut self,
        rule: &str,
        command: Span,
        a: Argument,
        value: ObservationValue,
        agrees: Option<bool>,
        reason: Option<Limitation>,
    ) {
        if self.stopped {
            return;
        }
        if self.file_remaining == 0 || *self.project_remaining == 0 {
            self.result.limit(Limitation::ObservationBudget);
            self.result.complete = false;
            self.stopped = true;
            return;
        }
        self.file_remaining -= 1;
        *self.project_remaining -= 1;
        let raw = self.slice(a);
        // Do not rescan/copy an overlong rejected argument just for display.
        let trimmed = if raw.len() <= MAX_TOKEN_TEXT_BYTES {
            trim(raw)
        } else {
            raw
        };
        let start = if raw.len() <= MAX_TOKEN_TEXT_BYTES {
            a.start + raw.len() - raw.trim_ascii_start().len()
        } else {
            a.start
        };
        let end = start + trimmed.len();
        let (excerpt, excerpt_truncated) = excerpt(trimmed);
        let id = format!("{}:{}:{}", self.result.file_id, rule, start);
        self.result.observations.push(Observation {
            id,
            rule_id: rule.into(),
            declaration_span: Span {
                start_byte: command.start_byte,
                end_byte: a.owned_end as u64,
            },
            command_span: command,
            value_span: Span {
                start_byte: start as u64,
                end_byte: end as u64,
            },
            excerpt,
            excerpt_truncated,
            method: if rule == INDENT {
                "literal-setlength"
            } else {
                "literal-heading-argument"
            }
            .into(),
            value,
            agrees,
            reason,
        });
    }

    /// Only numeric-looking ASCII literals can have indent-local grammar/unit
    /// uncertainty. Everything else falls back to common unknown argument
    /// semantics; ownership must not conceal macro/active/parameter content.
    /// Called only after bounded_argument, with this additional inspection
    /// charged to the SAME shared byte-work budget.
    fn unsupported_length_is_literal(&mut self, a: Argument) -> bool {
        // Bound both trimming and ASCII classification, including all-whitespace
        // arguments. The argument length was already capped at 4096 bytes.
        if !self.spend((a.end - a.start).saturating_mul(2)) {
            return false;
        }
        let mut raw = trim(self.slice(a));
        if matches!(raw.first(), Some(b'+' | b'-')) {
            raw = &raw[1..];
        }
        let numeric_start = raw.first().is_some_and(u8::is_ascii_digit)
            || (raw.first() == Some(&b'.') && raw.get(1).is_some_and(u8::is_ascii_digit));
        numeric_start
            && raw.iter().all(|b| {
                b.is_ascii_alphanumeric()
                    || b.is_ascii_whitespace()
                    || matches!(b, b'.' | b',' | b'+' | b'-')
            })
    }

    fn indent(&mut self, i: usize, command: Span, starred: bool) -> Option<usize> {
        let target = self.group(i + 1)?;
        let value = self.group(target.next)?;
        let target_ok = self.bounded_argument(target) && trim(self.slice(target)) == b"\\parindent";
        let value_bounded = self.bounded_argument(value);
        let length = if value_bounded && target_ok && !starred {
            parse_length(self.slice(value))
        } else {
            None
        };
        let agrees = length.as_ref().map(Length::is_required);
        let reason = if length.is_some() {
            None
        } else if target_ok
            && value_bounded
            && !starred
            && self.unsupported_length_is_literal(value)
        {
            Some(Limitation::UnsupportedLength)
        } else {
            Some(Limitation::UnsupportedArgument)
        };
        if let Some(r) = reason {
            self.result.limit(r);
        }
        self.emit(
            INDENT,
            command,
            value,
            ObservationValue::IndentDeclaration {
                target_span: Span {
                    start_byte: target.start as u64,
                    end_byte: target.end as u64,
                },
                length,
            },
            agrees,
            reason,
        );
        Some(value.owned_end)
    }

    fn title(&mut self, command: Span, a: Argument, kind: TitleKind) {
        let valid = self.literal(a);
        let last = if valid {
            std::str::from_utf8(trim(self.slice(a)))
                .ok()
                .and_then(|s| s.chars().next_back())
        } else {
            None
        };
        let reason = if valid {
            None
        } else {
            Some(Limitation::UnsupportedArgument)
        };
        if let Some(r) = reason {
            self.result.limit(r);
        }
        self.emit(
            HEADING,
            command,
            a,
            ObservationValue::HeadingTitle {
                title_kind: kind,
                final_scalar: last,
            },
            last.map(|c| c != '.'),
            reason,
        );
    }

    fn titles(&mut self, i: usize, command: Span) -> Option<usize> {
        let mut next = i + 1;
        let mut short = None;
        if let Some(t) = self.tokens.get(next) {
            if t.kind == TokenKind::OptionalArg && t.start_byte == command.end_byte {
                let start = t.start_byte as usize;
                let end = t.end_byte as usize;
                next += 1;
                while next < self.tokens.len() && self.tokens[next].start_byte < end as u64 {
                    next += 1;
                }
                short = Some(Argument {
                    start: start + 1,
                    end: end - 1,
                    next,
                    owned_end: end,
                });
            }
        }
        let display = self.group(next)?;
        if let Some(a) = short {
            self.title(command.clone(), a, TitleKind::Short);
        }
        if !self.stopped {
            self.title(command, display, TitleKind::Display);
        }
        Some(display.owned_end)
    }

    fn run(&mut self) {
        let mut owned_end = 0;
        let mut document_open = false;
        let mut document_closed = false;
        let mut class_seen = false;
        let mut other_env_depth = 0usize;
        for i in 0..self.tokens.len() {
            if self.stopped {
                break;
            }
            let t = &self.tokens[i];
            if t.start_byte < owned_end {
                continue;
            }
            match t.kind {
                TokenKind::Comment
                | TokenKind::Verbatim
                | TokenKind::OptionalArg
                | TokenKind::IncludePath
                | TokenKind::Label
                | TokenKind::Reference => continue,
                TokenKind::BraceOpen | TokenKind::BraceClose => continue,
                TokenKind::Text => {
                    let b = &self.bytes[t.start_byte as usize..t.end_byte as usize];
                    if !self.spend(b.len()) {
                        break;
                    }
                    if b.iter()
                        .any(|b| matches!(b, b'$' | b'#' | b'&' | b'^' | b'_' | b'~'))
                    {
                        self.result.limit(Limitation::UnsupportedContext);
                    }
                    continue;
                }
                TokenKind::Escape => {
                    let b = &self.bytes[t.start_byte as usize..t.end_byte as usize];
                    if !matches!(
                        b,
                        b"\\%"
                            | b"\\{"
                            | b"\\}"
                            | b"\\&"
                            | b"\\#"
                            | b"\\_"
                            | b"\\$"
                            | b"\\\\"
                            | b"\\ "
                    ) {
                        self.result.limit(Limitation::UnsupportedContext);
                    }
                    continue;
                }
                TokenKind::Command => {}
                _ => {
                    self.result.limit(Limitation::UnknownSemantics);
                    continue;
                }
            }
            let raw = &self.bytes[t.start_byte as usize..t.end_byte as usize];
            if !self.spend(raw.len()) {
                break;
            }
            // The accepted lexer folds exactly these three begin markers into
            // one Command token; their body and closing marker are Verbatim.
            if matches!(
                raw,
                b"\\begin{verbatim}" | b"\\begin{lstlisting}" | b"\\begin{Verbatim}"
            ) {
                continue;
            }
            let starred = raw.ends_with(b"*");
            let name = &raw[1..raw.len() - usize::from(starred)];
            if matches!(name, b"input" | b"include") {
                self.result.limit(Limitation::IncludeNotResolved);
            }
            if self.depths[i] != 0
                || (other_env_depth != 0 && !matches!(name, b"begin" | b"end"))
                || document_closed
            {
                self.result.limit(Limitation::UnsupportedContext);
                continue;
            }
            let command = Span {
                start_byte: t.start_byte,
                end_byte: t.end_byte,
            };
            let end = if name == b"setlength" {
                self.indent(i, command, starred)
            } else if heading(name) {
                self.titles(i, command)
            } else if !starred
                && matches!(
                    name,
                    b"documentclass"
                        | b"begin"
                        | b"end"
                        | b"label"
                        | b"ref"
                        | b"eqref"
                        | b"pageref"
                )
            {
                let a = self.group(i + 1);
                if let Some(a) = a {
                    if !self.literal(a) {
                        self.result.limit(Limitation::UnsupportedArgument);
                    }
                    let arg = trim(self.slice(a));
                    match name {
                        b"documentclass" => {
                            if class_seen || !matches!(arg, b"article" | b"report" | b"book") {
                                self.result.limit(Limitation::UnknownSemantics);
                            }
                            class_seen = true;
                        }
                        b"begin" => {
                            if arg != b"document" {
                                other_env_depth += 1;
                                self.result.limit(Limitation::UnknownSemantics);
                            } else if document_open {
                                self.result.limit(Limitation::UnsupportedContext);
                            } else {
                                document_open = true;
                            }
                        }
                        b"end" => {
                            if arg != b"document" && other_env_depth > 0 {
                                // Still unknown semantics, but later outside-body
                                // literal evidence must not be silently lost.
                                other_env_depth -= 1;
                            } else if arg != b"document" || !document_open {
                                self.result.limit(Limitation::UnsupportedContext);
                            } else {
                                document_open = false;
                                document_closed = true;
                            }
                        }
                        _ => {}
                    }
                    Some(a.owned_end)
                } else {
                    None
                }
            } else if !starred
                && matches!(
                    name,
                    b"tableofcontents" | b"maketitle" | b"newpage" | b"clearpage"
                )
            {
                Some(t.end_byte as usize)
            } else {
                self.result.limit(Limitation::UnknownSemantics);
                Some(t.end_byte as usize)
            };
            if let Some(end) = end {
                owned_end = end as u64;
            } else {
                self.result.limit(Limitation::UnsupportedArgument);
            }
        }
        if document_open || other_env_depth != 0 {
            self.result.limit(Limitation::UnsupportedContext);
        }
    }
}

/// Called AFTER all engine files have been lexed. Observations use only leftover
/// emissions so no later engine file/outcome can be starved or rewritten.
pub fn observe(
    facts: &SourceFileFacts,
    bytes: &[u8],
    eligible: bool,
    work: LookaheadBudget,
    per_file_cap: u32,
    project_remaining: &mut u32,
) -> FileObservations {
    let result = FileObservations {
        file_id: facts.file_id.clone(),
        eligible,
        complete: true,
        observation_count: 0,
        observations: Vec::new(),
        limitations: Vec::new(),
    };
    if !eligible {
        return result;
    }
    let mut pass = Pass {
        bytes,
        tokens: &facts.tokens,
        closes: vec![None; facts.tokens.len()],
        depths: vec![0; facts.tokens.len()],
        work,
        file_remaining: per_file_cap.saturating_sub(facts.token_count),
        project_remaining,
        result,
        stopped: false,
    };
    if matches!(
        facts.engine_outcome,
        Outcome::Indeterminate | Outcome::Unavailable
    ) || facts.truncated
        || facts.brace_depth_exceeded
    {
        pass.result.complete = false;
        pass.result.limit(Limitation::IncompleteEngineScan);
    }
    let mut stack = Vec::new();
    for (i, t) in facts.tokens.iter().enumerate() {
        pass.depths[i] = stack.len();
        if t.kind == TokenKind::Command {
            let raw = &bytes[t.start_byte as usize..t.end_byte as usize];
            if matches!(raw, b"\\input" | b"\\include" | b"\\input*" | b"\\include*") {
                pass.result.limit(Limitation::IncludeNotResolved);
            }
        }
        match t.kind {
            TokenKind::BraceOpen => stack.push(i),
            TokenKind::BraceClose => {
                if let Some(open) = stack.pop() {
                    pass.closes[open] = Some(i);
                } else {
                    pass.result.limit(Limitation::UnmatchedGroup);
                }
            }
            _ => {}
        }
    }
    if !stack.is_empty() {
        pass.result.limit(Limitation::UnmatchedGroup);
    }
    pass.run();
    let mut first: Option<&Length> = None;
    let mut conflict = false;
    for o in &pass.result.observations {
        if let ObservationValue::IndentDeclaration {
            length: Some(n), ..
        } = &o.value
        {
            if let Some(prev) = first {
                conflict |= !prev.equals(n);
            } else {
                first = Some(n);
            }
        }
    }
    if conflict {
        pass.result.limit(Limitation::ConflictingDeclarations);
    }
    pass.result.observation_count = pass.result.observations.len() as u32;
    pass.result
}
