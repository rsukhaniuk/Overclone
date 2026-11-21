//! Compare the baseline interval of a confirmed two-line heading with the
//! observed intervals of the adjacent confirmed body paragraph (KNU §3.2).
use super::{
    classify,
    dto::{Annotation, Character, Page, Request},
};
use serde_json::{json, Value};
#[cfg(test)]
mod tests;

pub(super) const PROFILE_VERSION: &str = "1.6.0";
pub(super) const LIMITATIONS: &[&str] = &[
    "one-confirmed-two-line-heading-and-three-line-body-paragraph",
    "observed-baseline-intervals-only",
    "nominal-spacing-factor-unassessed",
    "unselected-headings-unassessed",
];
const EVIDENCE_LIMITATIONS: &[&str] = &[
    "conditional-on-confirmed-heading-and-body-roles",
    "source-text-layout-transform-error-unmeasured",
];
const REQUIRED: &str = "confirmed-heading-and-paragraph-ranges-required";
const ADJACENT: &str = "heading-spacing-complete-adjacent-lines-required";
const GEOMETRY: &str = "heading-spacing-line-geometry-unavailable";
const REFERENCE: &str = "heading-spacing-body-reference-unestablished";
const DIFFERENT: &str = "confirmed-heading-baselines-differ-from-local-body";
const INCONCLUSIVE: &str = "conditional-heading-body-baseline-separation-unestablished";

pub(super) fn citation() -> Value {
    json!({"section":"3.2","printedPages":[19],"pdfPages":[19]})
}

pub(super) fn evaluate(request: &Request, page: &Page) -> (&'static str, Vec<Value>) {
    match observe(request, page) {
        Ok((true, evidence)) => (DIFFERENT, vec![evidence]),
        Ok((false, evidence)) => (INCONCLUSIVE, vec![evidence]),
        Err(reason) => (reason, Vec::new()),
    }
}

fn separator(chars: &[Character], at: usize) -> bool {
    chars
        .get(at..at + 2)
        .is_some_and(super::heading_page_end::line_break)
}

fn selected_range(label: &Annotation) -> Result<(usize, usize), &'static str> {
    match (label.character_start, label.character_end) {
        (Some(start), Some(end))
            if label.provenance == "authorLabel" && label.certainty == "confirmedObservation" =>
        {
            Ok((start, end))
        }
        _ => Err(REQUIRED),
    }
}

fn measure_line(page: &Page, start: usize, end: usize) -> Result<classify::Run, &'static str> {
    let chars = &page.facts.characters[start..end];
    if chars.is_empty()
        || chars[0].unicode == 32
        || chars[chars.len() - 1].unicode == 32
        || chars.iter().any(|c| {
            c.unicode != 32
                && char::from_u32(c.unicode)
                    .is_none_or(|u| u.is_control() || u.is_whitespace() || u == '\u{fffd}')
        })
    {
        return Err(ADJACENT);
    }
    let measured = classify::measure_range(page, start, end).map_err(|_| GEOMETRY)?;
    let [left, bottom, right, top] = measured.bounds;
    let [page_left, page_bottom, page_right, page_top] = page.facts.effective;
    let margin = measured.error;
    if measured.lines.len() != 1
        || measured.characters < 2
        || margin <= 0.0
        || left - margin < page_left
        || bottom - margin < page_bottom
        || right + margin > page_right
        || top + margin > page_top
    {
        return Err(GEOMETRY);
    }
    measured.lines.into_iter().next().ok_or(GEOMETRY)
}

struct Interval {
    mm: f64,
    error: f64,
}

impl Interval {
    fn disjoint(&self, other: &Self) -> bool {
        self.mm + self.error < other.mm - other.error
            || other.mm + other.error < self.mm - self.error
    }
}

fn interval(page: &Page, unit: f64, upper: &classify::Run, lower: &classify::Run) -> Interval {
    let distance = upper.baseline - lower.baseline;
    Interval {
        mm: distance * unit * 25.4 / 72.0,
        error: classify::physical_error(
            page,
            distance,
            upper.error + lower.error,
            &[upper.baseline, lower.baseline],
        ),
    }
}

