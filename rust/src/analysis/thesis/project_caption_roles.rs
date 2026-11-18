//! Selected-root caption contexts for one source-associated P16 preamble.
//! The parent pass supplies literal include traversal, environment ownership
//! and all source uncertainty. This module does not run or emulate packages.
use super::{Event, TokenKind};
use crate::analysis::dto::Token;
use serde_json::{json, Value};
use std::ops::Range;

pub(super) const VERSION: &str = "1.7.0";
pub(super) const CATALOG_VERSION: &str = "1.2.0";
const MANIFEST: &str = include_str!("caption-role-capability.json");
const METHOD: &str = "literal-project-caption-title";

#[derive(Clone, Copy)]
pub(super) enum Environment {
    Figure,
    Table,
}
impl Environment {
    pub(super) fn from_name(name: &str) -> Option<Self> {
        match name {
            "figure" => Some(Self::Figure),
            "table" => Some(Self::Table),
            _ => None,
        }
    }
    fn name(self) -> &'static str {
        match self {
            Self::Figure => "figure",
            Self::Table => "table",
        }
    }
}

pub(super) struct CaptionContext {
    root: usize,
    packages: [Range<usize>; 2],
    capability: Value,
    candidates: Vec<(usize, Environment)>,
    uncertain: bool,
}
impl CaptionContext {
    pub(super) fn recognize(
        root: usize,
        project: &crate::authoring::project::Project<'_>,
    ) -> Result<Option<Self>, &'static str> {
        let manifest: Value = serde_json::from_str(MANIFEST).map_err(|_| "InternalError")?;
        let sources = manifest["sourcePins"].as_array().ok_or("InternalError")?;
        // The fixture establishes this preamble over text-only .tex source.
        // A capture containing other inputs could change optional package
        // config discovery as well as shadow the files loaded in the witness.
        if project.files.iter().any(|file| {
            let leaf = file.relative_path.rsplit('/').next().unwrap_or_default();
            file.media_kind != "text"
                || !super::references::tex(leaf)
                || sources.iter().any(|p| {
                    p["name"]
                        .as_str()
                        .is_some_and(|name| leaf.eq_ignore_ascii_case(name))
                })
        }) {
            return Ok(None);
        }
        // TeX can translate adjacent carets before ordinary comment handling.
        // Refuse that alternate lexical input even inside a lexer comment.
        if project
            .bytes
            .iter()
            .any(|bytes| bytes.windows(2).any(|pair| pair == b"^^"))
        {
            return Ok(None);
        }
        let package_mentions = [
            ("article", "", "catalogKnownResourcesUnverified"),
            ("fontenc", "T2A", "unknownResourcesUnverified"),
            ("babel", "ukrainian", "catalogKnownResourcesUnverified"),
        ];
        if !project
            .packages
            .iter()
            .map(|p| (p.name.as_str(), p.options.as_str(), p.status))
            .eq(package_mentions)
        {
            return Ok(None);
        }
        let bytes = project.bytes[root];
        let tokens = &project.tokens[root];
        let mut next = 0;
        let mut declarations = Vec::with_capacity(4);
        for literal in [
            &b"\\documentclass{article}"[..],
            &b"\\usepackage[T2A]{fontenc}"[..],
            &b"\\usepackage[ukrainian]{babel}"[..],
            &b"\\begin{document}"[..],
        ] {
            let Some(range) = exact_declaration(bytes, tokens, &mut next, literal) else {
                return Ok(None);
            };
            declarations.push(range);
        }
        Ok(Some(Self {
            root,
            packages: [declarations[1].clone(), declarations[2].clone()],
            capability: manifest["capability"].clone(),
            candidates: vec![],
            uncertain: false,
        }))
    }

    pub(super) fn package_end(&self, file: usize, start: u64) -> Option<u64> {
        if file != self.root {
            return None;
        }
        self.packages
            .iter()
            .find(|range| range.start as u64 == start)
            .map(|range| range.end as u64)
    }

    pub(super) fn record(&mut self, event: usize, environment: Environment, count: usize) {
        self.uncertain |= count != 1;
        self.candidates.push((event, environment));
    }

    pub(super) fn refuse_scope(&mut self) {
        self.uncertain = true;
    }

    pub(super) fn add_report_fields(
        context: Option<&Self>,
        report: &mut Value,
        events: &[Event],
        complete: bool,
        root_file_id: &str,
    ) {
        let mut roles = vec![];
        let mut capability = Value::Null;
        if let Some(context) = context.filter(|c| complete && !c.uncertain) {
            for &(index, environment) in &context.candidates {
                let event = &events[index];
                // Preserve the exact byte preimage, and leave any source that
                // cannot denote one line of native text to the old observation.
                if event.text.trim().is_empty()
                    || event
                        .text
                        .chars()
                        .any(|c| c.is_control() || c == '\u{fffd}')
                {
                    continue;
                }
                roles.push(json!({
                    "eventIndex": index,
                    "fileId": event.file_id,
                    "span": event.span,
                    "environment": environment.name(),
                    "method": METHOD,
                    "contextRootFileId": root_file_id
                }));
            }
            if !roles.is_empty() {
                capability = context.capability.clone();
            }
        }
        report["captionRoles"] = json!(roles);
        report["captionRoleCapability"] = capability;
    }
}

// Each token belongs to the already bounded root stream. Consume only four
// exact source sequences, using lexer comments/whitespace as inter-declaration
// trivia. This does not recognize alternate spellings, groups or option lists.
fn exact_declaration(
    bytes: &[u8],
    tokens: &[Token],
    next: &mut usize,
    literal: &[u8],
) -> Option<Range<usize>> {
    while let Some(t) = tokens.get(*next) {
        let raw = &bytes[t.start_byte as usize..t.end_byte as usize];
        if t.kind != TokenKind::Comment
            && !(t.kind == TokenKind::Text && raw.iter().all(u8::is_ascii_whitespace))
        {
            break;
        }
        *next += 1;
    }
    let token = tokens.get(*next)?;
    let start = token.start_byte as usize;
    let end = start.checked_add(literal.len())?;
    if token.kind != TokenKind::Command
        || token.end_byte > end as u64
        || bytes.get(start..end)? != literal
    {
        return None;
    }
    while tokens.get(*next).is_some_and(|t| t.end_byte <= end as u64) {
        *next += 1;
    }
    if tokens.get(*next - 1)?.end_byte != end as u64 {
        return None;
    }
    Some(start..end)
}
