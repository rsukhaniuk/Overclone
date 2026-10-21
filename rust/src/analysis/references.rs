//! Selected-root literal source intent only. No TeX expansion, IO or compilation.
//! Physical files scanned once; full known graph collected before key matching.
//!
//! Only literal article/report/book roots, document boundaries, literal headings,
//! supported root-CWD input/include and label/ref/pageref/eqref are recognized.
//! Comments/verbatim are excluded by the existing lexer. Unknown commands latch
//! unsafe context for the rest of that physical file; conditions are lexically
//! skipped, not evaluated. Optional/starred headings/classes, packages, custom
//! environments, math, moving/dynamic arguments and arbitrary macro execution
//! remain unsupported. Includes do NOT model macro/counter/environment effects.
//! Literal Unicode keys (including ordinary underscore/colon characters) compare
//! exact UTF8 bytes: no case, whitespace or normalization repair. This establishes
//! neither compiler success, actual counter/page values, nor PDF correspondence.
//! See <https://latex-project.org/help/documentation/source2e.pdf>, ltxref.dtx.
//! Default profiles, ABI, dependency pins, structural snapshot scope and caps are
//! unchanged. Only the two blanket include/reference coverage gaps are scoped.
use std::collections::{HashMap, HashSet};

use serde::{Deserialize, Deserializer, Serialize};

use super::dto::{
    AnalysisResult, AnalyzePayload, EngineDiagnostic, Location, Outcome, PerFileOutcome,
    ResolvedLimits, RuleResult, SourceFileFacts, Span, TokenKind, MAX_TOKEN_TEXT_BYTES,
};
use super::lexer::{LookaheadBudget, LookaheadResult};
use super::ownership;

pub const LABEL: &str = "ENGINE-LABEL-001";
pub const REFERENCE: &str = "ENGINE-REFERENCE-001";
const LABEL_MESSAGE: &str = "Repeated literal label key in the selected source closure.";
const REFERENCE_MESSAGE: &str =
    "No matching literal label was found in the selected source closure.";

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ReferenceConfiguration {
    pub root_file_id: String,
}

pub fn configuration<'de, D: Deserializer<'de>>(
    d: D,
) -> Result<Option<ReferenceConfiguration>, D::Error> {
    ReferenceConfiguration::deserialize(d).map(Some)
}

fn tex(path: &str) -> bool {
    path.rsplit_once('.')
        .is_some_and(|(_, ext)| ext.eq_ignore_ascii_case("tex"))
}

pub(super) fn validate(payload: &AnalyzePayload) -> Result<usize, &'static str> {
    let Some(config) = &payload.reference_configuration else {
        return Err("referenceConfiguration is required");
    };
    if config.root_file_id.is_empty() || config.root_file_id.len() > super::MAX_ID_BYTES {
        return Err("invalid rootFileId");
    }
    let mut paths = HashSet::new();
    for f in &payload.files {
        if !matches!(f.media_kind.as_str(), "text" | "binary")
            || f.relative_path.trim() != f.relative_path
            || !super::valid_project_path(&f.relative_path)
            || !paths.insert(f.relative_path.as_str())
        {
            return Err("reference paths must be unique canonical project-relative paths");
        }
    }
    let mut roots = payload
        .files
        .iter()
        .enumerate()
        .filter(|(_, f)| f.file_id == config.root_file_id);
    let Some((index, file)) = roots.next() else {
        return Err("rootFileId is not a captured file");
    };
    if roots.next().is_some() || file.media_kind != "text" || !tex(&file.relative_path) {
        return Err("rootFileId must identify one captured text .tex file");
    }
    Ok(index)
}

/// Filename policy is deliberately separate from key equality. Only literal
/// project-root-CWD input paths are normalized; no case/NFC/basename repair.
fn include_path(raw: &[u8], include: bool) -> Option<String> {
    if raw.is_empty() || raw.len() > super::MAX_PATH_BYTES {
        return None;
    }
    let path = std::str::from_utf8(raw).ok()?;
    if path.starts_with('/')
        || path.chars().any(|c| c.is_whitespace() || c.is_control())
        || path.contains(['\\', ':', '"', '\'', '{', '}', '%', '#', '$', '&', '^', '~'])
    {
        return None;
    }
    let mut segments = Vec::new();
    for part in path.split('/') {
        match part {
            "" => return None,
            "." => {}
            ".." => {
                segments.pop()?;
            }
            _ => segments.push(part),
        }
    }
    let last = *segments.last()?;
    if include && last.contains('.') {
        return None;
    }
    let mut normalized = segments.join("/");
    if !last.contains('.') {
        normalized.push_str(".tex");
    }
    (normalized.len() <= super::MAX_PATH_BYTES).then_some(normalized)
}

