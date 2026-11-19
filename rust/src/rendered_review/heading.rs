//! Positive same-page ink evidence for one explicitly scoped major heading and
//! one ordinary body run. No first-on-page or section sequence inference.
use super::{
    classify::{self, Measurement},
    dto::{Annotation, Page, Request},
};
use serde_json::{json, Value};

#[cfg(test)]
mod tests;

pub(super) const PROFILE_VERSION: &str = "1.3.0";
pub(super) const RULE_ID: &str = "KNU-HEADINGS-RENDERED-001";
pub(super) const LIMITATIONS: &[&str] = &[
    "heading-and-body-roles-author-confirmed",
    "selected-same-page-ink-only",
    "unselected-content-unassessed",
    "effective-style-and-text-alignment-unestablished",
    "section-sequence-outside-page-unassessed",
];
const EVIDENCE_LIMITATIONS: &[&str] = &[
    "positive-ink-only",
    "only-selected-author-ranges",
    "no-immediate-neighbor-or-page-start-proof",
];

pub(super) fn citation() -> Value {
    json!({"section":"3.2","printedPages":[18,19],"pdfPages":[18,19]})
}

pub(super) fn evaluate(request: &Request, page: &Page) -> (&'static str, &'static str, Vec<Value>) {
    match observe(request, page) {
        Ok((true, evidence)) => (
            "fail",
            "confirmed-major-heading-follows-body-on-page",
            vec![evidence],
        ),
        Ok((false, evidence)) => (
            "indeterminate",
            "conditional-major-heading-precedes-body-on-page",
            vec![evidence],
        ),
        Err(reason) => ("indeterminate", reason, Vec::new()),
    }
}

fn selected_range<'a>(
    request: &'a Request,
    page: &Page,
    role: &str,
    missing: &'static str,
) -> Result<(&'a Annotation, usize, usize), &'static str> {
    let mut labels = request
        .payload
        .annotations
        .iter()
        .filter(|a| a.page_index == page.facts.page_index && a.role == role);
    let label = labels.next().ok_or(missing)?;
    if labels.next().is_some() {
        return Err("ambiguous-heading-role-pair");
    }
    let (Some(start), Some(end)) = (label.character_start, label.character_end) else {
        return Err(missing);
    };
    if label.provenance != "authorLabel" || label.certainty != "confirmedObservation" {
        return Err(missing);
    }
    Ok((label, start, end))
}

fn ink(page: &Page, annotation: &Annotation) -> Result<Measurement, &'static str> {
    let measured = classify::measure(page, annotation).map_err(|_| "heading-ink-unavailable")?;
    if measured.lines.len() != 1 || measured.error <= 0.0 {
        return Err("heading-ink-unavailable");
    }
    let [left, bottom, right, top] = measured.bounds;
    let [fl, fb, fr, ft] = page.facts.effective;
    let error = measured.error;
    if left - error < fl || bottom - error < fb || right + error > fr || top + error > ft {
        return Err("heading-ink-outside-effective-page");
    }
    Ok(measured)
}

fn edge_error(first: f64, second: f64) -> f64 {
    8.0 * f64::EPSILON * (first.abs() + second.abs()).max(1.0)
}

fn observe(request: &Request, page: &Page) -> Result<(bool, Value), &'static str> {
    let (heading, heading_start, heading_end) = selected_range(
        request,
        page,
        "headingMajor",
        "confirmed-major-heading-range-required",
    )?;
    let (body, body_start, body_end) = selected_range(
        request,
        page,
        "body",
        "confirmed-heading-body-range-required",
    )?;
    let body_first = body_end <= heading_start;
    let uncertain_order = "heading-native-and-vertical-order-unestablished";
    if !body_first && heading_end > body_start {
        return Err(uncertain_order);
    }
    if page.facts.rotation != 0 || page.capabilities.schema_version != 2 {
        return Err("heading-upright-layout-required");
    }
    let unit = page
        .facts
        .user_unit
        .value
        .ok_or("heading-physical-scale-unavailable")?;
    let heading_ink = ink(page, heading)?;
    let body_ink = ink(page, body)?;
    let [hl, hb, hr, ht] = heading_ink.bounds;
    let [bl, bb, br, bt] = body_ink.bounds;
    let left = (hl + heading_ink.error).max(bl + body_ink.error);
    let right = (hr - heading_ink.error).min(br - body_ink.error);
    if right - left <= edge_error(right, left) {
        return Err("heading-horizontal-overlap-unestablished");
    }
    // Positive gap puts body above heading; negative gap puts body below.
    let (body_edge, heading_edge) = if body_first { (bb, ht) } else { (bt, hb) };
    let raw_gap = body_edge - heading_edge;
    let raw_error = heading_ink.error + body_ink.error + edge_error(body_edge, heading_edge);
    let gap = raw_gap * unit * 25.4 / 72.0;
    let error = classify::physical_error(page, raw_gap, raw_error, &[body_edge, heading_edge]);
    if (body_first && (raw_gap <= raw_error || gap <= error))
        || (!body_first && (raw_gap >= -raw_error || gap >= -error))
    {
        return Err(uncertain_order);
    }
    Ok((
        body_first,
        json!({
            "pageIndex": page.facts.page_index, "characterStart": heading_start, "characterEnd": heading_end,
            "rawBox": heading_ink.bounds, "measured": gap, "expected": null, "unit": "mm",
            "method": "confirmed-major-heading-and-body-ink",
            "uncertainty": {
                "absoluteError": error, "method": "native-outline-and-float32-bound",
                "limitations": EVIDENCE_LIMITATIONS,
            },
            "roleProvenance": {
                "annotationId": heading.annotation_id, "role": "headingMajor", "provenance": "authorLabel",
                "certainty": "confirmedObservation",
            },
            "sourceLocation": null,
            "observation": {
                "kind": "confirmed-major-heading-page-order", "bodyAnnotationId": body.annotation_id,
                "bodyCharacterStart": body_start, "bodyCharacterEnd": body_end, "bodyInkBox": body_ink.bounds,
                "effectiveBox": page.facts.effective, "userUnit": unit,
                "rawSignedGap": raw_gap, "absoluteErrorRaw": raw_error,
                "relativeOrder": if body_first { "body-above-heading" } else { "body-below-heading" },
            },
        }),
    ))
}
