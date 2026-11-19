//! KNU §3.3 work page numbers, scoped by explicit author roles and native ink.
//! Section 3.16 permits copied appendices to keep their separate numbering.
use super::{
    classify,
    dto::{Annotation, Page, Request},
};
use serde::Serialize;
use serde_json::{json, Value};

#[cfg(test)]
mod tests;

pub(super) const PROFILE_VERSION: &str = "1.2.0";
pub(super) fn enabled(version: &str) -> bool {
    matches!(version, PROFILE_VERSION | super::heading::PROFILE_VERSION)
}
pub(super) const LIMITATIONS: &[&str] = &[
    "empty-text-does-not-prove-number-absence",
    "extracted-unicode-not-visual-digit-proof",
    "work-number-role-is-author-confirmed",
    "top-right-corner-criterion-unspecified",
    "uncaptured-pages-unassessed",
    "copied-appendix-own-numbering-unassessed",
];
const EVIDENCE_LIMITATIONS: &[&str] = &[
    "conditional-on-confirmed-work-page-number",
    "extracted-unicode-not-visual-digit-proof",
    "uncaptured-pages-unassessed",
];
const MAX_NUMBER_CHARACTERS: usize = 32;
const MAX_SAFE_INTEGER: u64 = 9_007_199_254_740_991;

pub(super) fn citation() -> Value {
    json!({"section":"3.3;3.16","printedPages":[19,31],"pdfPages":[19,31]})
}

pub(super) fn title_number_pair(a: &Annotation, b: &Annotation) -> bool {
    confirmed_title(a) && confirmed_number(b) || confirmed_title(b) && confirmed_number(a)
}

fn confirmed_author(annotation: &Annotation) -> bool {
    annotation.provenance == "authorLabel" && annotation.certainty == "confirmedObservation"
}
fn confirmed_title(annotation: &Annotation) -> bool {
    annotation.role == "title"
        && annotation.page_index == 0
        && annotation.character_start.is_none()
        && annotation.character_end.is_none()
        && confirmed_author(annotation)
}
fn confirmed_number(annotation: &Annotation) -> bool {
    annotation.role == "pageNumber"
        && confirmed_author(annotation)
        && annotation.character_start.is_some()
        && annotation.character_end.is_some()
}

pub(super) fn evaluate(request: &Request, page: &Page) -> (&'static str, &'static str, Vec<Value>) {
    match observed(request, page) {
        Ok(evidence) => {
            let (outcome, reason) = match evidence.observation.comparison {
                Comparison::TitleNumberPresent => ("fail", "confirmed-title-number-present"),
                Comparison::IndexMatch => ("indeterminate", "confirmed-page-number-index-match"),
                Comparison::IndexMismatch => {
                    ("indeterminate", "confirmed-page-number-index-mismatch")
                }
            };
            (outcome, reason, vec![json!(evidence)])
        }
        Err(reason) => ("indeterminate", reason, Vec::new()),
    }
}

