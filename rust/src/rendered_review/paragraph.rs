//! Conditional first-line origin measurement of one confirmed body paragraph.
use super::{
    catalog, classify,
    dto::{Annotation, Character, Page, Request},
};
use serde::Serialize;
use serde_json::{json, Value};

#[cfg(test)]
mod tests;

pub(super) const PROFILE_VERSION: &str = "1.4.0";
pub(super) const RULE_ID: &str = "KNU-PARAGRAPH-RENDERED-001";
pub(super) const LIMITATIONS: &[&str] = &[
    "three-complete-lines-of-one-author-confirmed-paragraph",
    "rendered-text-origins-only",
    "unselected-paragraphs-and-uniformity-unassessed",
];
const UNCERTAINTY_LIMITATIONS: &[&str] = &[
    "conditional-on-left-aligned-paragraph-role",
    "source-text-layout-transform-error-unmeasured",
];
const EXACT_RANGE: &str = "confirmed-paragraph-three-line-range-required";
const INK: &str = "paragraph-line-ink-unavailable";
const ALIGNMENT: &str = "paragraph-continuation-origin-unestablished";
const ORDER: &str = "paragraph-isolated-line-order-unestablished";

pub(super) fn citation() -> Value {
    json!({"section":"3.1","printedPages":[18],"pdfPages":[18]})
}

pub(super) fn evaluate(request: &Request, page: &Page) -> (&'static str, &'static str, Vec<Value>) {
    match observe(request, page) {
        Ok((true, evidence)) => ("fail", "confirmed-paragraph-indent-differs", vec![evidence]),
        Ok((false, evidence)) => (
            "indeterminate",
            "conditional-paragraph-indent-target-in-interval",
            vec![evidence],
        ),
        Err(reason) => ("indeterminate", reason, Vec::new()),
    }
}

fn line_break(chars: &[Character], start: usize) -> bool {
    chars.get(start..start + 2).is_some_and(|pair| {
        pair[0].unicode == 13
            && pair[1].unicode == 10
            && pair
                .iter()
                .all(|c| c.ink_status == "missing" && c.ink_box.is_none())
    })
}

fn origin_roundoff(value: f64) -> f64 {
    value.abs() * f32::EPSILON as f64 + 16.0 * f64::EPSILON
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Start {
    index: usize,
    origin: [f64; 2],
    absolute_error_raw: f64,
}

struct Line {
    first: Start,
    ink: [f64; 4],
    ink_error: f64,
}

fn line(page: &Page, start: usize, end: usize) -> Result<Line, &'static str> {
    let chars = &page.facts.characters[start..end];
    if chars.len() < 2
        || [chars[0].unicode, chars[chars.len() - 1].unicode]
            .iter()
            .any(|&u| char::from_u32(u).is_none_or(char::is_whitespace))
        || chars
            .iter()
            .any(|c| c.unicode != 32 && char::from_u32(c.unicode).is_some_and(char::is_whitespace))
    {
        return Err(EXACT_RANGE);
    }
    let scope = Annotation {
        annotation_id: 0,
        role: String::new(),
        page_index: page.facts.page_index,
        character_start: Some(start),
        character_end: Some(end),
        source_file_id: None,
        source_span: None,
        provenance: String::new(),
        certainty: String::new(),
    };
    let ink = classify::measure(page, &scope).map_err(|_| INK)?;
    let [l, b, r, t] = ink.bounds;
    let [fl, fb, fr, ft] = page.facts.effective;
    let error = ink.error;
    if ink.lines.len() != 1
        || error <= 0.0
        || l - error < fl
        || b - error < fb
        || r + error > fr
        || t + error > ft
    {
        return Err(INK);
    }
    let (Some([x, y]), Some([_, _, _, _, mx, my])) = (chars[0].origin, chars[0].matrix) else {
        return Err(ALIGNMENT);
    };
    // Require the first glyph to be at its native text object's start,
    // avoiding inferred starts from partial runs or side-bearing offsets.
    if (x - mx).abs() > origin_roundoff(x) + origin_roundoff(mx)
        || (y - my).abs() > origin_roundoff(y) + origin_roundoff(my)
        || x < fl
        || x > fr
        || y < fb
        || y > ft
    {
        return Err(ALIGNMENT);
    }
    Ok(Line {
        first: Start {
            index: start,
            origin: [x, y],
            absolute_error_raw: error + origin_roundoff(x) + origin_roundoff(mx),
        },
        ink: ink.bounds,
        ink_error: error,
    })
}

