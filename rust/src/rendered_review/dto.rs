use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashSet;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Request {
    pub protocol_version: u32,
    pub request_id: String,
    pub operation: String,
    pub payload: Payload,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Payload {
    pub pdf_sha256: String,
    pub compile_request_id: String,
    pub compile_epoch: u64,
    pub input_digest: String,
    pub capture_id: String,
    pub profile: Profile,
    pub work_type: String,
    pub annotation_version: u64,
    pub page_count: u16,
    pub requested_pages: Vec<u16>,
    pub annotations: Vec<Annotation>,
    pub facts_sha256: String,
    pub caption_image_pair: Option<super::caption_placement::Pair>,
}
#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Profile {
    pub id: String,
    pub version: String,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Annotation {
    pub annotation_id: u64,
    pub role: String,
    pub page_index: u16,
    pub character_start: Option<usize>,
    pub character_end: Option<usize>,
    pub source_file_id: Option<String>,
    pub source_span: Option<Span>,
    pub provenance: String,
    pub certainty: String,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Span {
    pub start_byte: u64,
    pub end_byte: u64,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FactBatch {
    pub schema_version: u32,
    pub document_sha256: String,
    pub pages: Vec<Page>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Page {
    pub capabilities: Capabilities,
    pub facts: Facts,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Capabilities {
    pub schema_version: u32,
    pub coordinate_space: String,
    pub physical_scale: String,
    pub glyph_ink: String,
    pub text_style: String,
    pub line_grouping: String,
    pub max_characters: usize,
    pub nominal_size_unit: String,
    pub nominal_size_method: String,
    #[serde(default)]
    pub glyph_ink_method: Option<String>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Facts {
    pub schema_version: u32,
    pub page_index: u16,
    pub page_count: u16,
    pub media: [f64; 4],
    pub crop: Option<[f64; 4]>,
    pub effective: [f64; 4],
    pub rotation: u16,
    pub user_unit: UserUnit,
    pub characters: Vec<Character>,
    pub complete: bool,
    pub limitations: Vec<String>,
}
#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct UserUnit {
    pub status: String,
    pub value: Option<f64>,
    pub method: String,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Character {
    pub index: usize,
    pub unicode: u32,
    pub origin: Option<[f64; 2]>,
    pub advance: Option<[f64; 2]>,
    pub matrix: Option<[f64; 6]>,
    pub nominal_size: Option<f64>,
    pub font_name: Option<String>,
    pub font_flags: Option<u32>,
    pub render_mode: Option<i32>,
    pub ink_box: Option<[f64; 4]>,
    #[serde(default)]
    pub ink_uncertainty: Option<InkUncertainty>,
    pub ink_status: String,
    pub style_status: String,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InkUncertainty {
    pub absolute_error_raw: f64,
    pub method: String,
}
pub fn hex(s: &str, n: usize) -> bool {
    s.len() == n
        && s.bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}
pub(super) fn number(n: f64) -> bool {
    n.is_finite() && n.abs() <= 1_000_000.0
}
fn rect(a: &[f64; 4]) -> bool {
    a.iter().copied().all(number) && a[0] <= a[2] && a[1] <= a[3]
}
fn state(s: &str) -> bool {
    matches!(s, "available" | "unsupported" | "indeterminate" | "missing")
}
fn capability(s: &str) -> bool {
    matches!(s, "available" | "unsupported" | "indeterminate")
}
fn code(s: &str) -> bool {
    !s.is_empty() && s.len() <= 64 && s.bytes().all(|b| b.is_ascii_graphic())
}
impl Request {
    pub fn validate(&self, batch: &FactBatch) -> Result<(), &'static str> {
        self.validate_profile(batch, "1.0.0")
    }
    pub(super) fn validate_profile(
        &self,
        batch: &FactBatch,
        version: &str,
    ) -> Result<(), &'static str> {
        let p = &self.payload;
        if self.protocol_version != 1
            || self.operation != "evaluateRenderedReview"
            || !hex(&self.request_id, 32)
            || !hex(&p.pdf_sha256, 64)
            || !hex(&p.input_digest, 64)
            || !hex(&p.facts_sha256, 64)
            || !hex(&p.compile_request_id, 32)
            || p.compile_epoch == 0
            || p.compile_epoch > 9_007_199_254_740_991
            || p.annotation_version > 9_007_199_254_740_991
            || p.capture_id.len() != 36
            || !p.capture_id.bytes().enumerate().all(|(i, b)| {
                if [8, 13, 18, 23].contains(&i) {
                    b == b'-'
                } else {
                    b.is_ascii_hexdigit()
                }
            })
            || p.profile.id != "knu-csc-2025-rendered-review"
            || p.profile.version != version
            || !matches!(
                p.work_type.as_str(),
                "unspecified" | "coursework" | "bachelorQualification" | "masterQualification"
            )
            || p.page_count == 0
            || p.page_count > 512
            || p.requested_pages.is_empty()
            || p.requested_pages.len() > 8
            || p.requested_pages.iter().any(|i| *i >= p.page_count)
            || p.requested_pages.windows(2).any(|w| w[0] >= w[1])
        {
            return Err("InvalidInput");
        }
        if batch.schema_version != 1
            || batch.document_sha256 != p.pdf_sha256
            || batch.pages.len() != p.requested_pages.len()
        {
            return Err("InvalidFacts");
        }
        for (page, index) in batch.pages.iter().zip(&p.requested_pages) {
            page.validate(*index, p.page_count)?;
        }
        if p.annotations.len() > 512 {
            return Err("InvalidAnnotations");
        }
        let mut ids = HashSet::new();
        for a in &p.annotations {
            if a.annotation_id == 0
                || a.annotation_id > 9_007_199_254_740_991
                || !ids.insert(a.annotation_id)
                || a.page_index >= p.page_count
                || !matches!(
                    a.role.as_str(),
                    "title"
                        | "abstract"
                        | "contents"
                        | "introduction"
                        | "body"
                        | "conclusions"
                        | "references"
                        | "appendix"
                        | "headingMajor"
                        | "headingMinor"
                        | "paragraph"
                        | "pageNumber"
                        | "figureCaption"
                        | "tableCaption"
                        | "footnote"
                        | "note"
                        | "illustration"
                )
                || !matches!(
                    a.provenance.as_str(),
                    "authorLabel" | "literalSourceAndNativeMap" | "validatedNativeClassification"
                )
                || !matches!(a.certainty.as_str(), "confirmedObservation" | "tentative")
                || a.source_file_id.is_some() != a.source_span.is_some()
                || a.source_file_id
                    .as_ref()
                    .is_some_and(|s| s.is_empty() || s.len() > 128)
                || a.source_span
                    .as_ref()
                    .is_some_and(|s| s.start_byte > s.end_byte)
                || a.provenance == "authorLabel" && a.source_file_id.is_some()
            {
                return Err("InvalidAnnotations");
            }
            match (a.character_start, a.character_end) {
                (None, None) => {}
                (Some(start), Some(end)) if start < end => {
                    if let Some(page) = batch
                        .pages
                        .iter()
                        .find(|page| page.facts.page_index == a.page_index)
                    {
                        if end > page.facts.characters.len() {
                            return Err("InvalidAnnotations");
                        }
                    } else {
                        return Err("InvalidAnnotations");
                    }
                }
                _ => return Err("InvalidAnnotations"),
            }
        }
        // Whole-page semantic roles must not overlap. Body may contain a
        // heading/paragraph/illustration; incompatible page purposes are refused.
        for (i, a) in p.annotations.iter().enumerate() {
            for b in &p.annotations[i + 1..] {
                if a.page_index != b.page_index {
                    continue;
                }
                let overlap = match (
                    a.character_start,
                    a.character_end,
                    b.character_start,
                    b.character_end,
                ) {
                    (Some(s), Some(e), Some(t), Some(u)) => s < u && t < e,
                    _ => true,
                };
                let containment = matches!(a.role.as_str(), "body" | "appendix")
                    && matches!(
                        b.role.as_str(),
                        "headingMajor"
                            | "headingMinor"
                            | "paragraph"
                            | "pageNumber"
                            | "illustration"
                            | "figureCaption"
                            | "tableCaption"
                            | "footnote"
                            | "note"
                    )
                    || matches!(b.role.as_str(), "body" | "appendix")
                        && matches!(
                            a.role.as_str(),
                            "headingMajor"
                                | "headingMinor"
                                | "paragraph"
                                | "pageNumber"
                                | "illustration"
                                | "figureCaption"
                                | "tableCaption"
                                | "footnote"
                                | "note"
                        );
                let title_number = version == super::pagination::PROFILE_VERSION
                    && super::pagination::title_number_pair(a, b);
                if overlap && !containment && !title_number {
                    return Err("InvalidAnnotations");
                }
            }
        }
        Ok(())
    }
}
impl Page {
    fn validate(&self, index: u16, pages: u16) -> Result<(), &'static str> {
        let c = &self.capabilities;
        let f = &self.facts;
        if !matches!(c.schema_version, 1 | 2)
            || c.coordinate_space != "pdf-raw-unrotated-bottom-left"
            || !capability(&c.physical_scale)
            || !capability(&c.glyph_ink)
            || !capability(&c.text_style)
            || c.line_grouping != "unsupported"
            || c.max_characters != 100000
            || c.nominal_size_unit != "raw-user-units"
            || c.nominal_size_method != "pdfium-font-size"
            || (c.schema_version == 1 && c.glyph_ink_method.is_some())
            || (c.schema_version == 2
                && c.glyph_ink_method.as_deref()
                    != Some("embedded-static-font-outline-opaque-fill"))
            || f.schema_version != c.schema_version
            || f.page_index != index
            || f.page_count != pages
            || !f.complete
            || !matches!(f.rotation, 0 | 90 | 180 | 270)
            || !rect(&f.media)
            || !rect(&f.effective)
            || f.crop.as_ref().is_some_and(|b| !rect(b))
            || f.characters.len() > 100000
            || f.limitations.len() > 32
            || f.limitations.iter().any(|s| !code(s))
        {
            return Err("InvalidFacts");
        }
        let crop = f.crop.unwrap_or(f.media);
        if f.effective
            != [
                f.media[0].max(crop[0]),
                f.media[1].max(crop[1]),
                f.media[2].min(crop[2]),
                f.media[3].min(crop[3]),
            ]
            || f.effective[0] >= f.effective[2]
            || f.effective[1] >= f.effective[3]
        {
            return Err("InvalidFacts");
        }
        let u = &f.user_unit;
        if !state(&u.status)
            || c.schema_version == 2
                && ((c.physical_scale == "available") != (u.status == "available")
                    || u.method == "default-userunit" && u.value != Some(1.0))
            || if u.status == "available" {
                c.physical_scale != "available"
                    || !u
                        .value
                        .is_some_and(|v| number(v) && v > 0.0 && v <= 75000.0)
                    || !matches!(
                        u.method.as_str(),
                        "page-dictionary-userunit" | "default-userunit"
                    )
            } else {
                u.value.is_some() || !matches!(u.method.as_str(), "unavailable" | "invalid")
            }
        {
            return Err("InvalidFacts");
        }
        for (i, ch) in f.characters.iter().enumerate() {
            if c.schema_version == 1 && ch.ink_uncertainty.is_some()
                || c.schema_version == 2
                    && ((ch.ink_status == "available") != ch.ink_uncertainty.is_some())
                || ch.ink_uncertainty.as_ref().is_some_and(|u| {
                    !number(u.absolute_error_raw)
                        || u.absolute_error_raw < 0.0
                        || u.method != "native-outline-fill-witness"
                })
            {
                return Err("InvalidFacts");
            }
            if ch.index != i
                || ch.unicode > 0x10ffff
                || (0xd800..=0xdfff).contains(&ch.unicode)
                || !state(&ch.ink_status)
                || !state(&ch.style_status)
                || ch
                    .origin
                    .as_ref()
                    .is_some_and(|p| !p.iter().copied().all(number))
                || ch
                    .advance
                    .as_ref()
                    .is_some_and(|p| !p.iter().copied().all(number))
                || ch
                    .matrix
                    .as_ref()
                    .is_some_and(|p| !p.iter().copied().all(number))
                || ch.nominal_size.is_some_and(|n| !number(n) || n < 0.0)
                || ch
                    .font_name
                    .as_ref()
                    .is_some_and(|s| s.len() > 256 || s.contains('\0'))
                || ch.render_mode.is_some_and(|m| !(0..=7).contains(&m))
                || ch.ink_box.as_ref().is_some_and(|b| !rect(b))
                || (ch.ink_status == "available") != ch.ink_box.is_some()
                || ch.ink_status == "available" && c.glyph_ink != "available"
                || ch.style_status == "available"
                    && (c.text_style != "available"
                        || ch.matrix.is_none()
                        || ch.nominal_size.is_none()
                        || ch.font_flags.is_none()
                        || ch.render_mode.is_none())
            {
                return Err("InvalidFacts");
            }
        }
        Ok(())
    }
}
pub fn object_shapes(request: &Value, facts: &Value) -> Result<(), &'static str> {
    if !request.is_object()
        || !request.get("payload").is_some_and(Value::is_object)
        || !request["payload"]["profile"].is_object()
        || !request["payload"]["annotations"]
            .as_array()
            .is_some_and(|a| {
                a.iter().all(|v| {
                    v.is_object() && (v["sourceSpan"].is_null() || v["sourceSpan"].is_object())
                })
            })
        || !facts.is_object()
        || !facts["pages"].as_array().is_some_and(|a| {
            a.iter().all(|p| {
                p.is_object()
                    && layout_version_shape(p)
                    && p["capabilities"].is_object()
                    && p["facts"].is_object()
                    && p["facts"]["userUnit"].is_object()
                    && p["facts"]["characters"]
                        .as_array()
                        .is_some_and(|a| a.iter().all(Value::is_object))
            })
        })
    {
        return Err("InvalidInput");
    }
    Ok(())
}

// The optional serde fields distinguish versions, not absent-vs-null wire
// fields. Require every native field explicitly, including v2 uncertainty:null.
pub(super) fn exact_keys(value: &Value, keys: &[&str]) -> bool {
    value
        .as_object()
        .is_some_and(|o| o.len() == keys.len() && keys.iter().all(|key| o.contains_key(*key)))
}

fn layout_version_shape(page: &Value) -> bool {
    let c = &page["capabilities"];
    let f = &page["facts"];
    let v = c["schemaVersion"].as_u64();
    let mut cap = vec![
        "schemaVersion",
        "coordinateSpace",
        "physicalScale",
        "glyphInk",
        "textStyle",
        "lineGrouping",
        "maxCharacters",
        "nominalSizeUnit",
        "nominalSizeMethod",
    ];
    let mut character = vec![
        "index",
        "unicode",
        "origin",
        "advance",
        "matrix",
        "nominalSize",
        "fontName",
        "fontFlags",
        "renderMode",
        "inkBox",
        "inkStatus",
        "styleStatus",
    ];
    if v == Some(2) {
        cap.push("glyphInkMethod");
        character.push("inkUncertainty");
    }
    if !matches!(v, Some(1 | 2))
        || !exact_keys(c, &cap)
        || !exact_keys(
            f,
            &[
                "schemaVersion",
                "pageIndex",
                "pageCount",
                "media",
                "crop",
                "effective",
                "rotation",
                "userUnit",
                "characters",
                "complete",
                "limitations",
            ],
        )
        || !exact_keys(&f["userUnit"], &["status", "value", "method"])
    {
        return false;
    }
    f["characters"].as_array().is_some_and(|chars| {
        chars.iter().all(|ch| {
            exact_keys(ch, &character)
                && (v != Some(2)
                    || ch["inkUncertainty"].is_null()
                    || exact_keys(&ch["inkUncertainty"], &["absoluteErrorRaw", "method"]))
        })
    })
}