fn observed(request: &Request, page: &Page) -> Result<Evidence, &'static str> {
    let facts = &page.facts;
    let annotations = || {
        request
            .payload
            .annotations
            .iter()
            .filter(|a| a.page_index == facts.page_index)
    };
    let mut numbers = annotations().filter(|a| a.role == "pageNumber");
    let number = numbers
        .next()
        .ok_or("confirmed-page-number-range-required")?;
    if numbers.next().is_some() {
        return Err("ambiguous-page-number-labels");
    }
    if !confirmed_number(number) {
        return Err("confirmed-page-number-range-required");
    }
    let mut titles = annotations().filter(|a| a.role == "title");
    let title_annotation_id = if facts.page_index == 0 {
        let title = titles
            .next()
            .filter(|a| confirmed_title(a))
            .ok_or("confirmed-title-role-required")?;
        if titles.next().is_some() {
            return Err("conflicting-title-role");
        }
        Some(title.annotation_id)
    } else {
        if titles.next().is_some() {
            return Err("conflicting-title-role");
        }
        None
    };
    let (Some(start), Some(end)) = (number.character_start, number.character_end) else {
        return Err("confirmed-page-number-range-required");
    };
    let glyphs = &facts.characters[start..end];
    // Preserve native scalars. Do not normalize Roman, Arabic-Indic, fullwidth
    // or lookalike glyph mappings into the supported ASCII-decimal observation.
    if glyphs.len() > MAX_NUMBER_CHARACTERS
        || glyphs
            .iter()
            .any(|glyph| !(48..=57).contains(&glyph.unicode))
    {
        return Err("page-number-non-ascii-or-out-of-range");
    }
    let literal: String = glyphs
        .iter()
        .map(|glyph| char::from(glyph.unicode as u8))
        .collect();
    let value = literal
        .parse::<u64>()
        .ok()
        .filter(|n| *n <= MAX_SAFE_INTEGER)
        .ok_or("page-number-non-ascii-or-out-of-range")?;
    let ink = classify::measure(page, number).map_err(|_| "page-number-unavailable-ink")?;
    if ink.lines.len() != 1 || ink.characters != glyphs.len() || ink.error <= 0.0 {
        return Err("page-number-unavailable-ink");
    }
    let [left, bottom, right, top] = ink.bounds;
    let [fl, fb, fr, ft] = facts.effective;
    if left - ink.error < fl
        || bottom - ink.error < fb
        || right + ink.error > fr
        || top + ink.error > ft
    {
        return Err("page-number-ink-outside-effective-page");
    }
    let unit = facts
        .user_unit
        .value
        .ok_or("page-number-physical-scale-unavailable")?;
    let top_clearance = facts.media[3] - top;
    let right_clearance = facts.media[2] - right;
    let absolute_error_mm =
        classify::physical_error(page, top_clearance, ink.error, &[facts.media[3], top]).max(
            classify::physical_error(page, right_clearance, ink.error, &[facts.media[2], right]),
        );
    let physical_number = facts.page_index + 1;
    let expected = title_annotation_id.is_none().then_some(physical_number);
    let comparison = match expected {
        None => Comparison::TitleNumberPresent,
        Some(n) if u64::from(n) == value => Comparison::IndexMatch,
        Some(_) => Comparison::IndexMismatch,
    };
    Ok(Evidence {
        page_index: facts.page_index,
        character_start: start,
        character_end: end,
        raw_box: ink.bounds,
        measured: literal,
        expected,
        unit: "text",
        method: "confirmed-page-number-native-ink",
        uncertainty: Uncertainty {
            absolute_error: None,
            method: "native-character-unicode",
            limitations: EVIDENCE_LIMITATIONS,
        },
        role_provenance: Provenance {
            annotation_id: number.annotation_id,
            role: "pageNumber",
            provenance: "authorLabel",
            certainty: "confirmedObservation",
        },
        source_location: None,
        observation: Observation {
            kind: "confirmed-page-number",
            title_annotation_id,
            physical_page_number: physical_number,
            ascii_digit_value: value,
            comparison,
            placement: Placement {
                media: facts.media,
                effective: facts.effective,
                top_clearance_mm: top_clearance * unit * 25.4 / 72.0,
                right_clearance_mm: right_clearance * unit * 25.4 / 72.0,
                absolute_error_mm,
            },
        },
    })
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Evidence {
    page_index: u16,
    character_start: usize,
    character_end: usize,
    raw_box: [f64; 4],
    measured: String,
    expected: Option<u16>,
    unit: &'static str,
    method: &'static str,
    uncertainty: Uncertainty,
    role_provenance: Provenance,
    source_location: Option<()>,
    observation: Observation,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Uncertainty {
    absolute_error: Option<f64>,
    method: &'static str,
    limitations: &'static [&'static str],
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Provenance {
    annotation_id: u64,
    role: &'static str,
    provenance: &'static str,
    certainty: &'static str,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Observation {
    kind: &'static str,
    title_annotation_id: Option<u64>,
    physical_page_number: u16,
    ascii_digit_value: u64,
    comparison: Comparison,
    placement: Placement,
}

#[derive(Serialize)]
#[serde(rename_all = "kebab-case")]
enum Comparison {
    TitleNumberPresent,
    IndexMatch,
    IndexMismatch,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Placement {
    media: [f64; 4],
    effective: [f64; 4],
    top_clearance_mm: f64,
    right_clearance_mm: f64,
    absolute_error_mm: f64,
}
