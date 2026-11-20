//! Pure source-observation predicates and untrusted proposal descriptors.
use super::dto::{
    AnalysisResult, AnalyzePayload, EngineDiagnostic, Outcome, PerFileOutcome, RuleResult,
};
use super::knu_dto::{FileObservations, FixDescriptor, KnuAnalysis, Limitation, ObservationValue};
use super::profiles::{self, CAPTION, FIX, HEADING, INDENT, LINE_SPACING, MARGINS, PAPER, VERSION};

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
    match reason {
        Limitation::PaperUncertain => rule == PAPER,
        Limitation::LineSpacingUncertain => rule == LINE_SPACING,
        Limitation::CaptionUncertain => rule == CAPTION,
        Limitation::ConflictingDeclarations | Limitation::UnsupportedLength => rule == INDENT,
        Limitation::UnsupportedGeometry
        | Limitation::MissingMarginSides
        | Limitation::MultipleGeometryDeclarations => rule == MARGINS,
        _ => true,
    }
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
    if rule == MARGINS {
        let sides = file
            .observations
            .iter()
            .filter_map(|o| match o.value {
                ObservationValue::MarginDeclaration { side, .. } => Some(side.bit()),
                _ => None,
            })
            .fold(0, |all, side| all | side);
        if sides != 15 {
            return Outcome::Indeterminate;
        }
    }
    if fail {
        Outcome::Fail
    } else if any {
        Outcome::Pass
    } else if matches!(rule, PAPER | LINE_SPACING) {
        Outcome::Indeterminate
    } else {
        Outcome::Unavailable
    }
}

