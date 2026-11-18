//! Strict reconstruction of reviewed native direct-page image placement schema 1.
use super::super::dto::{self, Page, UserUnit};
use super::within;
use serde::Deserialize;
use serde_json::Value;

const MAX_OBJECTS: u16 = 10_000;
const MAX_IMAGES: u16 = 512;
const SOURCE_CTM: &str = "source-ctm-accumulation-error-unmeasured";
const ROOT_LIMITATIONS: [&str; 9] = [
    "direct-page-objects-only",
    "native-parsed-inventory-not-content-validation",
    "image-placement-not-ink",
    "image-visibility-not-established",
    "object-role-unestablished",
    "caption-association-unestablished",
    "nested-forms-not-inspected",
    "clip-or-compositing-uncertain-refused",
    SOURCE_CTM,
];

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(super) struct ObjectLayout {
    schema_version: u32,
    coordinate_space: String,
    scope: String,
    page_index: u16,
    page_count: u16,
    media: [f64; 4],
    crop: Option<[f64; 4]>,
    effective: [f64; 4],
    rotation: u16,
    user_unit: UserUnit,
    direct_object_count: u16,
    object_type_counts: ObjectTypeCounts,
    images: Vec<Image>,
    scan_complete: bool,
    limitations: Vec<String>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ObjectTypeCounts {
    text: u16,
    path: u16,
    image: u16,
    shading: u16,
    form: u16,
    unknown: u16,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Image {
    object_index: u16,
    active: bool,
    status: String,
    method: String,
    matrix: Option<[f64; 6]>,
    quad: Option<[[f64; 2]; 4]>,
    raw_box: Option<[f64; 4]>,
    uncertainty: Option<Uncertainty>,
    reason: String,
    limitations: Vec<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Uncertainty {
    absolute_error_raw: f64,
    method: String,
}

pub(super) struct Placement {
    pub bounds: [f64; 4],
    pub error: f64,
}

// Nullable native fields must be present. Shape checks also reject positional
// arrays before serde can interpret them as named structs.
pub(super) fn validate_shape(layout: &Value) -> Result<(), &'static str> {
    use dto::exact_keys;
    if !exact_keys(
        layout,
        &[
            "schemaVersion",
            "coordinateSpace",
            "scope",
            "pageIndex",
            "pageCount",
            "media",
            "crop",
            "effective",
            "rotation",
            "userUnit",
            "directObjectCount",
            "objectTypeCounts",
            "images",
            "scanComplete",
            "limitations",
        ],
    ) || !exact_keys(&layout["userUnit"], &["status", "value", "method"])
        || !exact_keys(
            &layout["objectTypeCounts"],
            &["text", "path", "image", "shading", "form", "unknown"],
        )
        || !layout["images"].as_array().is_some_and(|rows| {
            rows.len() <= usize::from(MAX_IMAGES)
                && rows.iter().all(|row| {
                    exact_keys(
                        row,
                        &[
                            "objectIndex",
                            "active",
                            "status",
                            "method",
                            "matrix",
                            "quad",
                            "rawBox",
                            "uncertainty",
                            "reason",
                            "limitations",
                        ],
                    ) && (row["uncertainty"].is_null()
                        || exact_keys(&row["uncertainty"], &["absoluteErrorRaw", "method"]))
                })
        })
    {
        return Err("InvalidFacts");
    }
    Ok(())
}

impl ObjectLayout {
    pub(super) fn image(
        &self,
        page: &Page,
        selected: u16,
    ) -> Result<Option<Placement>, &'static str> {
        let text = &page.facts;
        let counts = &self.object_type_counts;
        let total: u32 = [
            counts.text,
            counts.path,
            counts.image,
            counts.shading,
            counts.form,
            counts.unknown,
        ]
        .into_iter()
        .map(u32::from)
        .sum();
        // The layout page has already validated its frame and numeric domain.
        // These native inventory observations must use exactly the same frame.
        if self.schema_version != 1
            || self.scope != "direct-page-image-placement"
            || self.coordinate_space != page.capabilities.coordinate_space
            || page.capabilities.schema_version != 2
            || self.page_index != text.page_index
            || self.page_count != text.page_count
            || self.media != text.media
            || self.crop != text.crop
            || self.effective != text.effective
            || self.rotation != text.rotation
            || self.user_unit != text.user_unit
            || !matches!(
                (
                    self.user_unit.status.as_str(),
                    self.user_unit.method.as_str()
                ),
                ("available", "default-userunit" | "page-dictionary-userunit")
                    | ("indeterminate", "invalid")
            )
            || !self.scan_complete
            || self.direct_object_count > MAX_OBJECTS
            || total != u32::from(self.direct_object_count)
            || self.images.len() != usize::from(counts.image)
            || self.limitations != ROOT_LIMITATIONS
        {
            return Err("InvalidFacts");
        }
        let mut result = None;
        let mut previous = None;
        for row in &self.images {
            if row.object_index >= self.direct_object_count
                || previous.is_some_and(|index| row.object_index <= index)
            {
                return Err("InvalidFacts");
            }
            previous = Some(row.object_index);
            let placement = row.placement(self.effective)?;
            if row.object_index == selected {
                result = Some(placement);
            }
        }
        result.ok_or("InvalidInput")
    }
}

impl Image {
    fn placement(&self, frame: [f64; 4]) -> Result<Option<Placement>, &'static str> {
        if self.method != "native-image-unit-square-placement" || self.limitations != [SOURCE_CTM] {
            return Err("InvalidFacts");
        }
        match (
            self.status.as_str(),
            self.active,
            self.reason.as_str(),
            self.matrix,
            self.quad,
            self.raw_box,
            &self.uncertainty,
        ) {
            (
                "available",
                true,
                "parsed-native-image-matrix",
                Some(m),
                Some(q),
                Some(b),
                Some(e),
            ) => reconstruct(m, q, b, e, frame).map(Some),
            ("indeterminate", false, "inactive-object", None, None, None, None)
            | (
                "indeterminate",
                true,
                "object-geometry-out-of-domain"
                | "invalid-image-dimensions"
                | "page-compositing-group"
                | "clip-path-present"
                | "optional-content-unresolved"
                | "image-mask-or-softmask"
                | "nondefault-compositing"
                | "invalid-object-matrix"
                | "crosses-effective-page",
                None,
                None,
                None,
                None,
            ) => Ok(None),
            _ => Err("InvalidFacts"),
        }
    }
}