fn observe(request: &Request, page: &Page) -> Result<(bool, Value), &'static str> {
    let f = &page.facts;
    let mut labels = request
        .payload
        .annotations
        .iter()
        .filter(|a| a.page_index == f.page_index && a.role == "paragraph");
    let annotation = labels.next().ok_or(EXACT_RANGE)?;
    if labels.next().is_some() {
        return Err("ambiguous-paragraph-selected-range");
    }
    let (Some(start), Some(end)) = (annotation.character_start, annotation.character_end) else {
        return Err(EXACT_RANGE);
    };
    if annotation.provenance != "authorLabel"
        || annotation.certainty != "confirmedObservation"
        || end - start > classify::MAX_CHARACTERS
        || start != 0 && (start < 2 || !line_break(&f.characters, start - 2))
        || end != f.characters.len() && !line_break(&f.characters, end)
    {
        return Err(EXACT_RANGE);
    }
    let breaks: Vec<_> = (start..end)
        .filter(|&i| line_break(&f.characters, i))
        .collect();
    let [break1, break2] = breaks[..] else {
        return Err(EXACT_RANGE);
    };
    if break1 == start || break1 + 2 >= break2 || break2 + 2 >= end {
        return Err(EXACT_RANGE);
    }
    let unit = f
        .user_unit
        .value
        .ok_or("paragraph-physical-scale-unavailable")?;
    let first = line(page, start, break1)?;
    let next = line(page, break1 + 2, break2)?;
    let last = line(page, break2 + 2, end)?;
    let lines = [first, next, last];
    let left = lines
        .iter()
        .map(|line| line.ink[0] + line.ink_error)
        .fold(f64::NEG_INFINITY, f64::max);
    let right = lines
        .iter()
        .map(|line| line.ink[2] - line.ink_error)
        .fold(f64::INFINITY, f64::min);
    let top = lines[0].ink[3] + lines[0].ink_error;
    let bottom = lines[2].ink[1] - lines[2].ink_error;
    if left >= right
        || lines
            .windows(2)
            .any(|p| p[0].ink[1] - p[0].ink_error <= p[1].ink[3] + p[1].ink_error)
        || f.characters[..start]
            .iter()
            .chain(&f.characters[end..])
            .any(|c| {
                let generated_whitespace = c.ink_status == "missing"
                    && char::from_u32(c.unicode).is_some_and(char::is_whitespace);
                !generated_whitespace
                    && c.origin.is_none_or(|[_, y]| {
                        y + origin_roundoff(y) >= bottom && y - origin_roundoff(y) <= top
                    })
            })
    {
        return Err(ORDER);
    }
    let [x, a, b] = lines.each_ref().map(|l| l.first.origin[0]);
    let [first_error, next_error, last_error] =
        lines.each_ref().map(|l| l.first.absolute_error_raw);
    let spread = (a - b).abs();
    if spread > next_error + last_error {
        return Err(ALIGNMENT);
    }
    let continuation_x = (a + b) / 2.0;
    let raw = x - continuation_x;
    let raw_error = first_error + next_error.max(last_error) + spread / 2.0;
    let mm = raw * unit * 25.4 / 72.0;
    let error = classify::physical_error(page, raw, raw_error, &[x, a, b]);
    let expected = catalog::PARAGRAPH_INDENT_MM;
    let differs = mm + error < expected || mm - error > expected;
    let ink = [
        lines.iter().map(|l| l.ink[0]).fold(f64::INFINITY, f64::min),
        lines[2].ink[1],
        lines
            .iter()
            .map(|l| l.ink[2])
            .fold(f64::NEG_INFINITY, f64::max),
        lines[0].ink[3],
    ];
    Ok((
        differs,
        json!({
            "pageIndex":f.page_index,"characterStart":start,"characterEnd":end,
            "rawBox":ink,"measured":mm,"expected":expected,"unit":"mm",
            "method":"confirmed-paragraph-first-line-origin",
            "uncertainty":{"absoluteError":error,"method":"native-outline-and-float32-bound","limitations":UNCERTAINTY_LIMITATIONS},
            "roleProvenance":{"annotationId":annotation.annotation_id,"role":"paragraph","provenance":"authorLabel","certainty":"confirmedObservation"},
            "sourceLocation":null,"observation":{
                "kind":"confirmed-paragraph-indent","starts":lines.map(|l| l.first),
                "effectiveBox":f.effective,"userUnit":unit,"rawIndent":raw,"absoluteErrorRaw":raw_error,
                "comparison":if differs { "outsideTarget" } else { "targetWithinUncertainty" }
            }
        }),
    ))
}
