//! Pure source-observation predicates and untrusted proposal descriptors.
use super::dto::{
    AnalysisResult, AnalyzePayload, EngineDiagnostic, Outcome, PerFileOutcome, RuleResult,
};
use super::knu_dto::{FileObservations, FixDescriptor, KnuAnalysis, Limitation, ObservationValue};
use super::profiles::{self, FIX, HEADING, INDENT, VERSION};

/// Catalog priority (deliberately NOT the generic engine's aggregation).
pub fn aggregate(values: impl IntoIterator<Item = Outcome>) -> Outcome {
    fn rank(o: Outcome) -> u8 {
        match o {
            Outcome::NotApplicable => 0,
            Outcome::Pass => 1,
            Outcome::Unavailable => 2,
            Outcome::Fail => 3,
            Outcome::Indeterminate => 4,
        }
    }
    values
        .into_iter()
        .max_by_key(|o| rank(*o))
        .unwrap_or(Outcome::NotApplicable)
}

fn relevant(reason: Limitation, rule: &str) -> bool {
    !matches!(
        reason,
        Limitation::ConflictingDeclarations | Limitation::UnsupportedLength
    ) || rule == INDENT
}

fn outcome(file: &FileObservations, rule: &str) -> Outcome {
    if !file.eligible {
        return Outcome::NotApplicable;
    }
    if !file.complete || file.limitations.iter().any(|r| relevant(*r, rule)) {
        return Outcome::Indeterminate;
    }
    let mut any = false;
    let mut fail = false;
    for o in file.observations.iter().filter(|o| o.rule_id == rule) {
        any = true;
        match o.agrees {
            Some(false) => fail = true,
            None => return Outcome::Indeterminate,
            _ => {}
        }
    }
    if fail {
        Outcome::Fail
    } else if any {
        Outcome::Pass
    } else {
        Outcome::Unavailable
    }
}

/// Bounded descriptors use a supplied hash as a correlation precondition, not
/// cryptographic proof. Future review MUST recompute it over current bytes.
fn fix(payload: &AnalyzePayload, files: &[FileObservations], bytes: &[u8]) -> Vec<FixDescriptor> {
    if files
        .iter()
        .any(|f| f.eligible && (!f.complete || !f.limitations.is_empty()))
    {
        return vec![];
    }
    let mut candidates = files.iter().flat_map(|f| {
        f.observations
            .iter()
            .filter(|o| o.rule_id == INDENT)
            .map(move |o| (f, o))
    });
    let Some((file, o)) = candidates.next() else {
        return vec![];
    };
    if candidates.next().is_some() || o.agrees != Some(false) {
        return vec![];
    }
    let Some(d) = payload.files.iter().find(|d| d.file_id == file.file_id) else {
        return vec![];
    };
    if d.sha256.len() != 64 || !d.sha256.bytes().all(|b| b.is_ascii_hexdigit()) {
        return vec![];
    }
    let start = d.byte_offset as usize + o.value_span.start_byte as usize;
    let end = d.byte_offset as usize + o.value_span.end_byte as usize;
    // Slices and spans came from checked public input / owned scalar spans.
    let expected = std::str::from_utf8(&bytes[start..end]).unwrap_or_default();
    if expected.is_empty() || expected.len() > super::dto::MAX_TOKEN_TEXT_BYTES {
        return vec![];
    }
    let Some(configuration) = payload.configuration.clone() else {
        return vec![];
    };
    vec![FixDescriptor {
        id: FIX.into(), version: VERSION.into(), rule_id: INDENT.into(), rule_version: VERSION.into(),
        evidence_id: o.id.clone(), snapshot_id: payload.snapshot_id.clone(), project_id: payload.project_id.clone(),
        session_id: payload.session_id.clone(), working_generation: payload.working_generation,
        base_head_revision_id: payload.base_head_revision_id.clone(), profile: payload.profile.clone(), configuration,
        file_id: file.file_id.clone(), relative_path: d.relative_path.clone(), expected_file_sha256: d.sha256.clone(),
        span: o.value_span.clone(), expected_original: expected.into(), replacement: "1.27cm".into(),
        explanation: "Changes this explicit source indent declaration; rendered layout unverified. Review and current-byte/hash validation required; never auto-apply.".into(),
    }]
}

