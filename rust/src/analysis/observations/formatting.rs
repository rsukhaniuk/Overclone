//! 1.4-only associations inside the existing pass and its shared budgets.
//! No source re-lexing, expansion, execution or effective-layout inference.
use super::*;
use crate::analysis::knu_dto::{PaperSize, SpacingSyntax};
use crate::analysis::profiles::{CAPTION, LINE_SPACING, PAPER};

impl Pass<'_> {
    // Only the immediate lexer-owned optional argument is admitted.
    fn optional(&mut self, i: usize, end: u64) -> Option<Argument> {
        let t = self.tokens.get(i)?;
        if t.kind != TokenKind::OptionalArg || t.start_byte != end {
            return None;
        }
        let start = t.start_byte as usize;
        let end = t.end_byte as usize;
        let mut next = i + 1;
        while next < self.tokens.len() && self.tokens[next].start_byte < end as u64 {
            if !self.spend(1) {
                return None;
            }
            next += 1;
        }
        Some(Argument {
            start: start + 1,
            end: end - 1,
            next,
            owned_end: end,
        })
    }

    pub(super) fn formatting_class(
        &mut self,
        i: usize,
        command: Span,
        starred: bool,
        seen: &mut bool,
        in_document: bool,
    ) -> Option<usize> {
        let options = self.optional(i + 1, command.end_byte);
        let class = self.group(options.map_or(i + 1, |a| a.next))?;
        if starred
            || *seen
            || in_document
            || !self.literal(class)
            || !matches!(trim(self.slice(class)), b"article" | b"report" | b"book")
        {
            self.result.limit(Limitation::UnknownSemantics);
            self.formatting_safe = false;
        }
        *seen = true;
        if let Some(a) = options {
            if self.formatting_safe
                && self.literal(a)
                && self.spend((a.end - a.start).saturating_mul(2))
            {
                let mut start = a.start;
                while start <= a.end && !self.stopped {
                    let end = self.bytes[start..a.end]
                        .iter()
                        .position(|b| *b == b',')
                        .map_or(a.end, |p| start + p);
                    let value = Argument {
                        start,
                        end,
                        next: a.next,
                        owned_end: class.owned_end,
                    };
                    if self.slice(value).contains(&b'=') {
                        self.result.limit(Limitation::PaperUncertain);
                    } else if !self.paper_option(command.clone(), value) {
                        let raw = trim(self.slice(value));
                        // These standard class options do not declare paper size.
                        if !matches!(
                            raw,
                            b"10pt"
                                | b"11pt"
                                | b"12pt"
                                | b"oneside"
                                | b"twoside"
                                | b"onecolumn"
                                | b"twocolumn"
                                | b"draft"
                                | b"final"
                                | b"titlepage"
                                | b"notitlepage"
                                | b"openright"
                                | b"openany"
                                | b"fleqn"
                                | b"leqno"
                        ) {
                            self.result.limit(Limitation::PaperUncertain);
                        }
                    }
                    if end == a.end {
                        break;
                    }
                    start = end + 1;
                }
            } else {
                self.result.limit(Limitation::PaperUncertain);
                self.result.limit(Limitation::UnsupportedArgument);
                self.formatting_safe = false;
            }
        }
        Some(class.owned_end)
    }

    /// Called once per already bounded literal option, by the existing options loop.
    pub(super) fn paper_option(&mut self, command: Span, mut a: Argument) -> bool {
        if !self.spend((a.end - a.start).saturating_mul(4)) {
            return false;
        }
        let raw = trim(self.slice(a));
        let eq = raw.iter().position(|b| *b == b'=');
        let (key, value) = eq.map_or((raw, raw), |p| (trim(&raw[..p]), trim(&raw[p + 1..])));
        let paper = match value {
            b"a4paper" | b"a4" => Some(PaperSize::A4),
            b"a3paper" | b"a3" => Some(PaperSize::A3),
            b"a5paper" | b"a5" => Some(PaperSize::A5),
            b"b5paper" | b"b5" => Some(PaperSize::B5),
            b"letterpaper" | b"letter" => Some(PaperSize::Letter),
            b"legalpaper" | b"legal" => Some(PaperSize::Legal),
            b"executivepaper" | b"executive" => Some(PaperSize::Executive),
            _ => None,
        };
        // Bare short names are geometry value syntax, not standard class flags.
        let recognized = if eq.is_some() {
            key == b"paper"
        } else {
            matches!(
                raw,
                b"a4paper"
                    | b"a3paper"
                    | b"a5paper"
                    | b"b5paper"
                    | b"letterpaper"
                    | b"legalpaper"
                    | b"executivepaper"
            )
        };
        if !recognized {
            if key.starts_with(b"paper") || key.ends_with(b"paper") {
                self.result.limit(Limitation::PaperUncertain);
            }
            return false;
        }
        // Evidence points to the literal option value (not its key).
        if let Some(p) = self.slice(a).iter().position(|b| *b == b'=') {
            a.start += p + 1;
        }
        let agrees = match paper {
            Some(PaperSize::A4) => Some(true),
            Some(PaperSize::A3) | None => None,
            _ => Some(false),
        };
        let reason = if agrees.is_none() {
            Some(Limitation::PaperUncertain)
        } else {
            None
        };
        if let Some(r) = reason {
            self.result.limit(r);
        }
        self.emit(
            PAPER,
            command,
            a,
            ObservationValue::PaperDeclaration { paper },
            agrees,
            reason,
        );
        true
    }

    pub(super) fn spacing(
        &mut self,
        i: usize,
        command: Span,
        name: &[u8],
        starred: bool,
    ) -> Option<usize> {
        let syntax = match name {
            b"onehalfspacing" => SpacingSyntax::OneHalfSpacing,
            b"setstretch" => SpacingSyntax::SetStretch,
            b"linespread" => SpacingSyntax::LineSpread,
            _ => SpacingSyntax::BaselineStretch,
        };
        let mut next = i + 1;
        if syntax == SpacingSyntax::BaselineStretch {
            let Some(target) = self.group(next) else {
                self.formatting_safe = false;
                self.result.limit(Limitation::LineSpacingUncertain);
                return None;
            };
            if !self.bounded_argument(target) || trim(self.slice(target)) != b"\\baselinestretch" {
                self.formatting_safe = false;
                self.result.limit(Limitation::UnknownSemantics);
                return Some(target.owned_end);
            }
            next = target.next;
        }
        let a = if syntax == SpacingSyntax::OneHalfSpacing {
            Argument {
                start: command.start_byte as usize,
                end: command.end_byte as usize,
                next,
                owned_end: command.end_byte as usize,
            }
        } else {
            let Some(a) = self.group(next) else {
                self.formatting_safe = false;
                self.result.limit(Limitation::LineSpacingUncertain);
                return None;
            };
            a
        };
        let literal = syntax == SpacingSyntax::OneHalfSpacing || self.literal(a);
        if !literal || starred {
            self.result.limit(Limitation::UnsupportedArgument);
            self.formatting_safe = false;
        }
        let requires_setspace = matches!(
            syntax,
            SpacingSyntax::OneHalfSpacing | SpacingSyntax::SetStretch
        );
        let value = if !self.formatting_safe || requires_setspace && self.setspace_imports != 1 {
            None
        } else if syntax == SpacingSyntax::OneHalfSpacing {
            Some((3, 2))
        } else if self.spend(a.end - a.start) {
            scalar(trim(self.slice(a)))
        } else {
            None
        };
        let agrees = value.map(|(n, d)| n.checked_mul(2) == d.checked_mul(3));
        let reason = if value.is_none() {
            Some(Limitation::LineSpacingUncertain)
        } else {
            None
        };
        if let Some(r) = reason {
            self.result.limit(r);
        }
        self.emit(
            LINE_SPACING,
            command,
            a,
            ObservationValue::LineSpacingDeclaration {
                syntax,
                numerator: value.map(|v| v.0),
                denominator: value.map(|v| v.1),
            },
            agrees,
            reason,
        );
        Some(a.owned_end)
    }

    pub(super) fn caption(&mut self, i: usize, command: Span, starred: bool) -> Option<usize> {
        let short = self.optional(i + 1, command.end_byte);
        let title = self.group(short.map_or(i + 1, |a| a.next))?;
        let valid = !starred && short.is_none() && self.literal(title);
        let last = if valid {
            std::str::from_utf8(trim(self.slice(title)))
                .ok()
                .and_then(|s| s.chars().next_back())
        } else {
            None
        };
        let reason = if last.is_none() {
            Some(Limitation::CaptionUncertain)
        } else {
            None
        };
        if let Some(r) = reason {
            self.result.limit(r);
            self.result.limit(Limitation::UnsupportedArgument);
            self.formatting_safe = false;
        }
        if let Some(environment) = self.caption_environment {
            self.emit(
                CAPTION,
                command,
                title,
                ObservationValue::CaptionTitle {
                    environment,
                    final_scalar: last,
                },
                last.map(|c| c != '.'),
                reason,
            );
        }
        Some(title.owned_end)
    }

    pub(super) fn float_placement(&mut self, a: Argument) -> usize {
        // A placement argument after {figure} is NOT lexer-owned optional metadata.
        // Do not guess bracket ownership. Such floats remain indeterminate.
        for i in a.next..self.tokens.len() {
            let token = &self.tokens[i];
            let kind = token.kind;
            let start = token.start_byte as usize;
            let end = token.end_byte as usize;
            if !matches!(kind, TokenKind::Comment | TokenKind::Text) {
                break;
            }
            if !self.spend(end - start) {
                self.caption_environment = None;
                break;
            }
            if kind == TokenKind::Comment {
                continue;
            }
            let raw = trim(&self.bytes[start..end]);
            if raw.is_empty() {
                continue;
            }
            if raw.first() == Some(&b'[') {
                self.caption_environment = None;
                self.result.limit(Limitation::CaptionUncertain);
            }
            break;
        }
        a.owned_end
    }
}

/// Exact scalar grammar, checked and bounded by literal() before this call.
fn scalar(raw: &[u8]) -> Option<(u64, u64)> {
    let mut numerator = 0u64;
    let mut digits = 0;
    let mut fraction = None;
    for &b in raw {
        if b == b'.' && fraction.is_none() && digits > 0 {
            fraction = Some(0u32);
            continue;
        }
        if !b.is_ascii_digit() {
            return None;
        }
        digits += 1;
        if digits > 9 {
            return None;
        }
        if let Some(n) = &mut fraction {
            *n += 1;
            if *n > 3 {
                return None;
            }
        }
        numerator = numerator.checked_mul(10)?.checked_add((b - b'0') as u64)?;
    }
    if digits == 0 || fraction == Some(0) {
        return None;
    }
    Some((numerator, 10u64.checked_pow(fraction.unwrap_or(0))?))
}
