//! Bounded associations over lexer tokens, not a TeX parser/expander.
//! Argument ownership is consumed once. Unknown context remains explicit.
use super::dto::{Outcome, SourceFileFacts, Span, Token, TokenKind, MAX_TOKEN_TEXT_BYTES};
use super::knu_dto::{
    FileObservations, Length, Limitation, MarginSide, Observation, ObservationValue, TitleKind,
};
use super::lexer::{LookaheadBudget, LookaheadResult};
use super::ownership::{self, Argument};
use super::profiles::{HEADING, INDENT, MARGINS};

mod formatting;

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
    margins_enabled: bool,
    packages_enabled: bool,
    geometry_count: u32,
    geometry_imports: u32,
    package_needs_declaration: bool,
    formatting_enabled: bool,
    formatting_safe: bool,
    setspace_imports: u32,
    caption_environment: Option<super::knu_dto::CaptionEnvironment>,
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
    fn group(&mut self, i: usize) -> Option<Argument> {
        match ownership::group(self.tokens, self.bytes, &self.closes, i, &mut self.work) {
            LookaheadResult::Found(a) => Some(a),
            LookaheadResult::NotFound => None,
            LookaheadResult::Exhausted => {
                self.result.limit(Limitation::LookaheadBudget);
                self.result.complete = false;
                self.stopped = true;
                None
            }
        }
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
            method: if rule == super::profiles::PAPER {
                "literal-paper-intent"
            } else if rule == super::profiles::LINE_SPACING {
                "declared-line-spacing-intent"
            } else if rule == super::profiles::CAPTION {
                "literal-caption-title"
            } else if rule == INDENT {
                "literal-setlength"
            } else if rule == MARGINS {
                "literal-geometry-option"
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

    /// Finite options within the lexer's owned group, never a source-file scan.
    /// Reject active/nested syntax before splitting so nested commas cannot
    /// masquerade as independent margin declarations. Each group is visited once.
    fn geometry(&mut self, i: usize, command: Span, starred: bool) -> Option<usize> {
        self.geometry_count += 1;
        if self.geometry_count > 1 {
            self.result.limit(Limitation::MultipleGeometryDeclarations);
        }
        let Some(a) = self.group(i + 1) else {
            self.result.limit(Limitation::UnsupportedGeometry);
            return None;
        };
        self.geometry_options(a, command, starred)
    }

    // Shared finite body parser: argument/declaration ownership comes ONLY from
    // the existing lexer. Old command callers retain identical work accounting.
    fn geometry_options(&mut self, a: Argument, command: Span, starred: bool) -> Option<usize> {
        if !self.bounded_argument(a) || !self.spend((a.end - a.start).saturating_mul(2)) {
            self.result.limit(Limitation::UnsupportedGeometry);
            return Some(a.owned_end);
        }
        let dynamic = self.slice(a).iter().any(|b| {
            matches!(
                b,
                b'\\' | b'{' | b'}' | b'%' | b'$' | b'&' | b'#' | b'^' | b'_' | b'~'
            )
        });
        if starred || dynamic {
            self.result.limit(Limitation::UnsupportedGeometry);
            // Owned arguments must not hide arbitrary executable TeX context
            // from the existing rules/fix gates (same policy as setlength).
            if dynamic {
                if self.formatting_enabled {
                    self.formatting_safe = false;
                }
                self.result.limit(Limitation::UnsupportedArgument);
            }
            return Some(a.owned_end);
        }
        let mut seen = 0u8;
        let mut offset = a.start;
        while offset <= a.end && !self.stopped {
            let end = self.bytes[offset..a.end]
                .iter()
                .position(|b| *b == b',')
                .map_or(a.end, |p| offset + p);
            if self.formatting_enabled && self.formatting_safe {
                self.paper_option(
                    command.clone(),
                    Argument {
                        start: offset,
                        end,
                        next: a.next,
                        owned_end: a.owned_end,
                    },
                );
                if self.stopped {
                    return Some(a.owned_end);
                }
            }
            let option = &self.bytes[offset..end];
            if let Some(eq) = option.iter().position(|b| *b == b'=') {
                let side = match trim(&option[..eq]) {
                    b"left" => Some(MarginSide::Left),
                    b"right" => Some(MarginSide::Right),
                    b"top" => Some(MarginSide::Top),
                    b"bottom" => Some(MarginSide::Bottom),
                    _ => None,
                };
                if let Some(side) = side {
                    if seen & side.bit() != 0 {
                        self.result.limit(Limitation::MultipleGeometryDeclarations);
                    }
                    seen |= side.bit();
                    let value = Argument {
                        start: offset + eq + 1,
                        end,
                        next: a.next,
                        owned_end: a.owned_end,
                    };
                    let length = parse_length(self.slice(value));
                    let agrees = length.as_ref().map(|n| side.agrees(n));
                    let reason = if length.is_some() {
                        None
                    } else {
                        Some(Limitation::UnsupportedGeometry)
                    };
                    if let Some(r) = reason {
                        self.result.limit(r);
                    }
                    self.emit(
                        MARGINS,
                        command.clone(),
                        value,
                        ObservationValue::MarginDeclaration { side, length },
                        agrees,
                        reason,
                    );
                } else {
                    self.result.limit(Limitation::UnsupportedGeometry);
                }
            } else {
                self.result.limit(Limitation::UnsupportedGeometry);
            }
            if end == a.end {
                break;
            }
            offset = end + 1;
        }
        if seen != 15 {
            self.result.limit(Limitation::MissingMarginSides);
        }
        Some(a.owned_end)
    }

    fn geometry_package(&mut self, i: usize, command: Span, starred: bool) -> Option<usize> {
        let mut next = i + 1;
        let mut options = None;
        if let Some(t) = self.tokens.get(next) {
            // Exactly the already lexed immediate optional argument. Do not scan
            // for brackets or reinterpret missing/unterminated optional metadata.
            if t.kind == TokenKind::OptionalArg && t.start_byte == command.end_byte {
                let start = t.start_byte as usize;
                let end = t.end_byte as usize;
                next += 1;
                while next < self.tokens.len() && self.tokens[next].start_byte < end as u64 {
                    if !self.spend(1) {
                        return None;
                    }
                    next += 1;
                }
                options = Some(Argument {
                    start: start + 1,
                    end: end - 1,
                    next,
                    owned_end: end,
                });
            }
        }
        let package = self.group(next)?;
        if self.formatting_enabled
            && !starred
            && self.bounded_argument(package)
            && trim(self.slice(package)) == b"setspace"
            && options.is_none()
        {
            self.setspace_imports += 1;
            if self.setspace_imports > 1 {
                self.result.limit(Limitation::LineSpacingUncertain);
            }
            return Some(package.owned_end);
        }
        if starred || !self.bounded_argument(package) || trim(self.slice(package)) != b"geometry" {
            if self.formatting_enabled {
                self.formatting_safe = false;
            }
            self.result.limit(Limitation::UnknownSemantics);
            return Some(package.owned_end);
        }
        self.geometry_imports += 1;
        if self.geometry_imports > 1 {
            self.result.limit(Limitation::MultipleGeometryDeclarations);
        }
        if let Some(mut a) = options {
            self.geometry_count += 1;
            if self.geometry_count > 1 {
                self.result.limit(Limitation::MultipleGeometryDeclarations);
            }
            a.owned_end = package.owned_end;
            self.geometry_options(a, command, false)
        } else {
            // A no-option import is context, not a second margin declaration.
            // This precise form must precede exactly one explicit declaration.
            if self.geometry_count != 0 {
                self.result.limit(Limitation::MultipleGeometryDeclarations);
            }
            self.package_needs_declaration = true;
            Some(package.owned_end)
        }
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
                        if self.formatting_enabled {
                            self.formatting_safe = false;
                        }
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
                        if self.formatting_enabled {
                            self.formatting_safe = false;
                        }
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
            if self.formatting_enabled
                && (name.starts_with(b"if")
                    || matches!(
                        name,
                        b"else"
                            | b"fi"
                            | b"newcommand"
                            | b"renewcommand"
                            | b"def"
                            | b"let"
                            | b"catcode"
                    ) && name != b"renewcommand")
            {
                self.formatting_safe = false;
                self.result.limit(Limitation::UnsupportedContext);
            }
            if self.formatting_enabled
                && name == b"caption"
                && self.depths[i] == 0
                && !document_closed
            {
                let command = Span {
                    start_byte: t.start_byte,
                    end_byte: t.end_byte,
                };
                if self.formatting_safe
                    && other_env_depth == 1
                    && self.caption_environment.is_some()
                {
                    if let Some(end) = self.caption(i, command, starred) {
                        owned_end = end as u64;
                    } else {
                        self.result.limit(Limitation::CaptionUncertain);
                    }
                } else {
                    self.result.limit(Limitation::CaptionUncertain);
                }
                continue;
            }
            if self.depths[i] != 0
                || (other_env_depth != 0 && !matches!(name, b"begin" | b"end"))
                || document_closed
            {
                if self.formatting_enabled && !matches!(name, b"begin" | b"end") {
                    self.formatting_safe = false;
                }
                self.result.limit(Limitation::UnsupportedContext);
                continue;
            }
            let command = Span {
                start_byte: t.start_byte,
                end_byte: t.end_byte,
            };
            let end = if self.formatting_enabled && name == b"documentclass" {
                self.formatting_class(i, command, starred, &mut class_seen, document_open)
            } else if self.formatting_enabled
                && matches!(
                    name,
                    b"onehalfspacing" | b"setstretch" | b"linespread" | b"renewcommand"
                )
            {
                self.spacing(i, command, name, starred)
            } else if name == b"setlength" {
                self.indent(i, command, starred)
            } else if name == b"geometry" && self.margins_enabled {
                self.geometry(i, command, starred)
            } else if name == b"usepackage" && self.packages_enabled {
                if document_open {
                    self.result.limit(Limitation::UnsupportedContext);
                    Some(t.end_byte as usize)
                } else {
                    self.geometry_package(i, command, starred)
                }
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
                    // Finish all byte borrowing before mutating pass state.
                    // Only finite classifications escape this scope; no copies,
                    // new lookahead or changes to argument ownership/budgets.
                    let (is_document, standard_class, environment, expected_caption_end) = {
                        let arg = trim(self.slice(a));
                        (
                            arg == b"document",
                            matches!(arg, b"article" | b"report" | b"book"),
                            match arg {
                                b"figure" => Some(super::knu_dto::CaptionEnvironment::Figure),
                                b"table" => Some(super::knu_dto::CaptionEnvironment::Table),
                                _ => None,
                            },
                            match self.caption_environment {
                                Some(super::knu_dto::CaptionEnvironment::Figure) => {
                                    arg == b"figure"
                                }
                                Some(super::knu_dto::CaptionEnvironment::Table) => arg == b"table",
                                None => arg.is_empty(),
                            },
                        )
                    };
                    match name {
                        b"documentclass" => {
                            if class_seen || !standard_class {
                                self.result.limit(Limitation::UnknownSemantics);
                            }
                            class_seen = true;
                        }
                        b"begin" => {
                            if self.formatting_enabled && !is_document {
                                self.caption_environment = if document_open
                                    && other_env_depth == 0
                                    && self.formatting_safe
                                {
                                    environment
                                } else {
                                    None
                                };
                                if self.caption_environment.is_some() {
                                    other_env_depth += 1;
                                    let end = self.float_placement(a);
                                    owned_end = end as u64;
                                    continue;
                                }
                            }
                            if !is_document {
                                if self.formatting_enabled {
                                    self.formatting_safe = false;
                                }
                                other_env_depth += 1;
                                self.result.limit(Limitation::UnknownSemantics);
                            } else if document_open {
                                self.result.limit(Limitation::UnsupportedContext);
                            } else {
                                document_open = true;
                            }
                        }
                        b"end" => {
                            if self.formatting_enabled {
                                if other_env_depth == 1 && expected_caption_end {
                                    self.caption_environment = None;
                                    other_env_depth = 0;
                                    owned_end = a.owned_end as u64;
                                    continue;
                                }
                                if self.caption_environment.is_some() {
                                    self.result.limit(Limitation::CaptionUncertain);
                                    self.result.limit(Limitation::UnsupportedContext);
                                    self.formatting_safe = false;
                                }
                                self.caption_environment = None;
                            }
                            if !is_document && other_env_depth > 0 {
                                // Still unknown semantics, but later outside-body
                                // literal evidence must not be silently lost.
                                other_env_depth -= 1;
                            } else if !is_document || !document_open {
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
                if self.formatting_enabled {
                    self.formatting_safe = false;
                }
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
        if self.packages_enabled {
            if self.package_needs_declaration && self.geometry_count != 1 {
                self.result.limit(Limitation::UnsupportedGeometry);
            }
            // New syntax must never make invalid/ambiguous package geometry a
            // route to ANY proposal, including F1. Old profiles are untouched.
            if self.result.limitations.iter().any(|r| {
                matches!(
                    r,
                    Limitation::UnsupportedGeometry
                        | Limitation::MultipleGeometryDeclarations
                        | Limitation::MissingMarginSides
                )
            }) {
                self.result.limit(Limitation::UnsupportedArgument);
            }
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
    observe_version(
        facts,
        bytes,
        eligible,
        work,
        per_file_cap,
        project_remaining,
        false,
    )
}

#[derive(Clone, Copy)]
enum GeometrySupport {
    None,
    Command,
    Package,
    Formatting,
}

pub fn observe_formatting(
    facts: &SourceFileFacts,
    bytes: &[u8],
    eligible: bool,
    work: LookaheadBudget,
    per_file_cap: u32,
    project_remaining: &mut u32,
) -> FileObservations {
    observe_features(
        facts,
        bytes,
        eligible,
        work,
        per_file_cap,
        project_remaining,
        GeometrySupport::Formatting,
    )
}

pub fn observe_geometry_package(
    facts: &SourceFileFacts,
    bytes: &[u8],
    eligible: bool,
    work: LookaheadBudget,
    per_file_cap: u32,
    project_remaining: &mut u32,
) -> FileObservations {
    observe_features(
        facts,
        bytes,
        eligible,
        work,
        per_file_cap,
        project_remaining,
        GeometrySupport::Package,
    )
}

pub fn observe_version(
    facts: &SourceFileFacts,
    bytes: &[u8],
    eligible: bool,
    work: LookaheadBudget,
    per_file_cap: u32,
    project_remaining: &mut u32,
    margins_enabled: bool,
) -> FileObservations {
    observe_features(
        facts,
        bytes,
        eligible,
        work,
        per_file_cap,
        project_remaining,
        if margins_enabled {
            GeometrySupport::Command
        } else {
            GeometrySupport::None
        },
    )
}

fn observe_features(
    facts: &SourceFileFacts,
    bytes: &[u8],
    eligible: bool,
    work: LookaheadBudget,
    per_file_cap: u32,
    project_remaining: &mut u32,
    support: GeometrySupport,
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
        margins_enabled: !matches!(support, GeometrySupport::None),
        packages_enabled: matches!(
            support,
            GeometrySupport::Package | GeometrySupport::Formatting
        ),
        formatting_enabled: matches!(support, GeometrySupport::Formatting),
        formatting_safe: true,
        setspace_imports: 0,
        caption_environment: None,
        geometry_count: 0,
        geometry_imports: 0,
        package_needs_declaration: false,
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
