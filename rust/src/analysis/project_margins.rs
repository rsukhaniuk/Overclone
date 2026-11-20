//! A proof for one literal body input and one ordinary root preamble. Existing
//! geometry analysis owns numeric observations, spans and fix construction.
use super::{
    dto::{AnalyzePayload, Outcome, SourceFileFacts, Span, Token, TokenKind},
    knu_dto::FileObservations,
    lexer::{LookaheadBudget, LookaheadResult},
    ownership, profiles, references,
};
use serde::{Deserialize, Serialize};

pub(super) const VERSION: &str = "1.8.0";
pub(super) const LIMITATION: &str = "Selected-root repair requires exactly two text .tex files, an ordinary article and literal geometry package options, and one direct body input of ordinary literal text; all other include or macro contexts remain unverified";

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Context {
    pub root_file_id: String,
    pub included_file_id: String,
    pub input_span: Span,
}

impl Context {
    pub(super) fn complete(&self, files: &[FileObservations]) -> bool {
        let [a, b] = files else { return false };
        if files
            .iter()
            .any(|f| !f.eligible || !f.complete || !f.limitations.is_empty())
        {
            return false;
        }
        let (root, included) = if a.file_id == self.root_file_id {
            (a, b)
        } else {
            (b, a)
        };
        included.file_id == self.included_file_id
            && included.observations.is_empty()
            && root.observations.len() == 4
            && root
                .observations
                .iter()
                .all(|o| o.rule_id == profiles::MARGINS && o.reason.is_none() && o.agrees.is_some())
    }
}

const MAX_CLOSURE_FILE_BYTES: usize = 65536;

fn commands(facts: &SourceFileFacts) -> impl Iterator<Item = (usize, &Token)> {
    facts
        .tokens
        .iter()
        .enumerate()
        .filter(|(_, token)| token.kind == TokenKind::Command)
}

fn start(token: &Token) -> usize {
    token.start_byte as usize
}

fn exact(bytes: &[u8], token: &Token, literal: &[u8]) -> Option<usize> {
    let end = start(token) + literal.len();
    (bytes.get(start(token)..end)? == literal).then_some(end)
}

fn literal_body(bytes: &[u8]) -> bool {
    // ASCII printable TeX text under the ordinary class catcodes with the
    // active/special characters excluded. No comments or pre-lex ^^ forms.
    bytes.iter().all(|b| {
        (b.is_ascii_graphic() || matches!(b, b' ' | b'\t' | b'\r' | b'\n'))
            && !matches!(
                b,
                b'\\' | b'{' | b'}' | b'%' | b'$' | b'#' | b'&' | b'^' | b'_' | b'~'
            )
    })
}

pub(super) fn recognize(
    payload: &AnalyzePayload,
    root: usize,
    packed: &[u8],
    slices: &[(usize, u64)],
    facts: &[SourceFileFacts],
    lookahead: &mut [LookaheadBudget],
) -> Option<Context> {
    let [a, b] = payload.files.as_slice() else {
        return None;
    };
    if a.relative_path.eq_ignore_ascii_case(&b.relative_path)
        || payload.files.iter().zip(facts).any(|(file, f)| {
            file.media_kind != "text"
                || file.sha256.len() != 64
                || !file.sha256.bytes().all(|b| b.is_ascii_hexdigit())
                || !references::tex(&file.relative_path)
                || file.has_bom
                || f.truncated
                || f.brace_depth_exceeded
                || f.engine_outcome != Outcome::Pass
        })
    {
        return None;
    }
    let [(offset1, size1), (offset2, size2)] = *slices else {
        return None;
    };
    if offset1 != 0 || offset2 as u64 != size1 || size1.checked_add(size2)? != packed.len() as u64 {
        return None;
    }
    let child = 1 - root;
    let file_bytes = |index: usize| {
        let (offset, len) = slices[index];
        (len <= MAX_CLOSURE_FILE_BYTES as u64).then(|| &packed[offset..offset + len as usize])
    };
    let bytes = file_bytes(root)?;
    let body = file_bytes(child)?;
    if !lookahead[root].consume(bytes.len() + facts[root].tokens.len())
        || !lookahead[child].consume(body.len())
        || !literal_body(body)
        || body.iter().all(u8::is_ascii_whitespace)
        || !body.ends_with(b"\n")
    {
        return None;
    }

    let tokens = &facts[root].tokens;
    let mut commands = commands(&facts[root]);
    let (_, class) = commands.next()?;
    let (package_index, package) = commands.next()?;
    let (_, begin) = commands.next()?;
    let (input_index, input) = commands.next()?;
    let (_, end) = commands.next()?;
    if commands.next().is_some() {
        return None;
    }
    let class_end = exact(bytes, class, b"\\documentclass{article}")?;
    let package_start = exact(bytes, package, b"\\usepackage")?;
    let options = tokens.get(package_index + 1)?;
    if options.kind != TokenKind::OptionalArg || start(options) != package_start {
        return None;
    }
    let options_end = options.end_byte as usize;
    let option_bytes = bytes.get(package_start + 1..options_end.checked_sub(1)?)?;
    if !option_bytes.iter().all(|b| {
        b.is_ascii_alphanumeric() || matches!(b, b'.' | b',' | b'=' | b' ' | b'\t' | b'\r' | b'\n')
    }) {
        return None;
    }
    // The existing geometry observer still requires all four supported,
    // distinct options and owns the exact values. Here only preamble shape
    // and absence of arbitrary control sequences are established.
    let package_end = options_end + b"{geometry}".len();
    if bytes.get(options_end..package_end)? != b"{geometry}" {
        return None;
    }
    let document_start = exact(bytes, begin, b"\\begin{document}")?;
    let document_end = exact(bytes, end, b"\\end{document}")?;
    let (closes, depths, unmatched) = ownership::index(tokens);
    if unmatched
        || depths[input_index] != 0
        || &bytes[start(input)..input.end_byte as usize] != b"\\input"
    {
        return None;
    }
    let LookaheadResult::Found(argument) = ownership::group(
        tokens,
        bytes,
        &closes,
        input_index + 1,
        &mut lookahead[root],
    ) else {
        return None;
    };
    let raw_path = bytes.get(argument.start..argument.end)?;
    let child_path = &payload.files[child].relative_path;
    if argument.start != input.end_byte as usize + 1
        || raw_path != child_path.as_bytes()
        || !raw_path
            .iter()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'/' | b'-' | b'_' | b'.'))
        || references::include_path(raw_path, false).as_deref() != Some(child_path)
        || !child_path.ends_with(".tex")
    {
        return None;
    }
    for gap in [
        bytes.get(..start(class))?,
        bytes.get(class_end..start(package))?,
        bytes.get(package_end..start(begin))?,
        bytes.get(document_end..)?,
    ] {
        if !gap
            .iter()
            .all(|b| matches!(b, b' ' | b'\t' | b'\r' | b'\n'))
        {
            return None;
        }
    }
    if !literal_body(bytes.get(document_start..start(input))?)
        || !literal_body(bytes.get(argument.owned_end..start(end))?)
    {
        return None;
    }
    Some(Context {
        root_file_id: payload.files[root].file_id.clone(),
        included_file_id: payload.files[child].file_id.clone(),
        input_span: Span {
            start_byte: input.start_byte,
            end_byte: argument.owned_end as u64,
        },
    })
}

#[cfg(test)]
mod tests;
