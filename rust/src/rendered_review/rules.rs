use super::{catalog, classify, dto::*, heading, pagination, paragraph, roles, typography};
use serde_json::{json, Value};

fn metrics(
    request: &Request,
    page: &Page,
    paragraph: bool,
) -> (&'static str, &'static str, &'static str, Vec<Value>) {
    let none = |support, outcome, reason| (support, outcome, reason, Vec::new());
    if page.capabilities.glyph_ink != "available" {
        return none("unsupported", "unavailable", "glyph-ink-unavailable");
    }
    if page.capabilities.schema_version != 2 {
        return none(
            "unsupported",
            "unavailable",
            "glyph-ink-uncertainty-unavailable",
        );
    }
    let f = &page.facts;
    let Some(unit) = f
        .user_unit
        .value
        .filter(|_| f.user_unit.status == "available")
    else {
        return none("unsupported", "unavailable", "physical-scale-unavailable");
    };
    let role = if paragraph { "paragraph" } else { "body" };
    let labels: Vec<_> = request
        .payload
        .annotations
        .iter()
        .filter(|a| a.page_index == f.page_index && a.role == role)
        .collect();
    if labels.is_empty() {
        return none(
            "partial",
            "indeterminate",
            if paragraph {
                "confirmed-paragraph-starts-required"
            } else {
                "confirmed-body-boundaries-required"
            },
        );
    }
    if labels.len() > 8 {
        return none("partial", "indeterminate", "metric-evidence-quota");
    }
    let mut evidence = Vec::new();
    for a in labels {
        // Explicit other roles prevent treating their ink as body. A whole-page
        // author label alone still cannot establish absent headers/other paint.
        if request.payload.annotations.iter().any(|other| {
            other.annotation_id != a.annotation_id
                && other.page_index == a.page_index
                && !matches!(other.role.as_str(), "body" | "paragraph")
                && a.character_start.unwrap_or(0)
                    < other.character_end.unwrap_or(f.characters.len())
                && other.character_start.unwrap_or(0)
                    < a.character_end.unwrap_or(f.characters.len())
        }) {
            return none("partial", "indeterminate", "metric-role-scope-overlap");
        }
        let measured = match classify::measure(page, a) {
            Ok(v) => v,
            Err(reason) => return none("partial", "indeterminate", reason),
        };
        let b = measured.bounds;
        let classification = json!({"lineCount":measured.lines.len(),"characterCount":measured.characters,
            "firstBaselineRaw":measured.lines[0].baseline,
            "continuationBaselineRaw":measured.lines.get(1).map(|line|line.baseline)});
        let provenance = json!({"annotationId":a.annotation_id,"role":a.role,"provenance":a.provenance,"certainty":a.certainty});
        let mut add = |metric: &str,
                       raw: f64,
                       raw_error: f64,
                       coordinates: &[f64],
                       expected: f64| {
            let error = classify::physical_error(page, raw, raw_error, coordinates);
            evidence.push(json!({"pageIndex":f.page_index,"characterStart":a.character_start,"characterEnd":a.character_end,
                "rawBox":b,"measured":raw*unit*25.4/72.0,"expected":expected,"unit":"mm",
                "method":if paragraph {"classified-paragraph-start"} else {"native-glyph-ink"},"metric":metric,
                "classification":classification,"uncertainty":{"absoluteError":error,"method":"native-outline-and-float32-bound",
                    "limitations":["conditional-role-scope-only","other-page-paint-not-established","ink-extents-not-layout-box"]},
                "roleProvenance":provenance,"sourceLocation":null}));
        };
        if paragraph {
            let (indent, error) = match measured.indent() {
                Ok(v) => v,
                Err(reason) => return none("partial", "indeterminate", reason),
            };
            add(
                "paragraph-indent",
                indent,
                error,
                &[measured.lines[0].bounds[0], measured.lines[1].bounds[0]],
                catalog::PARAGRAPH_INDENT_MM,
            );
        } else {
            for (metric, raw, coordinates, expected) in [
                (
                    "margin-left",
                    b[0] - f.media[0],
                    [b[0], f.media[0]],
                    catalog::MARGINS_MM[0],
                ),
                (
                    "margin-top",
                    f.media[3] - b[3],
                    [f.media[3], b[3]],
                    catalog::MARGINS_MM[1],
                ),
                (
                    "margin-right",
                    f.media[2] - b[2],
                    [f.media[2], b[2]],
                    catalog::MARGINS_MM[2],
                ),
                (
                    "margin-bottom",
                    b[1] - f.media[1],
                    [b[1], f.media[1]],
                    catalog::MARGINS_MM[3],
                ),
            ] {
                add(metric, raw, measured.error, &coordinates, expected);
            }
        }
    }
    // No source binding or independently validated semantic classifier is part
    // of this operation. Even a caller claiming machine provenance cannot mint
    // normative proof; these remain conditional measured observations.
    (
        "partial",
        "indeterminate",
        if paragraph {
            "conditional-paragraph-indent"
        } else {
            "conditional-body-ink-margins"
        },
        evidence,
    )
}

