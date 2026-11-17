//! Conditional numeric ink/baseline observations, never a font-height or
//! leading definition. Unicode mappings do not establish visual glyph identity.
use super::{classify, dto::*};
use serde_json::{json, Value};

pub const HEIGHT_EXPECTED: &str =
    "Font height is undefined in section 3.1; letter ink height is an observation only";
pub const BASELINE_EXPECTED: &str =
    "1.5 line spacing has no baseline-distance definition in section 3.1";

pub fn evaluate(
    request: &Request,
    page: &Page,
) -> (&'static str, &'static str, &'static str, Vec<Value>) {
    // Keep old schema1 behavior: it has no native measurement error witness.
    let none = |reason| ("partial", "indeterminate", reason, Vec::new());
    if page.capabilities.schema_version != 2 || page.capabilities.glyph_ink != "available" {
        return none("measurement-definition-unresolved");
    }
    let f = &page.facts;
    let Some(unit) = f
        .user_unit
        .value
        .filter(|_| f.user_unit.status == "available")
    else {
        return none("physical-scale-unavailable");
    };
    let annotations: Vec<_> = request
        .payload
        .annotations
        .iter()
        .filter(|a| a.page_index == f.page_index && matches!(a.role.as_str(), "body" | "paragraph"))
        .collect();
    if annotations.is_empty() {
        return none("measurement-definition-unresolved");
    }
    if annotations.len() > 8 {
        return none("metric-evidence-quota");
    }
    let mut evidence = Vec::new();
    for a in annotations {
        let start = a.character_start.unwrap_or(0);
        let end = a.character_end.unwrap_or(f.characters.len());
        if request.payload.annotations.iter().any(|other| {
            other.annotation_id != a.annotation_id
                && other.page_index == a.page_index
                && !matches!(other.role.as_str(), "body" | "paragraph")
                && start < other.character_end.unwrap_or(f.characters.len())
                && other.character_start.unwrap_or(0) < end
        }) {
            return none("metric-role-scope-overlap");
        }
        let measured = match classify::measure(page, a) {
            Ok(value) => value,
            Err(reason) => return none(reason),
        };
        // Distinct baseline representatives are insufficient when their actual
        // ink uncertainty envelopes overlap. Do not guess multiline structure.
        if measured
            .lines
            .windows(2)
            .any(|pair| pair[0].bounds[1] - pair[0].error <= pair[1].bounds[3] + pair[1].error)
        {
            return none("metric-ambiguous-line-order");
        }
        let classification = json!({"lineCount":measured.lines.len(),"characterCount":measured.characters,
            "firstBaselineRaw":measured.lines[0].baseline,
            "continuationBaselineRaw":measured.lines.get(1).map(|line|line.baseline)});
        let provenance = json!({"annotationId":a.annotation_id,"role":a.role,"provenance":a.provenance,"certainty":a.certainty});
        let mut add = |metric: &str,
                       raw: f64,
                       raw_error: f64,
                       coordinates: &[f64],
                       expected: &str,
                       method: &str| {
            evidence.push(json!({"pageIndex":f.page_index,"characterStart":a.character_start,"characterEnd":a.character_end,
                "rawBox":measured.bounds,"measured":raw*unit*25.4/72.0,"expected":expected,"unit":"mm","metric":metric,"method":method,
                "classification":classification,"roleProvenance":provenance,"sourceLocation":null,
                "uncertainty":{"absoluteError":classify::physical_error(page,raw,raw_error,coordinates),
                    "method":"native-outline-and-float32-bound","limitations":["conditional-role-scope-only",
                    "extracted-unicode-not-visual-glyph-proof","ink-height-not-font-height","baseline-distance-not-leading-definition"]}}));
        };
        let letters: Vec<_> = f.characters[start..end]
            .iter()
            .filter(|c| char::from_u32(c.unicode).is_some_and(char::is_alphabetic))
            .collect();
        if !letters.is_empty() {
            let mut low = f64::INFINITY;
            let mut high = f64::NEG_INFINITY;
            let mut error: f64 = 0.0;
            let mut largest_coordinate: f64 = 0.0;
            for ch in letters {
                // classify::measure established every non-whitespace glyph's
                // actual ink and error. Alphabetic scalars are not whitespace.
                let b = ch.ink_box.unwrap();
                let e = ch.ink_uncertainty.as_ref().unwrap().absolute_error_raw;
                let height = b[3] - b[1];
                if height <= 2.0 * e {
                    return none("metric-horizontal-frame-required");
                }
                low = low.min(height);
                high = high.max(height);
                error = error.max(2.0 * e);
                largest_coordinate = largest_coordinate.max(b[1].abs()).max(b[3].abs());
            }
            // A common maximum error bounds extrema even when uncertainty can
            // change which glyph supplies the minimum or maximum observation.
            let coordinates = [largest_coordinate, largest_coordinate];
            add(
                "letter-ink-height-min",
                low,
                error,
                &coordinates,
                HEIGHT_EXPECTED,
                "native-glyph-ink-height",
            );
            add(
                "letter-ink-height-max",
                high,
                error,
                &coordinates,
                HEIGHT_EXPECTED,
                "native-glyph-ink-height",
            );
        }
        if measured.lines.len() >= 2 {
            let mut low = f64::INFINITY;
            let mut high = f64::NEG_INFINITY;
            let mut error: f64 = 0.0;
            let mut largest_coordinate: f64 = 0.0;
            for pair in measured.lines.windows(2) {
                let distance = pair[0].baseline - pair[1].baseline;
                low = low.min(distance);
                high = high.max(distance);
                // Include the native outline uncertainty used to establish the
                // nonoverlapping line classification, plus float/unit roundoff.
                error = error.max(pair[0].error + pair[1].error);
                largest_coordinate = largest_coordinate
                    .max(pair[0].baseline.abs())
                    .max(pair[1].baseline.abs());
            }
            let coordinates = [largest_coordinate, largest_coordinate];
            add(
                "baseline-distance-min",
                low,
                error,
                &coordinates,
                BASELINE_EXPECTED,
                "classified-baseline-distance",
            );
            add(
                "baseline-distance-max",
                high,
                error,
                &coordinates,
                BASELINE_EXPECTED,
                "classified-baseline-distance",
            );
        }
    }
    if evidence.is_empty() {
        none("measurement-definition-unresolved")
    } else {
        (
            "partial",
            "indeterminate",
            "conditional-typography-observations",
            evidence,
        )
    }
}
