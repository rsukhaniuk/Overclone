//! Shared bounded token ownership, not another lexer or TeX expander.
//! Group association deliberately preserves the existing KNU trivia/fact rules.
use super::dto::{Token, TokenKind};
use super::lexer::{LookaheadBudget, LookaheadResult};

#[derive(Clone, Copy)]
pub(super) struct Argument {
    pub start: usize,
    pub end: usize,
    pub next: usize,
    pub owned_end: usize,
}

pub(super) fn group(
    tokens: &[Token],
    bytes: &[u8],
    closes: &[Option<usize>],
    mut i: usize,
    work: &mut LookaheadBudget,
) -> LookaheadResult<Argument> {
    while let Some(t) = tokens.get(i) {
        match t.kind {
            TokenKind::Comment => {
                if !work.consume((t.end_byte - t.start_byte) as usize) {
                    return LookaheadResult::Exhausted;
                }
                i += 1;
            }
            TokenKind::Text => {
                let b = &bytes[t.start_byte as usize..t.end_byte as usize];
                if !work.consume(b.len()) {
                    return LookaheadResult::Exhausted;
                }
                if !b.iter().all(u8::is_ascii_whitespace) {
                    return LookaheadResult::NotFound;
                }
                i += 1;
            }
            TokenKind::IncludePath | TokenKind::Label | TokenKind::Reference => i += 1,
            TokenKind::BraceOpen => {
                let Some(close) = closes[i] else {
                    return LookaheadResult::NotFound;
                };
                let end = tokens[close].start_byte as usize;
                return LookaheadResult::Found(Argument {
                    start: t.end_byte as usize,
                    end,
                    next: close + 1,
                    owned_end: end + 1,
                });
            }
            _ => return LookaheadResult::NotFound,
        }
    }
    LookaheadResult::NotFound
}

/// One linear brace pairing over already bounded tokens. Callers reserve token
/// work BEFORE allocating. Unowned braces remain unsafe semantic scope.
pub(crate) fn index(tokens: &[Token]) -> (Vec<Option<usize>>, Vec<usize>, bool) {
    let mut closes = vec![None; tokens.len()];
    let mut depths = vec![0; tokens.len()];
    let mut stack = Vec::new();
    let mut unmatched = false;
    for (i, t) in tokens.iter().enumerate() {
        depths[i] = stack.len();
        match t.kind {
            TokenKind::BraceOpen => stack.push(i),
            TokenKind::BraceClose => {
                if let Some(open) = stack.pop() {
                    closes[open] = Some(i);
                } else {
                    unmatched = true;
                }
            }
            _ => {}
        }
    }
    (closes, depths, unmatched || !stack.is_empty())
}
