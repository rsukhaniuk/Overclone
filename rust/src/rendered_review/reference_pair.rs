//! Compare two author-confirmed printed numbers from one current PDF capture.
use super::{catalog, classify, dto::*};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

pub(super) const PROFILE_ID: &str = "knu-csc-2025-rendered-reference-pair";
const PROFILE_VERSION: &str = "1.0.0";
const MAX_SELECTION: usize = 32;

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(super) struct Pair {
    scope: String,
    renderer_loader_sha256: String,
    renderer_wasm_sha256: String,
    citation: Selection,
    entry: Selection,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Selection {
    selection_request_id: String,
    selection_epoch: u64,
    page_index: u16,
    character_start: usize,
    character_end: usize,
    literal: String,
}

impl Selection {
    fn valid(&self) -> bool {
        let count = self.literal.chars().count();
        hex(&self.selection_request_id, 32)
            && (1..=9_007_199_254_740_991).contains(&self.selection_epoch)
            && (1..=MAX_SELECTION).contains(&count)
            && self.character_end <= 100000
            && self.character_end.checked_sub(self.character_start) == Some(count)
            && !self.literal.chars().any(char::is_control)
    }

    fn number(&self, page: &Page) -> Result<u32, &'static str> {
        let glyphs = page
            .facts
            .characters
            .get(self.character_start..self.character_end)
            .ok_or("selection-glyph-mismatch")?;
        if !glyphs
            .iter()
            .map(|c| c.unicode)
            .eq(self.literal.chars().map(u32::from))
        {
            return Err("selection-glyph-mismatch");
        }
        let digits = self
            .literal
            .strip_prefix('[')
            .and_then(|s| s.strip_suffix(']'))
            .filter(|s| {
                (1..=9).contains(&s.len())
                    && !s.starts_with('0')
                    && s.bytes().all(|b| b.is_ascii_digit())
            })
            .ok_or("unsupported-printed-number")?;
        // The role semantics come from the two explicit PDF selection gestures.
        // Reuse the horizontal-run measurement with the exact confirmed range.
        let range = Annotation {
            annotation_id: 0,
            role: String::new(),
            page_index: self.page_index,
            character_start: Some(self.character_start),
            character_end: Some(self.character_end),
            source_file_id: None,
            source_span: None,
            provenance: String::new(),
            certainty: String::new(),
        };
        let measurement = classify::measure(page, &range).map_err(|_| "glyph-ink-unavailable")?;
        let [l, b, r, t] = measurement.bounds;
        let [pl, pb, pr, pt] = page.facts.effective;
        let error = measurement.error;
        if measurement.lines.len() != 1
            || measurement.characters != glyphs.len()
            || error <= 0.0
            || l - error < pl
            || b - error < pb
            || r + error > pr
            || t + error > pt
        {
            return Err("glyph-ink-unavailable");
        }
        digits.parse().map_err(|_| "unsupported-printed-number")
    }
}

impl Pair {
    fn compare(&self, facts: &FactBatch) -> Result<(u32, u32), &'static str> {
        let citation = &self.citation;
        let entry = &self.entry;
        if citation.page_index == entry.page_index
            && citation.character_start < entry.character_end
            && entry.character_start < citation.character_end
        {
            return Err("overlapping-selection-ranges");
        }
        let first = &facts.pages[0];
        let last = &facts.pages[facts.pages.len() - 1];
        let (citation_page, entry_page) = if first.facts.page_index == citation.page_index {
            (first, last)
        } else {
            (last, first)
        };
        Ok((citation.number(citation_page)?, entry.number(entry_page)?))
    }
}

pub(super) fn run(metadata: &str, bytes: &str, payload: &Value) -> Result<Value, &'static str> {
    let shape = &payload["printedReferencePair"];
    if payload.get("captionImagePair").is_some()
        || !shape.is_object()
        || !shape["citation"].is_object()
        || !shape["entry"].is_object()
    {
        return Err("InvalidInput");
    }
    let request: Request = serde_json::from_str(metadata).map_err(|_| "InvalidInput")?;
    let facts: FactBatch = serde_json::from_str(bytes).map_err(|_| "InvalidFacts")?;
    request.validate_identity(&facts, PROFILE_ID, PROFILE_VERSION)?;
    let p = &request.payload;
    let pair = p.printed_reference_pair.as_ref().ok_or("InvalidInput")?;
    let mut expected_pages = vec![pair.citation.page_index, pair.entry.page_index];
    expected_pages.sort_unstable();
    expected_pages.dedup();
    if pair.scope != "mainWork"
        || !hex(&pair.renderer_loader_sha256, 64)
        || !hex(&pair.renderer_wasm_sha256, 64)
        || !pair.citation.valid()
        || !pair.entry.valid()
        || p.requested_pages != expected_pages
        || !p.annotations.is_empty()
        || p.annotation_version != 0
    {
        return Err("InvalidInput");
    }
    let (comparison, reason, numbers) = match pair.compare(&facts) {
        Ok((citation, entry)) => (
            if citation == entry { "numbersMatch" } else { "numbersDiffer" },
            "confirmed-printed-number-pair",
            json!({"citation":citation,"entry":entry}),
        ),
        Err(reason) => ("indeterminate", reason, Value::Null),
    };
    Ok(json!({
        "protocolVersion":1,"requestId":request.request_id,"status":"completed","payload":{
            "pdfSha256":p.pdf_sha256,"compileRequestId":p.compile_request_id,"compileEpoch":p.compile_epoch,
            "inputDigest":p.input_digest,"captureId":p.capture_id,"profile":p.profile,"workType":p.work_type,
            "annotationVersion":p.annotation_version,"pageCount":p.page_count,"requestedPages":p.requested_pages,
            "factsSha256":p.facts_sha256,"guidelineSha256":catalog::GUIDELINE_SHA256,
            "printedReferencePair":pair,"comparison":comparison,"reason":reason,"numbers":numbers,
            "roleProvenance":"authorConfirmedPdfSearchSelections","sourceLocation":null,
            "limitations":["selected-printed-numbers-only","citation-key-and-bibliography-completeness-unassessed"]
        }
    }))
}

#[cfg(test)]
mod tests;
