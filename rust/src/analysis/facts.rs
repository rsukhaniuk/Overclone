//! Outcome aggregation: turns per-file `ScanResult`s into the typed
//! `AnalysisResult` (one engine `RuleResult` for `ENGINE-STRUCT-001`,
//! per-file outcomes, and the aggregate outcome).

use crate::analysis::dto::{
    Coverage, EngineDiagnostic, Outcome, PerFileOutcome, RuleResult, SourceFileFacts,
};
use crate::analysis::lexer::ScanResult;

/// Build one `SourceFileFacts` from a scan, computing the per-file engine
/// outcome (lexical subset only — NOT TeX/KNU compliance):
///
/// - `NotApplicable` for a non-text descriptor (no scan).
/// - `Indeterminate` if the scan was truncated, brace-depth-exceeded, dropped a
///   diagnostic, had a too-long include/label/ref argument, or exhausted the
///   per-file lookahead byte-work budget.
/// - `Fail` if fully scanned with at least one unmatched opening brace.
/// - `Pass` if fully scanned with no unmatched openings.
///
/// `lookahead_exhausted` is reported as truncation on the file facts (it stops
/// the scan early without guessing), and surfaced as an explicit limitation
/// on the engine `RuleResult`.
pub fn file_facts(
    file_id: &str,
    byte_length: u64,
    has_bom: bool,
    scan: ScanResult,
    media_kind: &str,
) -> SourceFileFacts {
    let truncated = scan.truncated || scan.lookahead_exhausted;
    let outcome = if media_kind != "text" {
        Outcome::NotApplicable
    } else if truncated
        || scan.brace_depth_exceeded
        || scan.diagnostic_dropped
        || scan.argument_too_long
        || scan.verbatim_unterminated
        || scan.optional_unterminated
        || scan.nonliteral_argument
    {
        Outcome::Indeterminate
    } else if scan.had_unmatched_open {
        Outcome::Fail
    } else {
        Outcome::Pass
    };

    SourceFileFacts {
        file_id: file_id.to_string(),
        byte_length,
        has_bom,
        newline_kind: scan.newline_kind,
        truncated,
        brace_depth_exceeded: scan.brace_depth_exceeded,
        token_count: scan.tokens.len() as u32,
        engine_outcome: outcome,
        tokens: scan.tokens,
    }
}

/// Build the single engine `RuleResult` and the aggregate outcome from the
/// per-file facts. Exactly one `RuleResult` for `ENGINE-STRUCT-001`.
/// `diagnostics` is the ACTUAL stored top-level diagnostic list; per-file
/// `diagnosticIds` are built from those stored diagnostics (file_id + rule
/// match), never fabricated. `limitations` carries explicit engine
/// limitation notes (e.g. a per-file lookahead byte-work budget exhaustion).
pub fn rule_result_and_aggregate(
    files: &[SourceFileFacts],
    diagnostics: &[EngineDiagnostic],
    limitations: &[String],
) -> (RuleResult, Outcome) {
    let mut per_file = Vec::with_capacity(files.len());
    let mut aggregate = Outcome::NotApplicable;
    let mut any_eligible = false;

    for f in files {
        // Skip NotApplicable files (binary) from the per-file list and the
        // aggregate roll-up; they are not "eligible" for this lexical check.
        if f.engine_outcome == Outcome::NotApplicable {
            continue;
        }
        any_eligible = true;
        // Collect the ACTUAL stored diagnostic IDs for this file/rule.
        let diagnostic_ids: Vec<String> = diagnostics
            .iter()
            .filter(|d| d.file_id == f.file_id && d.rule_id == "ENGINE-STRUCT-001")
            .map(|d| d.id.clone())
            .collect();
        per_file.push(PerFileOutcome {
            file_id: f.file_id.clone(),
            outcome: f.engine_outcome,
            diagnostic_ids,
        });
        aggregate = roll_up(aggregate, f.engine_outcome);
    }

    if !any_eligible {
        aggregate = Outcome::NotApplicable;
    }

    let rule = RuleResult {
        rule_id: "ENGINE-STRUCT-001".to_string(),
        origin: "engine".to_string(),
        outcome: aggregate,
        per_file,
        limitations: limitations.to_vec(),
        source: None,
        evidence_ids: None,
    };
    (rule, aggregate)
}

/// Aggregate rule: `Indeterminate` if any eligible file is `Indeterminate` or
/// `Unavailable`; else `Fail` if any `Fail`; else `Pass` if any eligible file;
/// else `NotApplicable`. `NotApplicable` file outcomes are filtered out
/// before roll-up, so they never appear as `file_outcome` here.
fn roll_up(aggregate: Outcome, file_outcome: Outcome) -> Outcome {
    use Outcome::*;
    match file_outcome {
        Indeterminate | Unavailable => Indeterminate,
        NotApplicable => aggregate, // filtered out before roll-up; no-op
        Pass | Fail => match aggregate {
            Indeterminate | Unavailable => Indeterminate,
            NotApplicable => file_outcome, // seed from the first eligible file
            Pass => match file_outcome {
                Fail => Fail,
                _ => Pass,
            },
            Fail => Fail,
        },
    }
}

/// Build the coverage summary. `factKinds` lists the token kinds actually
/// produced; `unsupported` lists the deferred capabilities.
pub fn coverage(files: &[SourceFileFacts], diagnostics_truncated: bool) -> Coverage {
    let mut fact_kinds: Vec<String> = Vec::new();
    let mut unsupported: Vec<String> = Vec::new();
    let mut truncated_files: Vec<String> = Vec::new();

    for f in files {
        if f.engine_outcome == Outcome::NotApplicable {
            // binary/non-source: metadata-only unsupported entry
            if !unsupported.iter().any(|s| s == "binary-file") {
                unsupported.push("binary-file".to_string());
            }
            continue;
        }
        if f.truncated || f.brace_depth_exceeded {
            truncated_files.push(f.file_id.clone());
        }
        for t in &f.tokens {
            let k = token_kind_str(t.kind);
            if !fact_kinds.iter().any(|s| s == k) {
                fact_kinds.push(k.to_string());
            }
        }
    }
    for u in [
        "macro-expansion",
        "math-mode",
        "bibliography",
        "environment-bodies",
        "include-resolution",
        "label-reference-consistency",
        "pdf-facts",
        "pdf-checks",
    ] {
        unsupported.push(u.to_string());
    }

    Coverage {
        fact_kinds,
        unsupported,
        truncated_files,
        diagnostics_truncated,
        result_truncated: false,
    }
}

fn token_kind_str(k: crate::analysis::dto::TokenKind) -> &'static str {
    use crate::analysis::dto::TokenKind::*;
    match k {
        Command => "command",
        BraceOpen => "braceOpen",
        BraceClose => "braceClose",
        Group => "group",
        OptionalArg => "optionalArg",
        Text => "text",
        Comment => "comment",
        Escape => "escape",
        IncludePath => "includePath",
        Label => "label",
        Reference => "reference",
        Verbatim => "verbatim",
        Unknown => "unknown",
    }
}
