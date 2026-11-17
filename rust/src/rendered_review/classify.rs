//! Conservative measured horizontal runs. Role labels scope observations; they
//! never establish body/paragraph semantics or whole-page paint coverage.
use super::dto::{Annotation, Character, Page};

pub const MAX_CHARACTERS: usize = 8192;
pub const MAX_LINES: usize = 256;

pub struct Run {
    pub baseline: f64,
    pub bounds: [f64; 4],
    pub error: f64,
    pub count: usize,
    first_index: usize,
    last_index: usize,
    gaps: Vec<[f64; 2]>,
}
pub struct Measurement {
    pub bounds: [f64; 4],
    pub error: f64,
    pub lines: Vec<Run>,
    pub characters: usize,
}
fn roundoff(value: f64) -> f64 {
    value.abs() * f32::EPSILON as f64 + f64::EPSILON * 16.0
}
fn join(a: [f64; 4], b: [f64; 4]) -> [f64; 4] {
    [
        a[0].min(b[0]),
        a[1].min(b[1]),
        a[2].max(b[2]),
        a[3].max(b[3]),
    ]
}
fn whitespace(ch: &Character) -> bool {
    char::from_u32(ch.unicode).is_some_and(char::is_whitespace)
}
pub fn measure(page: &Page, annotation: &Annotation) -> Result<Measurement, &'static str> {
    let f = &page.facts;
    if page.capabilities.schema_version != 2 {
        return Err("glyph-ink-uncertainty-unavailable");
    }
    if f.rotation != 0 {
        return Err("metric-horizontal-frame-required");
    }
    let start = annotation.character_start.unwrap_or(0);
    let end = annotation.character_end.unwrap_or(f.characters.len());
    if end - start > MAX_CHARACTERS {
        return Err("metric-classification-quota");
    }
    // A PDF may map a painted glyph to a whitespace scalar. Its observed ink
    // cannot be silently omitted on the basis of ToUnicode/ActualText alone.
    if f.characters[start..end]
        .iter()
        .any(|ch| whitespace(ch) && (ch.ink_status != "missing" || ch.ink_box.is_some()))
    {
        return Err("metric-painted-whitespace-ambiguous");
    }
    let mut chars: Vec<_> = f.characters[start..end]
        .iter()
        .filter(|c| !whitespace(c))
        .collect();
    if chars.is_empty() {
        return Err("metric-no-measured-glyphs");
    }
    for ch in &chars {
        let (Some(origin), Some(matrix), Some(bounds), Some(uncertainty)) = (
            ch.origin,
            ch.matrix,
            ch.ink_box,
            ch.ink_uncertainty.as_ref(),
        ) else {
            return Err("metric-incomplete-glyph-coverage");
        };
        // Numeric roundoff is a bound on the observed matrix, not an angle,
        // typography or guideline tolerance. No rotated/sheared/reversed runs.
        if matrix[0] <= 0.0
            || matrix[3] <= 0.0
            || matrix[1].abs() > roundoff(matrix[0])
            || matrix[2].abs() > roundoff(matrix[3])
            || ch.ink_status != "available"
            || ch.render_mode != Some(0)
            || bounds[0] >= bounds[2]
            || bounds[1] >= bounds[3]
            || uncertainty.absolute_error_raw > (bounds[2] - bounds[0]).min(bounds[3] - bounds[1])
            || !origin.iter().all(|n| n.is_finite())
        {
            return Err("metric-horizontal-frame-required");
        }
    }
    // Native character indexes are not reading order. Sort actual baselines and
    // positions, then require that they also describe contiguous native runs.
    chars.sort_by(|a, b| {
        b.origin.unwrap()[1]
            .total_cmp(&a.origin.unwrap()[1])
            .then_with(|| a.origin.unwrap()[0].total_cmp(&b.origin.unwrap()[0]))
    });
    let mut grouped: Vec<Vec<&Character>> = Vec::new();
    for ch in chars {
        let y = ch.origin.unwrap()[1];
        if let Some(line) = grouped.last_mut() {
            let first = line[0].origin.unwrap()[1];
            if (first - y).abs() <= roundoff(first) + roundoff(y) {
                line.push(ch);
                continue;
            }
        }
        if grouped.len() == MAX_LINES {
            return Err("metric-classification-quota");
        }
        grouped.push(vec![ch]);
    }
    let mut lines = Vec::new();
    let mut selected_count = 0;
    for mut line in grouped {
        line.sort_by(|a, b| a.origin.unwrap()[0].total_cmp(&b.origin.unwrap()[0]));
        let first = line[0];
        let mut bounds = first.ink_box.unwrap();
        let mut error = first.ink_uncertainty.as_ref().unwrap().absolute_error_raw;
        let mut gaps = Vec::new();
        for (i, ch) in line.iter().enumerate() {
            let b = ch.ink_box.unwrap();
            let e = ch.ink_uncertainty.as_ref().unwrap().absolute_error_raw;
            bounds = join(bounds, b);
            error = error.max(e);
            if i > 0 {
                let before = line[i - 1];
                let prior = before.ink_box.unwrap();
                let pe = before.ink_uncertainty.as_ref().unwrap().absolute_error_raw;
                if before.index >= ch.index || prior[2] - pe > b[0] + e {
                    return Err("metric-ambiguous-line-order");
                }
                // Interior empty corridors crossing every line can be columns
                // or aligned word gaps. Either remains indeterminate, with no
                // heuristic font-size/spacing threshold that guesses a column.
                if i >= 2 && i + 1 < line.len() && prior[2] + pe < b[0] - e {
                    gaps.push([prior[2] + pe, b[0] - e]);
                }
            }
        }
        let first_index = first.index;
        let last_index = line.last().unwrap().index;
        if f.characters[first_index..=last_index]
            .iter()
            .filter(|c| !whitespace(c))
            .count()
            != line.len()
        {
            return Err("metric-ambiguous-line-order");
        }
        selected_count += line.len();
        lines.push(Run {
            baseline: first.origin.unwrap()[1],
            bounds,
            error,
            count: line.len(),
            first_index,
            last_index,
            gaps,
        });
    }
    for pair in lines.windows(2) {
        if pair[0].last_index >= pair[1].first_index
            || pair[0].bounds[1] + pair[0].error < pair[1].bounds[3] - pair[1].error
        {
            return Err("metric-ambiguous-line-order");
        }
    }
    if lines.len() > 1 {
        let mut common = lines[0].gaps.clone();
        for line in &lines[1..] {
            let mut next = Vec::new();
            let (mut i, mut j) = (0, 0);
            while i < common.len() && j < line.gaps.len() {
                let l = common[i][0].max(line.gaps[j][0]);
                let r = common[i][1].min(line.gaps[j][1]);
                if l < r {
                    next.push([l, r]);
                }
                if common[i][1] < line.gaps[j][1] {
                    i += 1;
                } else {
                    j += 1;
                }
            }
            common = next;
        }
        if !common.is_empty() {
            return Err("metric-ambiguous-columns");
        }
    }
    let mut bounds = lines[0].bounds;
    let mut error: f64 = 0.0;
    for line in &lines {
        bounds = join(bounds, line.bounds);
        error = error.max(line.error);
    }
    Ok(Measurement {
        bounds,
        error,
        lines,
        characters: selected_count,
    })
}

