//! Conditional geometry for an explicit caption annotation and direct image row.
//! The browser inspector owns same-call capture authority. Here we revalidate
//! the finite native schemas, shared page frame, selection, and measurement.
use super::classify;
use super::dto::{self, Annotation, FactBatch, Page, Request};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

mod object_layout;
#[cfg(test)]
mod tests;

pub(super) const PROFILE_VERSION: &str = "1.1.0";
const MAX_SAFE_INTEGER: u64 = 9_007_199_254_740_991;
const LIMITATIONS: [&str; 6] = [
    "explicit-pair-only",
    "positive-gap-means-caption-above-image",
    "image-placement-not-ink",
    "image-visibility-not-established",
    "source-ctm-accumulation-error-unmeasured",
    "no-normative-outcome",
];

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(super) struct Pair {
    caption_annotation_id: u64,
    object_index: u16,
    inspection_request_id: String,
    inspection_epoch: u64,
    renderer_loader_sha256: String,
    renderer_wasm_sha256: String,
    layout_facts_sha256: String,
    object_facts_sha256: String,
}

impl Pair {
    fn validate(&self) -> Result<(), &'static str> {
        if !(1..=MAX_SAFE_INTEGER).contains(&self.caption_annotation_id)
            || !(1..=MAX_SAFE_INTEGER).contains(&self.inspection_epoch)
            || !dto::hex(&self.inspection_request_id, 32)
            || [
                &self.renderer_loader_sha256,
                &self.renderer_wasm_sha256,
                &self.layout_facts_sha256,
                &self.object_facts_sha256,
            ]
            .iter()
            .any(|hash| !dto::hex(hash, 64))
        {
            return Err("InvalidInput");
        }
        Ok(())
    }

    fn caption<'a>(
        &self,
        request: &'a Request,
        page: &Page,
    ) -> Result<&'a Annotation, &'static str> {
        request
            .payload
            .annotations
            .iter()
            .find(|a| {
                a.annotation_id == self.caption_annotation_id
                    && a.page_index == page.facts.page_index
                    && matches!(a.role.as_str(), "figureCaption" | "tableCaption")
                    && a.certainty == "confirmedObservation"
                    && a.character_start.is_some()
                    && a.character_end.is_some()
            })
            .ok_or("InvalidAnnotations")
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Batch {
    schema_version: u32,
    document_sha256: String,
    pages: Vec<Page>,
    object_layout: object_layout::ObjectLayout,
}

pub(super) fn run(
    metadata: &str,
    bytes: &str,
    payload: &Value,
    fact_shape: &Value,
) -> Result<Value, &'static str> {
    if !payload["captionImagePair"].is_object() {
        return Err("InvalidInput");
    }
    object_layout::validate_shape(&fact_shape["objectLayout"])?;
    let request: Request = serde_json::from_str(metadata).map_err(|_| "InvalidInput")?;
    let pair = request
        .payload
        .caption_image_pair
        .as_ref()
        .ok_or("InvalidInput")?;
    pair.validate()?;
    if request.payload.requested_pages.len() != 1 {
        return Err("InvalidInput");
    }
    let batch: Batch = serde_json::from_str(bytes).map_err(|_| "InvalidFacts")?;
    if batch.schema_version != 2 || batch.pages.len() != 1 {
        return Err("InvalidFacts");
    }
    let text_pages = FactBatch {
        schema_version: 1,
        document_sha256: batch.document_sha256,
        pages: batch.pages,
    };
    request.validate_profile(&text_pages, PROFILE_VERSION)?;
    let page = &text_pages.pages[0];
    let image = batch.object_layout.image(page, pair.object_index)?;
    let caption = pair.caption(&request, page)?;
    let observation = measure(page, caption, image);
    let (relation, reason, geometry) = match observation {
        Ok(geometry) => {
            let relation = if geometry.raw_signed_gap > 0.0 {
                "captionAbove"
            } else {
                "captionBelow"
            };
            (relation, "measured-separation", Some(geometry))
        }
        Err(reason) => ("indeterminate", reason, None),
    };
    let p = &request.payload;
    Ok(
        json!({"protocolVersion":1,"requestId":request.request_id,"status":"completed","payload":{
            "pdfSha256":p.pdf_sha256,"compileRequestId":p.compile_request_id,"compileEpoch":p.compile_epoch,
            "inputDigest":p.input_digest,"captureId":p.capture_id,"profile":p.profile,"workType":p.work_type,
            "annotationVersion":p.annotation_version,"pageCount":p.page_count,"requestedPages":p.requested_pages,
            "factsSha256":p.facts_sha256,"captionImagePair":pair,"pageIndex":page.facts.page_index,
            "relation":relation,"reason":reason,"geometry":geometry,
            "method":"conditional-native-caption-image-vertical-gap","limitations":LIMITATIONS,
        }}),
    )
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Geometry {
    caption_ink_box: Bounds,
    image_placement_box: Bounds,
    raw_signed_gap: f64,
    absolute_error_raw: f64,
    signed_gap_mm: f64,
    absolute_error_mm: f64,
}

#[derive(Serialize)]
struct Bounds {
    left: f64,
    bottom: f64,
    right: f64,
    top: f64,
}

impl From<[f64; 4]> for Bounds {
    fn from([left, bottom, right, top]: [f64; 4]) -> Self {
        Self {
            left,
            bottom,
            right,
            top,
        }
    }
}

fn within(frame: [f64; 4], [l, b, r, t]: [f64; 4], error: f64) -> bool {
    let [fl, fb, fr, ft] = frame;
    l - error >= fl && b - error >= fb && r + error <= fr && t + error <= ft
}

fn measure(
    page: &Page,
    caption: &Annotation,
    image: Option<object_layout::Placement>,
) -> Result<Geometry, &'static str> {
    if page.facts.rotation != 0 {
        return Err("unsupported-page-rotation");
    }
    let unit = page
        .facts
        .user_unit
        .value
        .ok_or("unavailable-physical-scale")?;
    let image = image.ok_or("unavailable-image-placement")?;
    let ink = classify::measure(page, caption).map_err(|_| "unavailable-caption-ink")?;
    if !within(page.facts.effective, ink.bounds, ink.error) {
        return Err("caption-ink-crosses-effective-page");
    }
    let [cl, cb, cr, ct] = ink.bounds;
    let [il, ib, ir, it] = image.bounds;
    // Both inner x intervals must share a nonempty interval even at the bound.
    let shared_left = (cl + ink.error).max(il + image.error);
    let shared_right = (cr - ink.error).min(ir - image.error);
    let horizontal_roundoff =
        edge_roundoff(cl, cr) + edge_roundoff(il, ir) + edge_roundoff(ink.error, image.error);
    if shared_right - shared_left <= horizontal_roundoff {
        return Err("horizontal-overlap-unestablished");
    }
    let (caption_edge, image_edge) = if cb > it {
        (cb, it)
    } else if ct < ib {
        (ct, ib)
    } else {
        return Err("vertical-separation-unestablished");
    };
    // Native error bounds are independent intervals. Use their sum, with
    // roundoff of the edge subtraction and error accumulation included.
    let raw_gap = caption_edge - image_edge;
    let raw_error = ink.error + image.error + edge_roundoff(caption_edge, image_edge);
    let gap_mm = raw_gap * unit * 25.4 / 72.0;
    let error_mm = classify::physical_error(page, raw_gap, raw_error, &[caption_edge, image_edge]);
    if raw_gap.abs() <= raw_error || gap_mm.abs() <= error_mm {
        return Err("vertical-separation-unestablished");
    }
    Ok(Geometry {
        caption_ink_box: ink.bounds.into(),
        image_placement_box: image.bounds.into(),
        raw_signed_gap: raw_gap,
        absolute_error_raw: raw_error,
        signed_gap_mm: gap_mm,
        absolute_error_mm: error_mm,
    })
}

fn edge_roundoff(first: f64, second: f64) -> f64 {
    8.0 * f64::EPSILON * (first.abs() + second.abs()).max(1.0)
}