pub fn evaluate(
    result: &mut AnalysisResult,
    payload: &AnalyzePayload,
    mut files: Vec<FileObservations>,
    packed: &[u8],
    mut diagnostics_remaining: u32,
) {
    let includes = files
        .iter()
        .any(|f| f.limitations.contains(&Limitation::IncludeNotResolved));
    for file in &mut files {
        if !file.eligible {
            continue;
        }
        if includes {
            file.limit(Limitation::IncludeNotResolved);
        }
        for o in &file.observations {
            if o.agrees != Some(false) {
                continue;
            }
            if diagnostics_remaining == 0 {
                // Delay mutating limitations until after borrowing observations.
                result.coverage.diagnostics_truncated = true;
                continue;
            }
            diagnostics_remaining -= 1;
            let span = match &o.value {
                ObservationValue::HeadingTitle { .. } => super::dto::Span {
                    start_byte: o.value_span.end_byte - 1,
                    end_byte: o.value_span.end_byte,
                },
                _ => o.value_span.clone(),
            };
            result.diagnostics.push(EngineDiagnostic {
                id: o.id.clone(), rule_id: o.rule_id.clone(), origin: "knu".into(), severity: "warning".into(),
                file_id: file.file_id.clone(), span: Some(span), primary_location: None,
                message: if o.rule_id == INDENT {
                    "Observed literal indent differs from 12.7 mm; context may be indeterminate; rendered layout unverified"
                } else {
                    "Observed literal heading ends in U+002E; context may be indeterminate; rendered layout unverified"
                }.into(),
            });
        }
    }
    // Diagnostic incompleteness is project-wide coverage, never a false Pass.
    if result.coverage.diagnostics_truncated {
        for f in files.iter_mut().filter(|f| f.eligible) {
            f.limit(Limitation::DiagnosticBudget);
        }
    }
    let mut summary = Vec::new();
    for rule in [INDENT, HEADING] {
        let per_file: Vec<_> = files
            .iter()
            .map(|f| PerFileOutcome {
                file_id: f.file_id.clone(),
                outcome: outcome(f, rule),
                diagnostic_ids: result
                    .diagnostics
                    .iter()
                    .filter(|d| d.file_id == f.file_id && d.rule_id == rule)
                    .map(|d| d.id.clone())
                    .collect(),
            })
            .collect();
        let aggregate = aggregate(per_file.iter().map(|p| p.outcome));
        summary.push(aggregate);
        let mut limitations = vec![
            "Source observations only; effective TeX state and rendered compliance unverified"
                .into(),
        ];
        if per_file.iter().any(|p| p.outcome == Outcome::Unavailable) {
            limitations.push("no-supported-observation".into());
        }
        result.rule_results.push(RuleResult {
            rule_id: rule.into(),
            origin: "knu".into(),
            outcome: aggregate,
            per_file,
            limitations,
            source: Some(profiles::rule(rule)),
            evidence_ids: Some(
                files
                    .iter()
                    .flat_map(|f| f.observations.iter())
                    .filter(|o| o.rule_id == rule)
                    .map(|o| o.id.clone())
                    .collect(),
            ),
        });
    }
    let engine_aggregate_outcome = result.aggregate_outcome;
    result.aggregate_outcome = aggregate(summary);
    let fix_descriptors = fix(payload, &files, packed);
    result.configuration = payload.configuration.clone();
    result.knu = Some(KnuAnalysis {
        metadata: profiles::metadata(),
        engine_aggregate_outcome,
        files,
        fix_descriptors,
    });
}
