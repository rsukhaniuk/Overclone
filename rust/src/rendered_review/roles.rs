//! Literal native-index text and conditional measured locations. Neither
//! ToUnicode/ActualText nor an annotation proves visual glyph case or a role.
use super::{classify, dto::*};
use serde_json::{json, Value};

const TEXT_BYTES: usize = 256;
const LABELS: usize = 8;

pub fn evaluate(
    request: &Request,
    page: &Page,
    rule: &str,
) -> (&'static str, &'static str, &'static str, Vec<Value>) {
    let belongs = |a: &&Annotation| {
        a.page_index == page.facts.page_index
            && match rule {
                "KNU-PAGINATION-001" => a.role == "pageNumber",
                "KNU-HEADINGS-RENDERED-001" => {
                    matches!(a.role.as_str(), "headingMajor" | "headingMinor")
                }
                _ => matches!(a.role.as_str(), "figureCaption" | "tableCaption"),
            }
    };
    let labels: Vec<_> = request.payload.annotations.iter().filter(belongs).collect();
    let none = |reason| ("partial", "indeterminate", reason, Vec::new());
    if labels.is_empty() {
        return if rule == "KNU-PAGINATION-001" {
            none("confirmed-page-number-and-title-roles-required")
        } else if rule == "KNU-HEADINGS-RENDERED-001" {
            if page.capabilities.text_style != "available" {
                (
                    "unsupported",
                    "unavailable",
                    "effective-style-unavailable",
                    Vec::new(),
                )
            } else {
                none("confirmed-heading-neighbors-required")
            }
        } else {
            none("confirmed-caption-and-illustration-roles-required")
        };
    }
    if labels.len() > LABELS {
        return none("role-evidence-quota");
    }
    if rule == "KNU-PAGINATION-001" && labels.len() != 1 {
        return none("role-ambiguous-duplicates");
    }
    let mut evidence = Vec::new();
    for annotation in labels {
        match observe(page, annotation) {
            Ok(value) => evidence.push(value),
            Err(reason) => return none(reason),
        }
    }
    (
        "partial",
        "indeterminate",
        match rule {
            "KNU-PAGINATION-001" => "conditional-page-number-literal",
            "KNU-HEADINGS-RENDERED-001" => "conditional-heading-literal",
            _ => "conditional-caption-literal",
        },
        evidence,
    )
}

fn observe(page: &Page, a: &Annotation) -> Result<Value, &'static str> {
    let f = &page.facts;
    let start = a.character_start.unwrap_or(0);
    let end = a.character_end.unwrap_or(f.characters.len());
    if end - start > TEXT_BYTES {
        return Err("role-text-quota");
    }
    let mut text = String::new();
    for ch in &f.characters[start..end] {
        let scalar = char::from_u32(ch.unicode).ok_or("role-unknown-unicode")?;
        if scalar == '\0'
            || scalar == '\u{fffd}'
            || scalar.is_control() && !matches!(scalar, '\r' | '\n' | '\t')
        {
            return Err("role-unknown-unicode");
        }
        text.push(scalar);
        if text.len() > TEXT_BYTES {
            return Err("role-text-quota");
        }
    }
    let literal = text.trim();
    if literal.is_empty() {
        return Err("role-empty-text");
    }
    let upper = literal.chars().any(char::is_uppercase);
    let lower = literal.chars().any(char::is_lowercase);
    let case = match (upper, lower) {
        (true, false) => "upper",
        (false, true) => "lower",
        (true, true) => "mixed",
        _ => "uncased",
    };
    let number = if literal.bytes().all(|b| b.is_ascii_digit()) {
        literal
            .parse::<u64>()
            .ok()
            .filter(|n| *n <= 9_007_199_254_740_991)
    } else {
        None
    };
    let prefix = match literal.split_whitespace().next() {
        Some("Рисунок") => "figure",
        Some("Таблиця") => "table",
        _ => "none",
    };
    let kind = if a.role == "pageNumber" {
        "page-number"
    } else if a.role.starts_with("heading") {
        "heading"
    } else {
        "caption"
    };
    let (text_order, placement, placement_reason) = match classify::measure(page, a) {
        Ok(measured) => {
            if let Some(unit) = f
                .user_unit
                .value
                .filter(|_| f.user_unit.status == "available")
            {
                let b = measured.bounds;
                let top = f.media[3] - b[3];
                let right = f.media[2] - b[2];
                let centre = (b[0] + b[2] - f.media[0] - f.media[2]) * 0.5;
                let error =
                    classify::physical_error(page, top, measured.error, &[f.media[3], b[3]])
                        .max(classify::physical_error(
                            page,
                            right,
                            measured.error,
                            &[f.media[2], b[2]],
                        ))
                        .max(classify::physical_error(
                            page,
                            centre,
                            measured.error,
                            &[b[0], b[2], f.media[0], f.media[2]],
                        ));
                (
                    "validated-horizontal",
                    json!({"media":f.media,"inkBox":b,"topClearanceMm":top*unit*25.4/72.0,
                    "rightClearanceMm":right*unit*25.4/72.0,"pageCentreOffsetMm":centre*unit*25.4/72.0,
                    "absoluteErrorMm":error,"lineCount":measured.lines.len()}),
                    "native-outline-location",
                )
            } else {
                (
                    "validated-horizontal",
                    Value::Null,
                    "physical-scale-unavailable",
                )
            }
        }
        Err(reason) => ("native-index", Value::Null, reason),
    };
    // This is an extracted literal, not visual bold/case/digit identity. The
    // page centre is not the text-layout centre. No image/table geometry is in
    // this fact schema, so a labelled illustration cannot prove caption order.
    Ok(
        json!({"pageIndex":f.page_index,"characterStart":a.character_start,"characterEnd":a.character_end,
        "rawBox":placement.get("inkBox").cloned().unwrap_or(Value::Null),"measured":text,
        "expected":match kind {"page-number"=>"literal Arabic page number; document numbering and top-right placement require proof",
            "heading"=>"literal heading case and punctuation; visual style and text alignment require proof",
            _=>"literal Рисунок/Таблиця prefix and no final full stop; object order requires native geometry"},
        "unit":"text","method":"annotation-scoped-native-text",
        "uncertainty":{"absoluteError":null,"method":"native-character-unicode","limitations":[
            "conditional-role-scope-only","extracted-text-not-visual-glyph-proof","role-and-document-order-not-established","page-centre-not-text-layout-centre"]},
        "roleProvenance":{"annotationId":a.annotation_id,"role":a.role,"provenance":a.provenance,"certainty":a.certainty},"sourceLocation":null,
        "observation":{"kind":kind,"textOrder":text_order,"unicodeCase":case,"asciiDigitValue":number,"oneBasedPdfPage":u32::from(f.page_index)+1,
            "captionPrefix":prefix,"finalFullStop":literal.ends_with('.'),"placement":placement,"placementReason":placement_reason,
            "relativeOrder":if kind=="caption" {Some("illustration-geometry-unavailable")} else {None}}}),
    )
}
