//! KNU §3.2 prohibits a heading on the last text line. This check requires the
//! author to confirm page-text coverage independently of the selected heading.
use super::{
    classify,
    dto::{Character, Page, Request},
};
use serde_json::{json, Value};

#[cfg(test)]
mod tests;

pub(super) const PROFILE_VERSION: &str = "1.5.0";
pub(super) const LIMITATIONS: &[&str] = &[
    "minor-heading-and-page-text-coverage-author-confirmed",
    "one-upright-single-column-page",
    "heading-spacing-and-lower-page-region-unassessed",
    "unselected-pages-unassessed",
];
const EVIDENCE_LIMITATIONS: &[&str] = &[
    "native-extraction-does-not-establish-page-text-coverage",
    "text-line-order-conditional-on-author-confirmation",
];
const COVERAGE: &str = "minor-heading-coverage-unestablished";
const ORDER: &str = "minor-heading-page-order-unestablished";
const REQUIRED: &str = "confirmed-minor-heading-range-required";

pub(super) fn evaluate(request: &Request, page: &Page) -> (&'static str, &'static str, Vec<Value>) {
    match observe(request, page) {
        Ok((true, evidence)) => (
            "fail",
            "confirmed-minor-heading-is-final-page-text-line",
            vec![evidence],
        ),
        Ok((false, evidence)) => (
            "indeterminate",
            "conditional-minor-heading-has-following-page-text",
            vec![evidence],
        ),
        Err(reason) => ("indeterminate", reason, Vec::new()),
    }
}

fn line_break(pair: &[Character]) -> bool {
    pair[0].unicode == 13
        && pair[1].unicode == 10
        && pair.iter().all(|c| {
            c.ink_status == "missing"
                && c.ink_box.is_none()
                && c.advance.is_none()
                && c.render_mode.is_none()
        })
}

fn ranges(page: &Page) -> Result<Vec<(usize, usize)>, &'static str> {
    let chars = &page.facts.characters;
    if chars.len() > classify::MAX_CHARACTERS
        || page.capabilities.schema_version != 2
        || page.facts.rotation != 0
    {
        return Err(COVERAGE);
    }
    let ends = chars
        .windows(2)
        .enumerate()
        .filter(|(_, pair)| line_break(pair))
        .map(|(index, _)| index)
        .chain(std::iter::once(chars.len()));
    let mut lines = Vec::new();
    let mut start = 0;
    for end in ends {
        if end <= start
            || lines.len() == classify::MAX_LINES
            || chars[start].unicode == 32
            || chars[end - 1].unicode == 32
            || chars[start..end].iter().any(|c| {
                c.unicode != 32
                    && char::from_u32(c.unicode).is_none_or(|scalar| {
                        scalar.is_control() || scalar.is_whitespace() || scalar == '\u{fffd}'
                    })
            })
        {
            return Err(COVERAGE);
        }
        lines.push((start, end));
        start = end + 2;
    }
    if lines.len() < 2 {
        return Err(COVERAGE);
    }
    Ok(lines)
}

fn observe(request: &Request, page: &Page) -> Result<(bool, Value), &'static str> {
    let mut labels =
        request.payload.annotations.iter().filter(|label| {
            label.page_index == page.facts.page_index && label.role == "headingMinor"
        });
    let heading = labels.next().ok_or(REQUIRED)?;
    if labels.next().is_some() {
        return Err("ambiguous-minor-heading-ranges");
    }
    let (Some(start), Some(end)) = (heading.character_start, heading.character_end) else {
        return Err(REQUIRED);
    };
    if heading.provenance != "authorLabel" || heading.certainty != "confirmedObservation" {
        return Err(REQUIRED);
    }
    let ranges = ranges(page)?;
    let heading_index = ranges
        .iter()
        .position(|line| *line == (start, end))
        .ok_or("minor-heading-not-complete-single-line")?;

    let frame = page.facts.effective;
    let [fl, fb, fr, ft] = frame;
    let mut lines = Vec::with_capacity(ranges.len());
    let mut left = fl;
    let mut right = fr;
    for &(from, to) in &ranges {
        let ink = classify::measure_range(page, from, to).map_err(|_| COVERAGE)?;
        let [l, b, r, t] = ink.bounds;
        let e = ink.error;
        if ink.lines.len() != 1 || e <= 0.0 || l - e < fl || b - e < fb || r + e > fr || t + e > ft
        {
            return Err(COVERAGE);
        }
        left = left.max(l + e);
        right = right.min(r - e);
        lines.push(ink);
    }
    if right <= left
        || lines
            .windows(2)
            .any(|pair| pair[0].bounds[1] - pair[0].error <= pair[1].bounds[3] + pair[1].error)
    {
        return Err(ORDER);
    }

    let following = lines.len() - heading_index - 1;
    let final_line = following == 0;
    let neighbor_index = if final_line {
        heading_index - 1
    } else {
        heading_index + 1
    };
    let heading_ink = &lines[heading_index];
    let neighbor_ink = &lines[neighbor_index];
    let (neighbor_start, neighbor_end) = ranges[neighbor_index];
    Ok((
        final_line,
        json!({
            "pageIndex": page.facts.page_index, "characterStart": start, "characterEnd": end,
            "rawBox": heading_ink.bounds, "measured": following, "expected": null, "unit": "lines",
            "method": "confirmed-minor-heading-complete-native-text",
            "uncertainty": {
                "absoluteError": null, "method": "text-line-count-with-native-ink-bound",
                "limitations": EVIDENCE_LIMITATIONS,
            },
            "roleProvenance": {
                "annotationId": heading.annotation_id, "role": "headingMinor", "provenance": "authorLabel",
                "certainty": "confirmedObservation",
            },
            "sourceLocation": null,
            "observation": {
                "kind": "confirmed-minor-heading-page-end", "authorConfirmedPageTextCoverage": true,
                "textLineCount": lines.len(), "headingLineIndex": heading_index, "effectiveBox": frame,
                "headingInkErrorRaw": heading_ink.error,
                "neighbor": {
                    "side": if final_line { "preceding" } else { "following" },
                    "characterStart": neighbor_start, "characterEnd": neighbor_end, "rawBox": neighbor_ink.bounds,
                    "absoluteInkErrorRaw": neighbor_ink.error,
                },
            },
        }),
    ))
}