fn literal_key(raw: &[u8]) -> bool {
    !raw.is_empty()
        && raw.len() <= MAX_TOKEN_TEXT_BYTES
        && std::str::from_utf8(raw).is_ok_and(|s| {
            !s.chars().any(|c| c.is_whitespace() || c.is_control())
                && !raw.iter().any(|b| {
                    matches!(
                        b,
                        b'\\' | b'{' | b'}' | b'%' | b'#' | b'$' | b'&' | b'^' | b'~'
                    )
                })
        })
}

fn heading(name: &[u8]) -> bool {
    matches!(
        name,
        b"chapter" | b"section" | b"subsection" | b"subsubsection" | b"paragraph" | b"subparagraph"
    )
}

#[derive(Clone)]
struct Occurrence<'a> {
    file: usize,
    key: &'a [u8],
    span: Span,
    location: Location,
    label: bool,
}

#[derive(Default)]
struct LimitsObserved {
    codes: Vec<&'static str>,
}
impl LimitsObserved {
    fn add(&mut self, code: &'static str) {
        if !self.codes.contains(&code) {
            self.codes.push(code);
        }
    }
    fn uncertain(&self) -> bool {
        !self.codes.is_empty()
    }
    fn wire(&self) -> Vec<String> {
        ["sourceIntentOnly", "macroExpansionUnsupported"]
            .into_iter()
            .chain(self.codes.iter().copied())
            .map(str::to_string)
            .collect()
    }
}

fn reserve(remaining: &mut u32, amount: usize, observed: &mut LimitsObserved) -> bool {
    if amount > *remaining as usize {
        *remaining = 0;
        observed.add("budgetExhausted");
        false
    } else {
        *remaining -= amount as u32;
        true
    }
}

struct FilePass<'a, 'b> {
    file: usize,
    bytes: &'a [u8],
    facts: &'b SourceFileFacts,
    work: LookaheadBudget,
    remaining: u32,
    depth_cap: usize,
    root: bool,
}
impl FilePass<'_, '_> {
    fn spend(&mut self, n: usize, observed: &mut LimitsObserved) -> bool {
        if !self.work.consume(n) {
            observed.add("budgetExhausted");
            false
        } else {
            true
        }
    }
    fn unit(&mut self, project: &mut u32, observed: &mut LimitsObserved) -> bool {
        reserve(&mut self.remaining, 1, observed) && reserve(project, 1, observed)
    }
}