/// Bounded descriptors use a supplied hash as a correlation precondition, not
/// cryptographic proof. Future review MUST recompute it over current bytes.
fn indent_fix(
    payload: &AnalyzePayload,
    files: &[FileObservations],
    bytes: &[u8],
) -> Vec<FixDescriptor> {
    if files
        .iter()
        .any(|f| f.eligible && (!f.complete || f.limitations.iter().any(|r| relevant(*r, INDENT))))
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

fn fix(
    payload: &AnalyzePayload,
    files: &[FileObservations],
    bytes: &[u8],
    project_scope: bool,
) -> Vec<FixDescriptor> {
    if payload.profile.version == super::project_margins::VERSION && !project_scope {
        return vec![];
    }
    // Broader declaration admission is not correlated fix admission.
    if payload.profile.version == profiles::FORMATTING_VERSION {
        return vec![];
    }
    let indent = indent_fix(payload, files, bytes);
    if !indent.is_empty()
        || !matches!(
            payload.profile.version.as_str(),
            profiles::MARGIN_FIX_VERSION
                | profiles::GEOMETRY_PACKAGE_VERSION
                | super::project_margins::VERSION
        )
    {
        return indent;
    }
    // No new parser/predicate: only complete, unambiguous owned observations.
    // Unlike F1's historical margin-only exemption, margin fixes refuse ANY
    // eligible-file uncertainty, including unrelated unsupported source context.
    if files
        .iter()
        .any(|f| f.eligible && (!f.complete || !f.limitations.is_empty()))
    {
        return vec![];
    }
    let margins: Vec<_> = files
        .iter()
        .flat_map(|f| {
            f.observations
                .iter()
                .filter(|o| o.rule_id == MARGINS)
                .map(move |o| (f, o))
        })
        .collect();
    if margins.len() != 4
        || project_scope
            && margins
                .iter()
                .filter(|(_, o)| o.agrees == Some(false))
                .count()
                != 1
        || margins
            .iter()
            .any(|(_, o)| o.reason.is_some() || o.agrees.is_none())
    {
        return vec![];
    }
    let (first_file, first) = margins[0];
    let mut sides = 0;
    for (file, o) in &margins {
        if file.file_id != first_file.file_id
            || o.command_span.start_byte != first.command_span.start_byte
            || o.command_span.end_byte != first.command_span.end_byte
        {
            return vec![];
        }
        let ObservationValue::MarginDeclaration {
            side,
            length: Some(_),
        } = &o.value
        else {
            return vec![];
        };
        if sides & side.bit() != 0 {
            return vec![];
        }
        sides |= side.bit();
    }
    if sides != 15 {
        return vec![];
    }
    for (index, side) in [
        super::knu_dto::MarginSide::Left,
        super::knu_dto::MarginSide::Right,
        super::knu_dto::MarginSide::Top,
        super::knu_dto::MarginSide::Bottom,
    ]
    .into_iter()
    .enumerate()
    {
        let Some((file, o)) = margins.iter().find(|(_, o)| {
            matches!(o.value,
            ObservationValue::MarginDeclaration { side: actual, length: Some(_) } if actual == side)
                && o.agrees == Some(false)
        }) else {
            continue;
        };
        let Some(d) = payload.files.iter().find(|d| d.file_id == file.file_id) else {
            return vec![];
        };
        if d.sha256.len() != 64 || !d.sha256.bytes().all(|b| b.is_ascii_hexdigit()) {
            return vec![];
        }
        let start = d.byte_offset as usize + o.value_span.start_byte as usize;
        let end = d.byte_offset as usize + o.value_span.end_byte as usize;
        let expected = std::str::from_utf8(&bytes[start..end]).unwrap_or_default();
        if expected.is_empty() || expected.len() > super::dto::MAX_TOKEN_TEXT_BYTES {
            return vec![];
        }
        let Some(configuration) = payload.configuration.clone() else {
            return vec![];
        };
        let (id, replacement) = profiles::MARGIN_FIXES[index];
        return vec![FixDescriptor {
            id: id.into(), version: VERSION.into(), rule_id: MARGINS.into(), rule_version: profiles::rule_for(MARGINS, &payload.profile.version).version,
            evidence_id: o.id.clone(), snapshot_id: payload.snapshot_id.clone(), project_id: payload.project_id.clone(),
            session_id: payload.session_id.clone(), working_generation: payload.working_generation,
            base_head_revision_id: payload.base_head_revision_id.clone(), profile: payload.profile.clone(), configuration,
            file_id: file.file_id.clone(), relative_path: d.relative_path.clone(), expected_file_sha256: d.sha256.clone(),
            span: o.value_span.clone(), expected_original: expected.into(), replacement: replacement.into(),
            explanation: "Changes only this explicit literal margin to the section 3.1 minimum; effective geometry and rendered compliance unverified. Preview and explicit acceptance required; never auto-apply.".into(),
        }];
    }
    vec![]
}

pub fn evaluate(
    result: &mut AnalysisResult,
    payload: &AnalyzePayload,
    mut files: Vec<FileObservations>,
    packed: &[u8],
    mut diagnostics_remaining: u32,
) {
    let latest = payload.profile.version == profiles::LATEST_VERSION
        || payload.profile.version == profiles::MARGIN_FIX_VERSION
        || payload.profile.version == profiles::GEOMETRY_PACKAGE_VERSION
        || payload.profile.version == profiles::FORMATTING_VERSION
        || payload.profile.version == super::project_margins::VERSION;
    let formatting = payload.profile.version == profiles::FORMATTING_VERSION;
    let paper_count = files
        .iter()
        .flat_map(|f| &f.observations)
        .filter(|o| o.rule_id == PAPER)
        .count();
    let spacing_count = files
        .iter()
        .flat_map(|f| &f.observations)
        .filter(|o| o.rule_id == LINE_SPACING)
        .count();
    // No cross-file precedence inference. Count distinct command identities in
    // bounded stored evidence; unsupported/unobserved groups already carry
    // explicit uncertainty, so absence cannot become a margin Pass.
    let mut declarations = std::collections::BTreeSet::new();
    if latest {
        for file in &files {
            for o in &file.observations {
                if o.rule_id == MARGINS {
                    declarations.insert((&file.file_id, o.command_span.start_byte));
                }
            }
        }
    }
    let multiple_geometry = declarations.len() > 1;
    let includes = files
        .iter()
        .any(|f| f.limitations.contains(&Limitation::IncludeNotResolved));
    let scoped_child = result
        .project_margin_context
        .as_ref()
        .map(|scope| scope.included_file_id.as_str());
    for file in &mut files {
        if !file.eligible {
            continue;
        }
        if includes {
            file.limit(Limitation::IncludeNotResolved);
        }
        if formatting {
            if paper_count > 1 {
                file.limit(Limitation::PaperUncertain);
            }
            if spacing_count > 1 {
                file.limit(Limitation::LineSpacingUncertain);
            }
        }
        if latest {
            if multiple_geometry {
                file.limit(Limitation::MultipleGeometryDeclarations);
                if matches!(
                    payload.profile.version.as_str(),
                    profiles::GEOMETRY_PACKAGE_VERSION | super::project_margins::VERSION
                ) {
                    file.limit(Limitation::UnsupportedArgument);
                }
            }
            let sides = file
                .observations
                .iter()
                .filter_map(|o| match o.value {
                    ObservationValue::MarginDeclaration { side, .. } => Some(side.bit()),
                    _ => None,
                })
                .fold(0, |all, side| all | side);
            if sides != 15 && scoped_child != Some(file.file_id.as_str()) {
                file.limit(Limitation::MissingMarginSides);
            }
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
                ObservationValue::HeadingTitle { .. } | ObservationValue::CaptionTitle { .. } => {
                    super::dto::Span {
                        start_byte: o.value_span.end_byte - 1,
                        end_byte: o.value_span.end_byte,
                    }
                }
                _ => o.value_span.clone(),
            };
            result.diagnostics.push(EngineDiagnostic {
                id: o.id.clone(), rule_id: o.rule_id.clone(), origin: "knu".into(), severity: "warning".into(),
                file_id: file.file_id.clone(), span: Some(span), primary_location: None,
                message: if o.rule_id == INDENT {
                    "Observed literal indent differs from 12.7 mm; context may be indeterminate; rendered layout unverified"
                } else if o.rule_id == PAPER {
                    "Observed literal paper intent is not A4; A3 illustration purpose and physical paper size are unverified"
                } else if o.rule_id == LINE_SPACING {
                    "Declared literal spacing factor is not 1.5; effective baseline/glyph geometry is unverified"
                } else if o.rule_id == CAPTION {
                    "Observed literal figure/table caption title ends in U+002E; numbering, placement and rendered layout are unverified"
                } else if o.rule_id == MARGINS {
                    "Observed literal margin is below the section 3.1 minimum; context may be indeterminate; rendered geometry unverified"
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
    let rule_ids: &[&str] = if formatting {
        &[INDENT, HEADING, MARGINS, PAPER, LINE_SPACING, CAPTION]
    } else if latest {
        &[INDENT, HEADING, MARGINS]
    } else {
        &[INDENT, HEADING]
    };
    for &rule in rule_ids {
        let per_file: Vec<_> = files
            .iter()
            .map(|f| PerFileOutcome {
                file_id: f.file_id.clone(),
                outcome: if rule == MARGINS && scoped_child == Some(f.file_id.as_str()) {
                    Outcome::Unavailable // The declaration was scoped to the selected root.
                } else {
                    outcome(f, rule)
                },
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
            source: Some(profiles::rule_for(rule, &payload.profile.version)),
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
    let mut fix_descriptors = fix(
        payload,
        &files,
        packed,
        result.project_margin_context.is_some(),
    );
    if !result.coverage.truncated_files.is_empty()
        && fix_descriptors
            .first()
            .is_some_and(|f| f.rule_id == MARGINS)
    {
        fix_descriptors.clear();
    }
    result.configuration = payload.configuration.clone();
    result.knu = Some(KnuAnalysis {
        metadata: profiles::metadata_for(&payload.profile.version),
        engine_aggregate_outcome,
        files,
        fix_descriptors,
    });
}