pub fn evaluate(request: &Request, batch: &FactBatch) -> Vec<Value> {
    let mut results = Vec::new();
    for descriptor in catalog::RULES {
        for page in &batch.pages {
            let f = &page.facts;
            let mut evidence = Vec::new();
            let with_pagination = descriptor.rule_id == "KNU-PAGINATION-001"
                && pagination::enabled(&request.payload.profile.version);
            let with_heading = descriptor.rule_id == heading::RULE_ID
                && request.payload.profile.version == heading::PROFILE_VERSION;
            let with_heading_page_end = descriptor.rule_id == heading::RULE_ID
                && request.payload.profile.version == super::heading_page_end::PROFILE_VERSION;
            let with_paragraph = descriptor.rule_id == paragraph::RULE_ID
                && request.payload.profile.version == paragraph::PROFILE_VERSION;
            let (rule_version, citation, limitations) = if with_pagination {
                ("1.1.0", pagination::citation(), pagination::LIMITATIONS)
            } else if with_heading {
                ("1.1.0", heading::citation(), heading::LIMITATIONS)
            } else if with_heading_page_end {
                (
                    "1.2.0",
                    heading::citation(),
                    super::heading_page_end::LIMITATIONS,
                )
            } else if with_paragraph {
                ("1.1.0", paragraph::citation(), paragraph::LIMITATIONS)
            } else {
                (
                    descriptor.rule_version,
                    json!(descriptor.citation),
                    descriptor.limitations,
                )
            };
            let (support, outcome, reason) = match descriptor.rule_id {
                "KNU-PAPER-001" => {
                    if let Some(unit) = f
                        .user_unit
                        .value
                        .filter(|_| f.user_unit.status == "available")
                    {
                        let w = (f.media[2] - f.media[0]) * unit * 25.4 / 72.0;
                        let h = (f.media[3] - f.media[1]) * unit * 25.4 / 72.0;
                        // This is a numeric observation, not an invented paper
                        // manufacturing/compliance tolerance. PDFium page boxes
                        // and numeric UserUnit are float32. One full relative
                        // ULP per endpoint/scale conservatively bounds parser
                        // roundoff; genuine fixture tests retain the oracle.
                        let eps = f32::EPSILON as f64;
                        let unit_error = if f.user_unit.method == "default-userunit" {
                            0.0
                        } else {
                            unit.abs() * eps
                        };
                        let err = |a: f64, b: f64| {
                            ((a.abs() + b.abs()) * eps * unit.abs()
                                + (b - a).abs() * unit_error
                                + (a.abs() + b.abs()) * eps * unit_error)
                                * 25.4
                                / 72.0
                                + f64::EPSILON * 1000.0
                        };
                        let errors = [err(f.media[0], f.media[2]), err(f.media[1], f.media[3])];
                        let matches = |expected: [f64; 2]| {
                            (w - expected[0]).abs() <= errors[0]
                                && (h - expected[1]).abs() <= errors[1]
                        };
                        evidence.push(json!({"pageIndex":f.page_index,"characterStart":null,"characterEnd":null,"rawBox":f.media,
                            "measured":format!("{w:.9} x {h:.9}"),"expected":"A4 210 x 297; A3 297 x 420 only for necessary illustrations","unit":"mm",
                            "method":"native-page-box","uncertainty":{"absoluteError":errors[0].max(errors[1]),"method":"pdfium-float32-roundoff-bound","limitations":["nominal-paper-tolerance-unspecified"]},
                            "roleProvenance":null,"sourceLocation":null}));
                        if matches(catalog::A4) {
                            ("partial", "pass", "measured-page-matches-nominal-a4")
                        } else if matches(catalog::A3) {
                            let illustration = request.payload.annotations.iter().find(|a| {
                                a.page_index == f.page_index
                                    && a.role == "illustration"
                                    && a.character_start.is_none()
                            });
                            if illustration.is_some_and(|a| {
                                a.certainty == "confirmedObservation"
                                    && a.provenance != "authorLabel"
                            }) {
                                (
                                    "partial",
                                    "pass",
                                    "a3-confirmed-illustration-conditional-observation",
                                )
                            } else {
                                (
                                    "partial",
                                    "indeterminate",
                                    "a3-necessary-illustration-role-not-established",
                                )
                            }
                        } else {
                            // The guideline supplies nominal formats but no
                            // physical-paper tolerance. Report measured values
                            // without inventing an automatic tolerance/failure.
                            (
                                "partial",
                                "indeterminate",
                                "measured-page-differs-from-nominal-paper-review-required",
                            )
                        }
                    } else {
                        ("unsupported", "unavailable", "physical-scale-unavailable")
                    }
                }
                "KNU-MARGINS-001" => {
                    let measured = metrics(request, page, false);
                    evidence = measured.3;
                    (measured.0, measured.1, measured.2)
                }
                "KNU-TYPOGRAPHY-001" => {
                    let observed = typography::evaluate(request, page);
                    evidence = observed.3;
                    (observed.0, observed.1, observed.2)
                }
                paragraph::RULE_ID if with_paragraph => {
                    let measured = paragraph::evaluate(request, page);
                    evidence = measured.2;
                    ("partial", measured.0, measured.1)
                }
                "KNU-PARAGRAPH-RENDERED-001" => {
                    let measured = metrics(request, page, true);
                    evidence = measured.3;
                    (measured.0, measured.1, measured.2)
                }
                heading::RULE_ID if with_heading => {
                    let observation = heading::evaluate(request, page);
                    evidence = observation.2;
                    ("partial", observation.0, observation.1)
                }
                heading::RULE_ID if with_heading_page_end => {
                    let observation = super::heading_page_end::evaluate(request, page);
                    evidence = observation.2;
                    ("partial", observation.0, observation.1)
                }
                "KNU-HEADINGS-RENDERED-001" => {
                    let observed = roles::evaluate(request, page, descriptor.rule_id);
                    evidence = observed.3;
                    (observed.0, observed.1, observed.2)
                }
                "KNU-LENGTH-001" => (
                    "partial",
                    "notApplicable",
                    "delegated-to-existing-rendered-extent-evaluation",
                ),
                "KNU-BIBLIOGRAPHY-RENDERED-001" => (
                    "partial",
                    "notApplicable",
                    "delegated-to-reference-predicates",
                ),
                "KNU-PAGINATION-001" if with_pagination => {
                    let observation = pagination::evaluate(request, page);
                    evidence = observation.2;
                    ("partial", observation.0, observation.1)
                }
                "KNU-PAGINATION-001" | "KNU-CAPTION-RENDERED-001" => {
                    let observed = roles::evaluate(request, page, descriptor.rule_id);
                    evidence = observed.3;
                    (observed.0, observed.1, observed.2)
                }
                "KNU-STRUCTURE-001" => (
                    "partial",
                    "indeterminate",
                    "confirmed-source-and-rendered-structure-required",
                ),
                "KNU-ABSTRACT-001" => (
                    "partial",
                    "indeterminate",
                    "confirmed-abstract-and-reading-order-required",
                ),
                "KNU-REFERENCES-001" => (
                    "partial",
                    "indeterminate",
                    "compiled-bibliography-and-citation-order-required",
                ),
                "KNU-APPENDIX-001" => (
                    "partial",
                    "indeterminate",
                    "appendix-presence-and-designations-unknown",
                ),
                _ => (
                    "partial",
                    "indeterminate",
                    "confirmed-caption-and-illustration-roles-required",
                ),
            };
            results.push(json!({"ruleId":descriptor.rule_id,"ruleVersion":rule_version,"domain":"renderedObservation",
                "supportState":support,"outcome":outcome,"reason":reason,"severity":"warning","citation":citation,
                "evidence":evidence,"limitations":limitations}));
        }
    }
    results
}