fn scan<'a>(
    mut pass: FilePass<'a, '_>,
    project: &mut u32,
    observed: &mut LimitsObserved,
    paths: &HashMap<&str, usize>,
    payload: &AnalyzePayload,
    occurrences: &mut Vec<Occurrence<'a>>,
) -> Vec<usize> {
    let mut edges = Vec::new();
    if pass.facts.truncated
        || pass.facts.brace_depth_exceeded
        || pass.facts.engine_outcome != Outcome::Pass
    {
        observed.add("incompleteScan");
    }
    let facts = pass.facts;
    let tokens = &facts.tokens;
    // Reserve before index allocations; same per-file/project token/emission
    // ceilings as lexer and KNU observers, with the SAME remaining byte-work.
    if !reserve(&mut pass.remaining, tokens.len(), observed)
        || !reserve(project, tokens.len(), observed)
        || !pass.spend(tokens.len(), observed)
    {
        return edges;
    }
    let (closes, depths, unmatched) = ownership::index(tokens);
    if unmatched {
        observed.add("incompleteScan");
    }
    let mut owned_end = 0;
    let mut unsafe_context = false;
    let mut conditional_depth = 0usize;
    let mut document_open = !pass.root;
    let mut document_closed = false;
    let mut class_seen = false;
    let mut cursor = super::mapping::LineCursor::default();
    for (i, token) in tokens.iter().enumerate() {
        if token.start_byte < owned_end {
            continue;
        }
        match token.kind {
            TokenKind::Comment
            | TokenKind::Verbatim
            | TokenKind::IncludePath
            | TokenKind::Label
            | TokenKind::Reference => continue,
            TokenKind::Text => {
                let b = &pass.bytes[token.start_byte as usize..token.end_byte as usize];
                if !pass.spend(b.len(), observed) {
                    break;
                }
                if b.iter()
                    .any(|b| matches!(b, b'$' | b'#' | b'&' | b'^' | b'_' | b'~'))
                {
                    observed.add("unsupportedContext");
                    unsafe_context = true;
                }
                continue;
            }
            TokenKind::Escape => {
                let b = &pass.bytes[token.start_byte as usize..token.end_byte as usize];
                if !matches!(
                    b,
                    b"\\%" | b"\\{" | b"\\}" | b"\\&" | b"\\#" | b"\\_" | b"\\$" | b"\\\\" | b"\\ "
                ) {
                    observed.add("unsupportedContext");
                    unsafe_context = true;
                }
                continue;
            }
            TokenKind::Command => {}
            _ => {
                // Any unowned group/optional/unknown region is not an argument
                // whose inner Label facts may be promoted as executed evidence.
                observed.add("unsupportedContext");
                continue;
            }
        }
        let raw = &pass.bytes[token.start_byte as usize..token.end_byte as usize];
        if !pass.spend(raw.len(), observed) {
            break;
        }
        if matches!(
            raw,
            b"\\begin{verbatim}" | b"\\begin{lstlisting}" | b"\\begin{Verbatim}"
        ) {
            continue;
        }
        let name = &raw[1..];
        if name.starts_with(b"if") {
            observed.add("unsupportedContext");
            conditional_depth += 1;
            if conditional_depth > pass.depth_cap {
                observed.add("budgetExhausted");
                break;
            }
            continue;
        }
        if name == b"fi" && conditional_depth != 0 {
            conditional_depth -= 1;
            continue;
        }
        if conditional_depth != 0 || depths[i] != 0 || unsafe_context || document_closed {
            observed.add("unsupportedContext");
            continue;
        }
        if matches!(
            name,
            b"newpage" | b"clearpage" | b"tableofcontents" | b"maketitle"
        ) {
            continue;
        }
        let known = matches!(
            name,
            b"documentclass"
                | b"begin"
                | b"end"
                | b"input"
                | b"include"
                | b"label"
                | b"ref"
                | b"eqref"
                | b"pageref"
        ) || heading(name);
        if !known {
            observed.add("unsupportedContext");
            if matches!(
                name,
                b"includeonly" | b"import" | b"subimport" | b"inputfrom" | b"subinputfrom"
            ) {
                observed.add("unsupportedIncludeSyntax");
            }
            // Unknown arity may consume unbraced following control sequences;
            // do not "recover" subsequent facts as certainly executed.
            unsafe_context = true;
            continue;
        }
        let a = match ownership::group(tokens, pass.bytes, &closes, i + 1, &mut pass.work) {
            LookaheadResult::Found(a) => a,
            LookaheadResult::NotFound => {
                observed.add(if matches!(name, b"input" | b"include") {
                    "unsupportedIncludeSyntax"
                } else {
                    "nonliteralKey"
                });
                continue;
            }
            LookaheadResult::Exhausted => {
                observed.add("budgetExhausted");
                break;
            }
        };
        owned_end = a.owned_end as u64;
        let arg = &pass.bytes[a.start..a.end];
        // Charge bounded argument work before retaining observations/allocating
        // include paths. Token/emission reservations also bound the constant
        // number of later linear hash/count passes; no prefix rescans or joins.
        let multiplier = if matches!(
            name,
            b"label" | b"ref" | b"eqref" | b"pageref" | b"input" | b"include"
        ) {
            3
        } else {
            2
        };
        if arg.len() > MAX_TOKEN_TEXT_BYTES
            || !pass.spend(arg.len().saturating_mul(multiplier), observed)
        {
            observed.add("budgetExhausted");
            break;
        }
        if name == b"documentclass" {
            if !pass.root
                || class_seen
                || document_open
                || !matches!(arg, b"article" | b"report" | b"book")
            {
                observed.add("unsupportedContext");
                unsafe_context = true;
            }
            class_seen = true;
        } else if name == b"begin" || name == b"end" {
            if !pass.root || arg != b"document" {
                observed.add("unsupportedContext");
                unsafe_context = true;
            } else if name == b"begin" && !document_open && !document_closed {
                document_open = true;
            } else if name == b"end" && document_open {
                document_open = false;
                document_closed = true;
            } else {
                observed.add("unsupportedContext");
                unsafe_context = true;
            }
        } else if heading(name) {
            if !document_open
                || arg.iter().any(|b| {
                    matches!(
                        b,
                        b'\\' | b'{' | b'}' | b'%' | b'$' | b'#' | b'&' | b'^' | b'~'
                    )
                })
            {
                observed.add("unsupportedContext");
            }
        } else if !document_open {
            observed.add("unsupportedContext");
        } else if matches!(name, b"input" | b"include") {
            if !pass.unit(project, observed) {
                break;
            }
            let Some(path) = include_path(arg, name == b"include") else {
                observed.add("unsupportedIncludeSyntax");
                continue;
            };
            let Some(&target) = paths.get(path.as_str()) else {
                observed.add("missingInclude");
                continue;
            };
            if payload.files[target].media_kind != "text"
                || !tex(&payload.files[target].relative_path)
            {
                observed.add("missingInclude");
                continue;
            }
            edges.push(target);
        } else {
            if !literal_key(arg) {
                observed.add("nonliteralKey");
                continue;
            }
            if !pass.unit(project, observed) {
                break;
            }
            // Incremental standard byte mapping: across all observations this
            // visits each preceding source byte at most once, not N prefix scans.
            if !pass.spend(a.start.saturating_sub(cursor.offset()), observed) {
                break;
            }
            let (line, column) = cursor.advance(pass.bytes, a.start);
            occurrences.push(Occurrence {
                file: pass.file,
                key: arg,
                span: Span {
                    start_byte: a.start as u64,
                    end_byte: a.end as u64,
                },
                location: Location { line, column },
                label: name == b"label",
            });
        }
    }
    if conditional_depth != 0 || (pass.root && (!class_seen || document_open || !document_closed)) {
        observed.add("unsupportedContext");
    }
    edges
}