impl Measurement {
    pub fn indent(&self) -> Result<(f64, f64), &'static str> {
        if self.lines.len() < 2 || self.lines.iter().any(|l| l.count < 2) {
            return Err("metric-continuation-not-established");
        }
        let continuation = &self.lines[1];
        if self.lines[0].bounds[2] + self.lines[0].error
            < continuation.bounds[0] - continuation.error
            || continuation.bounds[2] + continuation.error
                < self.lines[0].bounds[0] - self.lines[0].error
        {
            return Err("metric-continuation-not-established");
        }
        let mut lower = continuation.bounds[0] - continuation.error;
        let mut upper = continuation.bounds[0] + continuation.error;
        for line in &self.lines[2..] {
            lower = lower.max(line.bounds[0] - line.error);
            upper = upper.min(line.bounds[0] + line.error);
        }
        if lower > upper {
            return Err("metric-continuation-not-established");
        }
        Ok((
            self.lines[0].bounds[0] - continuation.bounds[0],
            self.lines[0].error + continuation.error,
        ))
    }
}

/// Convert independent native outline and PDF float32/scale roundoff bounds to
/// the measured millimetre value. This does not enlarge guideline thresholds.
pub fn physical_error(page: &Page, raw_value: f64, raw_error: f64, coordinates: &[f64]) -> f64 {
    let unit = page.facts.user_unit.value.unwrap();
    let scale_error = if page.facts.user_unit.method == "default-userunit" {
        0.0
    } else {
        roundoff(unit)
    };
    let raw_roundoff: f64 = coordinates.iter().map(|v| roundoff(*v)).sum();
    ((raw_error + raw_roundoff) * unit.abs()
        + (raw_value.abs() + raw_error + raw_roundoff) * scale_error)
        * 25.4
        / 72.0
        + f64::EPSILON * 1000.0
}
