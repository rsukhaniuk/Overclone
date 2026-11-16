//! Bounded, deterministic rendered observations. Native facts are supplied by
//! the isolated PDF worker; compile/revision authority is checked by C#.
//! This module performs no IO, fixes, macro execution or compliance certification.
pub mod catalog;
mod dto;
mod rules;
#[cfg(test)]
mod tests;

use dto::*;
use serde_json::{json, Value};

pub fn evaluate(request_json: &str, facts_json: &str) -> String {
    match run(request_json, facts_json) {
        Ok(value) => match serde_json::to_string(&value) {
            Ok(text) if text.len() <= 524_288 => text,
            _ => error("QuotaExceeded"),
        },
        Err(code) => error(code),
    }
}
fn error(code: &str) -> String {
    json!({"protocolVersion":1,"status":"failed","error":{"code":code}}).to_string()
}
fn run(metadata: &str, bytes: &str) -> Result<Value, &'static str> {
    if metadata.len() > 262_144 || bytes.len() > 4_194_304 {
        return Err("QuotaExceeded");
    }
    // Serde structs accept positional arrays; the wire only accepts objects.
    let shape: Value = serde_json::from_str(metadata).map_err(|_| "InvalidInput")?;
    let fact_shape: Value = serde_json::from_str(bytes).map_err(|_| "InvalidFacts")?;
    dto::object_shapes(&shape, &fact_shape)?;
    // deny_unknown_fields additionally rejects duplicate known fields, including
    // fields which Value parsing alone would silently overwrite.
    let request: Request = serde_json::from_str(metadata).map_err(|_| "InvalidInput")?;
    let facts: FactBatch = serde_json::from_str(bytes).map_err(|_| "InvalidFacts")?;
    request.validate(&facts)?;
    let findings = rules::evaluate(&request, &facts);
    Ok(
        json!({"protocolVersion":1,"requestId":request.request_id,"status":"completed","payload":{
            "pdfSha256":request.payload.pdf_sha256,"compileRequestId":request.payload.compile_request_id,
            "compileEpoch":request.payload.compile_epoch,"inputDigest":request.payload.input_digest,
            "captureId":request.payload.capture_id,"profile":request.payload.profile,
            "workType":request.payload.work_type,"annotationVersion":request.payload.annotation_version,
            "pageCount":request.payload.page_count,"requestedPages":request.payload.requested_pages,
            "factsSha256":request.payload.facts_sha256,"guidelineSha256":catalog::GUIDELINE_SHA256,
            "coverage":{"pagesEvaluated":facts.pages.len(),"complete":true,"standardsComplete":false},
            "results":findings,"limitations":["finite-catalog-not-compliance-certificate","unassessed-guideline-categories"]
        }}),
    )
}