pub(super) fn evaluate(
    result: &mut AnalysisResult,
    payload: &AnalyzePayload,
    packed: &[u8],
    validated: &[(usize, u64)],
    lookaheads: Vec<LookaheadBudget>,
    limits: ResolvedLimits,
    mut remaining: u32,
) {
    let mut diagnostics_remaining = limits
        .max_diagnostics_per_project
        .saturating_sub(result.diagnostics.len() as u32);
    let root = payload
        .files
        .iter()
        .position(|f| {
            payload
                .reference_configuration
                .as_ref()
                .is_some_and(|c| c.root_file_id == f.file_id)
        })
        .expect("source-reference root validated before lexing");
    let paths: HashMap<&str, usize> = payload
        .files
        .iter()
        .enumerate()
        .map(|(i, f)| (f.relative_path.as_str(), i))
        .collect();
    let mut work: Vec<_> = lookaheads.into_iter().map(Some).collect();
    let mut observed = LimitsObserved::default();
    let mut occurrences = Vec::new();
    let mut reached = vec![false; payload.files.len()];
    let mut colors = vec![0u8; payload.files.len()];
    let mut edges = vec![Vec::new(); payload.files.len()];
    let mut stack = vec![(root, 0usize)];
    reached[root] = true;
    while let Some((file, next)) = stack.last_mut() {
        let i = *file;
        if colors[i] == 0 {
            colors[i] = 1;
            if !reserve(&mut remaining, 1, &mut observed) {
                break;
            }
            let f = &result.facts.files[i];
            let (off, len) = validated[i];
            edges[i] = scan(
                FilePass {
                    file: i,
                    bytes: &packed[off..off + len as usize],
                    facts: f,
                    work: work[i].take().expect("each physical file observed once"),
                    remaining: limits.max_tokens_per_file.saturating_sub(f.token_count),
                    depth_cap: limits.max_brace_depth as usize,
                    root: i == root,
                },
                &mut remaining,
                &mut observed,
                &paths,
                payload,
                &mut occurrences,
            );
        }
        if *next < edges[i].len() {
            let child = edges[i][*next];
            *next += 1;
            if !reserve(&mut remaining, 1, &mut observed) {
                break;
            }
            match colors[child] {
                1 => observed.add("includeCycle"),
                2 => observed.add("repeatedInclude"),
                _ => {
                    reached[child] = true;
                    stack.push((child, 0));
                }
            }
        } else {
            colors[i] = 2;
            stack.pop();
        }
    }
    if !stack.is_empty() {
        observed.add("incompleteScan");
    }
    let mut counts = HashMap::<&[u8], usize>::new();
    for o in &occurrences {
        if o.label {
            *counts.entry(o.key).or_default() += 1;
        }
    }
    let n = payload.files.len();
    let mut label_count = vec![0usize; n];
    let mut reference_count = vec![0usize; n];
    let mut duplicate = vec![false; n];
    let mut missing = vec![false; n];
    let mut label_ids = vec![Vec::new(); n];
    let mut reference_ids = vec![Vec::new(); n];
    // Only the COMPLETE graph may authorize a missing finding. Later diagnostic
    // exhaustion can only turn outcomes indeterminate, never create success.
    let needed = occurrences
        .iter()
        .filter(|o| {
            if o.label {
                counts.get(o.key).copied().unwrap_or(0) > 1
            } else {
                !observed.uncertain() && !counts.contains_key(o.key)
            }
        })
        .count();
    if needed > diagnostics_remaining as usize || needed > remaining as usize {
        observed.add("budgetExhausted");
        result.coverage.diagnostics_truncated = true;
    }
    let allow_missing = !observed.uncertain();
    for o in &occurrences {
        let relevant = if o.label {
            label_count[o.file] += 1;
            let repeats = counts.get(o.key).copied().unwrap_or(0) > 1;
            duplicate[o.file] |= repeats;
            repeats
        } else {
            reference_count[o.file] += 1;
            let absent = !counts.contains_key(o.key);
            missing[o.file] |= absent;
            absent && allow_missing
        };
        if !relevant {
            continue;
        }
        if diagnostics_remaining == 0 || !reserve(&mut remaining, 1, &mut observed) {
            observed.add("budgetExhausted");
            result.coverage.diagnostics_truncated = true;
            continue;
        }
        let (rule, message) = if o.label {
            (LABEL, LABEL_MESSAGE)
        } else {
            (REFERENCE, REFERENCE_MESSAGE)
        };
        let file = &payload.files[o.file];
        // Bounded by opaque fileID128 + fixed rule + decimal u64 offset20 +2.
        let id = format!("{}:{}:{}", rule, file.file_id, o.span.start_byte);
        let ids = if o.label {
            &mut label_ids[o.file]
        } else {
            &mut reference_ids[o.file]
        };
        ids.push(id.clone());
        diagnostics_remaining -= 1;
        result.diagnostics.push(EngineDiagnostic {
            id,
            rule_id: rule.into(),
            origin: "engine".into(),
            severity: "warning".into(),
            file_id: file.file_id.clone(),
            span: Some(o.span.clone()),
            message: message.into(),
            primary_location: Some(o.location.clone()),
        });
    }
    let uncertain = observed.uncertain();
    for (rule, totals, failed, ids) in [
        (LABEL, label_count, duplicate, label_ids),
        (REFERENCE, reference_count, missing, reference_ids),
    ] {
        let mut per_file = Vec::new();
        let mut aggregate = Outcome::NotApplicable;
        for (i, f) in payload
            .files
            .iter()
            .enumerate()
            .filter(|(i, _)| reached[*i])
        {
            let outcome = if uncertain {
                Outcome::Indeterminate
            } else if failed[i] {
                Outcome::Fail
            } else if totals[i] != 0 {
                Outcome::Pass
            } else {
                Outcome::NotApplicable
            };
            aggregate = super::facts::roll_up(aggregate, outcome);
            per_file.push(PerFileOutcome {
                file_id: f.file_id.clone(),
                outcome,
                diagnostic_ids: ids[i].clone(),
            });
        }
        result.rule_results.push(RuleResult {
            rule_id: rule.into(),
            origin: "engine".into(),
            outcome: aggregate,
            per_file,
            limitations: observed.wire(),
            source: None,
            evidence_ids: None,
        });
    }
    result.aggregate_outcome = result
        .rule_results
        .iter()
        .fold(Outcome::NotApplicable, |a, r| {
            super::facts::roll_up(a, r.outcome)
        });
    result.reference_configuration = payload.reference_configuration.clone();
    result
        .coverage
        .unsupported
        .retain(|s| s != "include-resolution" && s != "label-reference-consistency");
    result
        .coverage
        .unsupported
        .push("dynamic-include-resolution".into());
    result
        .coverage
        .unsupported
        .push("expanded-label-reference-consistency".into());
}