fn reconstruct(
    matrix: [f64; 6],
    quad: [[f64; 2]; 4],
    raw_box: [f64; 4],
    uncertainty: &Uncertainty,
    frame: [f64; 4],
) -> Result<Placement, &'static str> {
    // The native values are float32. Recover that exact observation across
    // JSON decimal parsing before reproducing native double arithmetic.
    let native_matrix = matrix.map(|coordinate| f64::from(coordinate as f32));
    if !matches_numbers(matrix, native_matrix) {
        return Err("InvalidFacts");
    }
    let [a, b, c, d, e, f] = native_matrix;
    let determinant = a * d - b * c;
    let determinant_error =
        16.0 * f64::from(f32::EPSILON) * (a.abs() * d.abs() + b.abs() * c.abs()).max(1.0);
    if determinant.abs() <= determinant_error {
        return Err("InvalidFacts");
    }
    let expected_quad = [
        [e, f],
        [a + e, b + f],
        [a + c + e, b + d + f],
        [c + e, d + f],
    ];
    if !quad
        .into_iter()
        .zip(expected_quad)
        .all(|(given, expected)| matches_numbers(given, expected))
    {
        return Err("InvalidFacts");
    }
    let mut bounds = [e, f, e, f];
    for [x, y] in expected_quad {
        bounds = [
            bounds[0].min(x),
            bounds[1].min(y),
            bounds[2].max(x),
            bounds[3].max(y),
        ];
    }
    let magnitude = (a.abs() + c.abs() + e.abs())
        .max(b.abs() + d.abs() + f.abs())
        .max(1.0);
    let expected_error = (8.0 * f64::from(f32::EPSILON) + 16.0 * f64::EPSILON) * magnitude;
    let given_error = uncertainty.absolute_error_raw;
    if !matches_numbers(raw_box, bounds)
        || uncertainty.method != "pdfium-float32-matrix-bound"
        || !matches_numbers([given_error], [expected_error])
        || !within(frame, bounds, expected_error)
    {
        return Err("InvalidFacts");
    }
    Ok(Placement {
        bounds,
        error: expected_error.max(given_error),
    })
}

fn matches_numbers<const N: usize>(given: [f64; N], expected: [f64; N]) -> bool {
    given.into_iter().zip(expected).all(|(a, b)| {
        // Allow only double precision parsing roundoff of the serialized number.
        dto::number(a) && dto::number(b) && (a - b).abs() <= 4.0 * f64::EPSILON * b.abs()
    })
}
