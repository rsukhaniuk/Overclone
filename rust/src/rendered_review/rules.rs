use super::{catalog, dto::*};
use serde_json::{json, Value};
pub fn evaluate(request: &Request, batch: &FactBatch) -> Vec<Value> {
    let mut results = Vec::new();
    for descriptor in catalog::RULES {
        for page in &batch.pages {
            let f = &page.facts;
            let c = &page.capabilities;
            let mut evidence = Vec::new();
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
                "KNU-MARGINS-001" if c.glyph_ink != "available" => {
                    ("unsupported", "unavailable", "glyph-ink-unavailable")
                }
                "KNU-MARGINS-001" => (
                    "partial",
                    "indeterminate",
                    "confirmed-body-boundaries-required",
                ),
                "KNU-TYPOGRAPHY-001" => (
                    "partial",
                    "indeterminate",
                    "measurement-definition-unresolved",
                ),
                "KNU-PARAGRAPH-RENDERED-001" if c.glyph_ink != "available" => {
                    ("unsupported", "unavailable", "glyph-ink-unavailable")
                }
                "KNU-PARAGRAPH-RENDERED-001" => (
                    "partial",
                    "indeterminate",
                    "confirmed-paragraph-starts-required",
                ),
                "KNU-HEADINGS-RENDERED-001" if c.text_style != "available" => {
                    ("unsupported", "unavailable", "effective-style-unavailable")
                }
                "KNU-HEADINGS-RENDERED-001" => (
                    "partial",
                    "indeterminate",
                    "confirmed-heading-neighbors-required",
                ),
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
                "KNU-PAGINATION-001" => (
                    "partial",
                    "indeterminate",
                    "confirmed-page-number-and-title-roles-required",
                ),
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
            results.push(json!({"ruleId":descriptor.rule_id,"ruleVersion":descriptor.rule_version,"domain":"renderedObservation",
                "supportState":support,"outcome":outcome,"reason":reason,"severity":"warning","citation":descriptor.citation,
                "evidence":evidence,"limitations":descriptor.limitations}));
        }
    }
    results
}