fn observe(request: &Request, page: &Page) -> Result<(bool, Value), &'static str> {
    let f = &page.facts;
    let labels = request
        .payload
        .annotations
        .iter()
        .filter(|a| a.page_index == f.page_index);
    let mut headings = labels
        .clone()
        .filter(|a| matches!(a.role.as_str(), "headingMajor" | "headingMinor"));
    let mut paragraphs = labels.filter(|a| a.role == "paragraph");
    let (heading, paragraph) = (
        headings.next().ok_or(REQUIRED)?,
        paragraphs.next().ok_or(REQUIRED)?,
    );
    if headings.next().is_some() || paragraphs.next().is_some() {
        return Err("ambiguous-heading-or-paragraph-ranges");
    }
    let (h_start, h_end) = selected_range(heading)?;
    let (p_start, p_end) = selected_range(paragraph)?;
    let chars = &f.characters;
    if h_end + 2 != p_start
        || p_end - h_start > classify::MAX_CHARACTERS
        || !separator(chars, h_end)
        || h_start != 0 && (h_start < 2 || !separator(chars, h_start - 2))
        || p_end != chars.len() && !separator(chars, p_end)
    {
        return Err(ADJACENT);
    }
    let heading_breaks: Vec<_> = (h_start..h_end)
        .filter(|&at| separator(chars, at))
        .collect();
    let body_breaks: Vec<_> = (p_start..p_end)
        .filter(|&at| separator(chars, at))
        .collect();
    let ([heading_break], [first_body_break, second_body_break]) =
        (&heading_breaks[..], &body_breaks[..])
    else {
        return Err(ADJACENT);
    };
    if heading_break + 2 >= h_end
        || first_body_break + 2 >= *second_body_break
        || second_body_break + 2 >= p_end
    {
        return Err(ADJACENT);
    }
    let unit = f
        .user_unit
        .value
        .filter(|_| f.user_unit.status == "available")
        .ok_or("heading-spacing-physical-scale-unavailable")?;
    let lines = [
        measure_line(page, h_start, *heading_break)?,
        measure_line(page, heading_break + 2, h_end)?,
        measure_line(page, p_start, *first_body_break)?,
        measure_line(page, first_body_break + 2, *second_body_break)?,
        measure_line(page, second_body_break + 2, p_end)?,
    ];
    let left = lines
        .iter()
        .map(|line| line.bounds[0] + line.error)
        .fold(f64::NEG_INFINITY, f64::max);
    let right = lines
        .iter()
        .map(|line| line.bounds[2] - line.error)
        .fold(f64::INFINITY, f64::min);
    let top = lines[0].bounds[3] + lines[0].error;
    let bottom = lines[4].bounds[1] - lines[4].error;
    // Reject conflicting text in the vertical band even when its native text
    // index occurs outside the selected range, as in interleaved columns.
    if left >= right
        || lines.windows(2).any(|pair| {
            pair[0].baseline <= pair[1].baseline
                || pair[0].bounds[1] - pair[0].error <= pair[1].bounds[3] + pair[1].error
        })
        || chars[..h_start].iter().chain(&chars[p_end..]).any(|c| {
            let separator = c.ink_status == "missing"
                && c.ink_box.is_none()
                && char::from_u32(c.unicode).is_some_and(char::is_whitespace);
            if separator {
                return false;
            }
            let measured_ink = c.ink_box.zip(c.ink_uncertainty.as_ref());
            c.origin.is_none_or(|[_, y]| (bottom..=top).contains(&y))
                || measured_ink.is_none_or(|(bounds, uncertainty)| {
                    bounds[1] - uncertainty.absolute_error_raw <= top
                        && bounds[3] + uncertainty.absolute_error_raw >= bottom
                })
        })
    {
        return Err(GEOMETRY);
    }
    let heading_interval = interval(page, unit, &lines[0], &lines[1]);
    let body_first = interval(page, unit, &lines[2], &lines[3]);
    let body_second = interval(page, unit, &lines[3], &lines[4]);
    if [&heading_interval, &body_first, &body_second]
        .iter()
        .any(|gap| gap.mm <= gap.error)
        || body_first.disjoint(&body_second)
    {
        return Err(REFERENCE);
    }
    let differs = heading_interval.disjoint(&body_first) && heading_interval.disjoint(&body_second);
    Ok((
        differs,
        json!({
            "pageIndex":f.page_index,"characterStart":h_start,"characterEnd":h_end,
            "rawBox":[lines[0].bounds[0].min(lines[1].bounds[0]),lines[1].bounds[1],
                lines[0].bounds[2].max(lines[1].bounds[2]),lines[0].bounds[3]],
            "measured":heading_interval.mm,"expected":"same observed baseline interval as adjacent body text","unit":"mm",
            "method":"confirmed-heading-body-baseline-interval",
            "uncertainty":{"absoluteError":heading_interval.error,"method":"native-outline-and-float32-bound","limitations":EVIDENCE_LIMITATIONS},
            "roleProvenance":{"annotationId":heading.annotation_id,"role":heading.role,"provenance":"authorLabel","certainty":"confirmedObservation"},
            "sourceLocation":null,"observation":{
                "kind":"confirmed-heading-body-baseline-comparison",
                "bodyAnnotationId":paragraph.annotation_id,"bodyCharacterStart":p_start,"bodyCharacterEnd":p_end,
                "bodyIntervals":[{"measuredMm":body_first.mm,"absoluteErrorMm":body_first.error},
                    {"measuredMm":body_second.mm,"absoluteErrorMm":body_second.error}],
                "comparison":if differs { "outsideBothBodyIntervals" } else { "noSeparationEstablished" },
            }
        }),
    ))
}
