//! Source analysis core: public wasm entry points and the canonical
//! metadata + packed-binary contract. Pure Rust, no filesystem/network/browser
//! APIs. The 5000 ms whole-job wall deadline and worker termination belong
//! to the later JS bridge (M2a-2); this core runs synchronously to
//! `completed` or `failed`.

pub mod dto;
pub mod facts;
pub mod knu_dto;
pub mod lexer;
pub mod mapping;
pub mod observations;
mod ownership;
pub mod profiles;
mod reference_wire;
pub mod references;
pub mod rules;

use dto::{
    AnalysisResult, AnalyzePayload, ErrorBody, FileDescriptor, InitializePayload, Limits,
    NewlineKind, Outcome, RequestEnvelope, ResolvedLimits, Response, SourceFacts, PROTOCOL_VERSION,
};
use lexer::{FileBudget, ScanResult};
use mapping::has_bom;

/// Hard defaults. Proposed guardrails for this slice — NOT measured
/// performance, NOT university rules, NOT a prerequisite on M0.
const D_MAX_BYTES_PER_FILE: u64 = 8 * 1024 * 1024;
const D_MAX_FILES: u32 = 5000;
const D_MAX_TOTAL_BYTES: u64 = 64 * 1024 * 1024;
const D_MAX_RESULT_BYTES: u64 = 4 * 1024 * 1024;
const D_MAX_TOKENS_PER_FILE: u32 = 20000;
const D_MAX_TOKENS_PER_PROJECT: u32 = 100000;
const D_MAX_DIAGNOSTICS_PER_PROJECT: u32 = 1000;
const D_MAX_BRACE_DEPTH: u32 = 256;

/// Engine version advertised to the bridge.
pub const ENGINE_VERSION: &str = "overclone-engine-m2b-1";

/// Bounded opaque identity IDs (requestId/snapshot/project/session/fileId,
/// profile id/version). Small fixture IDs stay valid; oversized IDs are
/// rejected before they can be copied into many diagnostics.
pub const MAX_ID_BYTES: usize = 128;

/// Bounded relative path (matches the workspace 1024-byte path maximum).
pub const MAX_PATH_BYTES: usize = 1024;

/// Resolve request limits: a request may LOWER a default but MUST NOT raise
/// the hard default. None ⇒ default.
fn resolve_limits(req: &Limits) -> ResolvedLimits {
    let mut r = ResolvedLimits {
        max_bytes_per_file: D_MAX_BYTES_PER_FILE,
        max_files: D_MAX_FILES,
        max_total_bytes: D_MAX_TOTAL_BYTES,
        max_result_bytes: D_MAX_RESULT_BYTES,
        max_tokens_per_file: D_MAX_TOKENS_PER_FILE,
        max_tokens_per_project: D_MAX_TOKENS_PER_PROJECT,
        max_diagnostics_per_project: D_MAX_DIAGNOSTICS_PER_PROJECT,
        max_brace_depth: D_MAX_BRACE_DEPTH,
    };
    if let Some(v) = req.max_bytes_per_file {
        r.max_bytes_per_file = r.max_bytes_per_file.min(v);
    }
    if let Some(v) = req.max_files {
        r.max_files = r.max_files.min(v);
    }
    if let Some(v) = req.max_total_bytes {
        r.max_total_bytes = r.max_total_bytes.min(v);
    }
    if let Some(v) = req.max_result_bytes {
        r.max_result_bytes = r.max_result_bytes.min(v);
    }
    if let Some(v) = req.max_tokens_per_file {
        r.max_tokens_per_file = r.max_tokens_per_file.min(v);
    }
    if let Some(v) = req.max_tokens_per_project {
        r.max_tokens_per_project = r.max_tokens_per_project.min(v);
    }
    if let Some(v) = req.max_diagnostics_per_project {
        r.max_diagnostics_per_project = r.max_diagnostics_per_project.min(v);
    }
    if let Some(v) = req.max_brace_depth {
        r.max_brace_depth = r.max_brace_depth.min(v);
    }
    r
}

// ---- canonical response builders ----

/// `Response` with `payload: serde_json::Value` is used only for the small
/// `initialize` payload (well under the result cap). The bounded analyze
/// payload is streamed via `CompletedResponse` to avoid an intermediate
/// `Value` allocation.
fn init_completed_response(request_id: Option<String>, payload: &InitializePayload) -> String {
    match serde_json::to_string(&Response {
        protocol_version: PROTOCOL_VERSION,
        request_id,
        status: "completed",
        payload: serde_json::to_value(payload).ok(),
        error: None,
    }) {
        Ok(s) => s,
        Err(_) => static_failed(None, "InternalError", "init serialization failed"),
    }
}

fn failed_response(
    request_id: Option<String>,
    code: &'static str,
    safe_message: impl Into<String>,
) -> String {
    let body = ErrorBody {
        code,
        safe_message: safe_message.into(),
        retryable: false,
    };
    serde_json::to_string(&Response {
        protocol_version: PROTOCOL_VERSION,
        request_id,
        status: "failed",
        payload: None,
        error: Some(body),
    })
    .unwrap_or_else(|_| static_minimal_failed(code, "serialization failed"))
}

/// Small canonical failed envelope for result-byte overflow. Never returns
/// truncated JSON, never builds an unbounded String.
fn quota_exceeded_result_response(request_id: Option<String>) -> String {
    failed_response(
        request_id,
        "QuotaExceeded",
        "analysis result exceeded the 4 MiB result-byte limit",
    )
}

/// Serialize an `AnalysisResult` through a bounded writer with an absolute
/// ceiling. On overflow return a small valid canonical `status:"failed"`
/// QuotaExceeded envelope; never return truncated JSON, never build an
/// unbounded `String` and check length afterward.
fn serialize_result_bounded(
    request_id: Option<String>,
    result: &AnalysisResult,
    max_result_bytes: u64,
) -> String {
    let max = max_result_bytes as usize;
    let mut writer = CappedWriter::new(max);
    let response = CompletedResponse {
        protocol_version: PROTOCOL_VERSION,
        request_id: request_id.clone(),
        status: "completed",
        payload: result,
    };
    match serde_json::to_writer(&mut writer, &response) {
        Ok(()) => writer.into_string(),
        Err(_) => quota_exceeded_result_response(request_id),
    }
}

/// A small, always-serializable minimal failed envelope used only as a last
/// resort if normal serialization itself fails.
fn static_minimal_failed(code: &'static str, safe_message: &str) -> String {
    serde_json::to_string(&serde_json::json!({
        "protocolVersion": 1,
        "status": "failed",
        "error": {"code": code, "safeMessage": safe_message, "retryable": false}
    }))
    .unwrap_or_else(|_| r#"{"protocolVersion":1,"status":"failed"}"#.to_string())
}

fn static_failed(_request_id: Option<String>, code: &'static str, safe_message: &str) -> String {
    // Intentionally omits requestId (cannot fabricate correlation under a
    // serialization failure); the bridge treats null requestId as a protocol
    // failure.
    static_minimal_failed(code, safe_message)
}

/// io::Write wrapper that refuses writes beyond a byte ceiling.
struct CappedWriter {
    buf: Vec<u8>,
    remaining: usize,
}

impl CappedWriter {
    fn new(cap: usize) -> Self {
        Self {
            buf: Vec::with_capacity(cap.min(64 * 1024)),
            remaining: cap,
        }
    }
    fn into_string(self) -> String {
        // serde_json writes valid UTF-8 JSON. On the (impossible) branch
        // where the bytes are not UTF-8, return ONLY a small valid canonical
        // envelope — never append partial/lossy output to a valid envelope
        // (that would yield invalid concatenated JSON).
        match String::from_utf8(self.buf) {
            Ok(s) => s,
            Err(_) => static_minimal_failed("InternalError", "non-utf8 serialization output"),
        }
    }
}

impl std::io::Write for CappedWriter {
    fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
        if bytes.len() > self.remaining {
            // Hard stop: do not partially accept the overflowing write.
            self.remaining = 0;
            return Err(std::io::Error::other("result-byte cap exceeded"));
        }
        self.buf.extend_from_slice(bytes);
        self.remaining -= bytes.len();
        Ok(bytes.len())
    }
    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}

/// Completed analyze response streaming the result payload directly (no
/// intermediate `serde_json::Value`), so the bounded writer is the real cap.
#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct CompletedResponse<'a> {
    #[serde(rename = "protocolVersion")]
    protocol_version: u32,
    #[serde(rename = "requestId", skip_serializing_if = "Option::is_none")]
    request_id: Option<String>,
    status: &'static str,
    payload: &'a AnalysisResult,
}

// ---- public wasm entry points ----

/// Advertise capabilities. `request_json` is the canonical initialize
/// envelope; only its `requestId` is echoed (no snapshot/profile on init).
pub fn initialize(request_json: &str) -> String {
    // Bound the metadata JSON before deserializing (same cap as analyzeSource).
    if request_json.len() > dto::MAX_METADATA_JSON_BYTES {
        return failed_response(
            None,
            "QuotaExceeded",
            "metadata JSON exceeded the 4 MiB limit",
        );
    }
    // Validate the requestId FIRST (nonempty string <= 128 bytes). A
    // missing/empty/oversize/non-string id yields a small InvalidInput
    // envelope with NO trusted requestId; a valid id is kept on every later
    // failure.
    let request_id = match recover_request_id(request_json) {
        Ok(id) => id,
        Err(msg) => return failed_response(None, "InvalidInput", msg),
    };
    match serde_json::from_str::<RequestEnvelope>(request_json) {
        Ok(env) if env.protocol_version == PROTOCOL_VERSION && env.operation == "initialize" => {
            let payload = InitializePayload {
                engine_version: ENGINE_VERSION,
                operations: &["analyzeSource"],
                limits: ResolvedLimits {
                    max_bytes_per_file: D_MAX_BYTES_PER_FILE,
                    max_files: D_MAX_FILES,
                    max_total_bytes: D_MAX_TOTAL_BYTES,
                    max_result_bytes: D_MAX_RESULT_BYTES,
                    max_tokens_per_file: D_MAX_TOKENS_PER_FILE,
                    max_tokens_per_project: D_MAX_TOKENS_PER_PROJECT,
                    max_diagnostics_per_project: D_MAX_DIAGNOSTICS_PER_PROJECT,
                    max_brace_depth: D_MAX_BRACE_DEPTH,
                },
            };
            init_completed_response(Some(request_id), &payload)
        }
        Ok(env) => {
            let code = if env.protocol_version != PROTOCOL_VERSION {
                "UnsupportedProtocol"
            } else {
                "UnsupportedCapability"
            };
            failed_response(Some(request_id), code, "unsupported initialize operation")
        }
        Err(_) => failed_response(
            Some(request_id),
            "InvalidInput",
            "malformed initialize envelope",
        ),
    }
}

/// The canonical source-analysis entry. `metadata_json` is the v1 request
/// envelope; `packed_bytes` is the single packed binary buffer the file
/// descriptors slice. Returns the canonical response as JSON text.
pub fn analyze_source(metadata_json: &str, packed_bytes: &[u8]) -> String {
    // Bound the metadata JSON before deserializing.
    if metadata_json.len() > dto::MAX_METADATA_JSON_BYTES {
        return failed_response(
            None,
            "QuotaExceeded",
            "metadata JSON exceeded the 4 MiB metadata limit",
        );
    }
    // Validate the requestId FIRST (nonempty string <= 128 bytes). A
    // missing/empty/oversize/non-string id yields a small InvalidInput
    // envelope with NO trusted requestId. Once a valid id is recovered it is
    // KEPT on every later failure (version/operation/snapshot/profile/file
    // identity/slice/limit), so a valid requestId plus a bad fileId/profile
    // echoes the original id.
    let request_id = match recover_request_id(metadata_json) {
        Ok(id) => id,
        Err(msg) => {
            return failed_response(None, "InvalidInput", msg);
        }
    };
    let env: RequestEnvelope = match serde_json::from_str(metadata_json) {
        Ok(e) => e,
        Err(_) => {
            return failed_response(
                Some(request_id),
                "InvalidInput",
                "malformed analyzeSource envelope",
            );
        }
    };
    if env.protocol_version != PROTOCOL_VERSION {
        return failed_response(
            Some(request_id),
            "UnsupportedProtocol",
            "unsupported protocolVersion",
        );
    }
    if env.operation != "analyzeSource" {
        return failed_response(
            Some(request_id),
            "UnsupportedCapability",
            "unsupported operation",
        );
    }
    let payload = env.payload;
    // Required identity (EXCLUDING requestId, already validated): empty
    // snapshotId/projectId/sessionId/profile/fileId are rejected; all identity
    // IDs are bounded opaque IDs (<= 128 bytes), relativePath <= 1024 bytes.
    // A failure here echoes the valid requestId (never drops it).
    if let Err(msg) = validate_payload_identity(&payload) {
        return failed_response(Some(request_id), "InvalidInput", msg);
    }
    let Some(selected_profile) = profiles::resolve(&payload.profile) else {
        return failed_response(
            Some(request_id),
            "UnsupportedCapability",
            "unknown profile or version",
        );
    };
    if selected_profile.is_knu() != payload.configuration.is_some() {
        return failed_response(
            Some(request_id),
            "InvalidInput",
            "KNU requires configuration; engine-only requests must omit it",
        );
    }
    let is_references = selected_profile == profiles::Profile::SourceReferences;
    if is_references != payload.reference_configuration.is_some() {
        return failed_response(
            Some(request_id),
            "InvalidInput",
            "referenceConfiguration is required only for source references",
        );
    }
    if selected_profile.is_knu()
        && payload
            .files
            .iter()
            .any(|f| !valid_project_path(&f.relative_path))
    {
        return failed_response(
            Some(request_id),
            "InvalidInput",
            "invalid project-relative path",
        );
    }
    let resolved = resolve_limits(&payload.limits);

    // Reject a packed buffer larger than the resolved total-byte cap even
    // when descriptors reference only a small prefix (a request limit may
    // LOWER the default; e.g. maxTotalBytes=4 with a 5-byte buffer).
    if (packed_bytes.len() as u64) > resolved.max_total_bytes {
        return failed_response(
            Some(request_id),
            "QuotaExceeded",
            "packed buffer exceeded the total byte limit",
        );
    }

    if payload.files.len() as u32 > resolved.max_files {
        return failed_response(
            Some(request_id),
            "QuotaExceeded",
            "file count exceeded the project file limit",
        );
    }
    if let Some(dup) = first_duplicate_file_id(&payload.files) {
        return failed_response(
            Some(request_id),
            "InvalidInput",
            format!("duplicate fileId: {dup}"),
        );
    }

    // New-profile map/path allocations follow the existing metadata, buffer,
    // file-count and opaque-identity preflight, and still precede all lexing.
    if is_references {
        if !reference_wire::validate(metadata_json) {
            return failed_response(
                Some(request_id),
                "InvalidInput",
                "invalid source-reference request shape",
            );
        }
        if let Err(message) = references::validate(&payload) {
            return failed_response(Some(request_id), "InvalidInput", message);
        }
    }

    // Validate slices, UTF-8, BOM consistency, and total/size caps BEFORE lexing.
    // Collect the validated (offset, length) pairs so the lexing pass does not
    // re-run bounds checks (and never needs an expect/panic).
    let packed_len = packed_bytes.len();
    let mut total: u64 = 0;
    let mut validated: Vec<(usize, u64)> = Vec::with_capacity(payload.files.len());
    for f in &payload.files {
        let (off, len) = match checked_slice(f, packed_len) {
            Ok(v) => v,
            Err(msg) => return failed_response(Some(request_id.clone()), "InvalidInput", msg),
        };
        if len > resolved.max_bytes_per_file {
            return failed_response(
                Some(request_id.clone()),
                "QuotaExceeded",
                "a file exceeded the per-file byte limit",
            );
        }
        total = match total.checked_add(len) {
            Some(v) => v,
            None => {
                return failed_response(
                    Some(request_id.clone()),
                    "InvalidInput",
                    "offset/length overflow",
                )
            }
        };
        if total > resolved.max_total_bytes {
            return failed_response(
                Some(request_id.clone()),
                "QuotaExceeded",
                "total bytes exceeded the project byte limit",
            );
        }
        // Slice bounds already verified by checked_slice; this indexing is safe.
        let slice = &packed_bytes[off..off + len as usize];
        if f.media_kind == "text" {
            if f.has_bom != has_bom(slice) {
                return failed_response(
                    Some(request_id.clone()),
                    "InvalidInput",
                    "hasBom flag does not match the file bytes",
                );
            }
            if std::str::from_utf8(slice).is_err() {
                return failed_response(
                    Some(request_id.clone()),
                    "InvalidInput",
                    "file bytes are not valid UTF-8",
                );
            }
        }
        // Non-text (binary) files are metadata-only: bounds-checked
        // above, but NOT UTF-8/BOM-validated or lexed.
        validated.push((off, len));
    }

    // Lex each eligible file.
    let mut file_facts: Vec<dto::SourceFileFacts> = Vec::with_capacity(payload.files.len());
    let mut all_diagnostics: Vec<dto::EngineDiagnostic> = Vec::new();
    let mut diagnostics_truncated = false;
    let mut rule_limitations: Vec<String> = Vec::new();
    let mut diag_cap_remaining = resolved.max_diagnostics_per_project;
    let mut lookaheads = Vec::with_capacity(payload.files.len());

    let mut budget = FileBudget {
        tokens_per_file_cap: resolved.max_tokens_per_file,
        tokens_per_file_used: 0,
        tokens_per_project_cap: resolved.max_tokens_per_project,
        tokens_per_project_used: 0,
        brace_depth_cap: resolved.max_brace_depth,
    };

    for (f, &(off, len)) in payload.files.iter().zip(&validated) {
        // Slice bounds were validated above; indexing is safe.
        let slice = &packed_bytes[off..off + len as usize];

        if f.media_kind != "text" {
            lookaheads.push(lexer::LookaheadBudget::new(0));
            // Binary/non-source: NotApplicable, no lexing, metadata-only.
            file_facts.push(dto::SourceFileFacts {
                file_id: f.file_id.clone(),
                byte_length: len,
                has_bom: f.has_bom,
                newline_kind: NewlineKind::Lf,
                truncated: false,
                brace_depth_exceeded: false,
                token_count: 0,
                engine_outcome: Outcome::NotApplicable,
                tokens: Vec::new(),
            });
            continue;
        }

        let mut scan: ScanResult = lexer::scan_file(
            &f.file_id,
            slice,
            len,
            f.has_bom,
            &mut budget,
            &mut diag_cap_remaining,
        );
        if scan.diagnostic_dropped {
            diagnostics_truncated = true;
        }
        if scan.lookahead_exhausted {
            rule_limitations.push(format!(
                "lookahead byte-work budget exhausted for file {fid}; scan stopped without guessing",
                fid = f.file_id
            ));
        }
        all_diagnostics.extend(scan.diagnostics.iter().cloned());
        lookaheads.push(std::mem::replace(
            &mut scan.lookahead,
            lexer::LookaheadBudget::new(0),
        ));
        let facts = facts::file_facts(&f.file_id, len, f.has_bom, scan, &f.media_kind);
        file_facts.push(facts);
    }

    let (rule, aggregate) =
        facts::rule_result_and_aggregate(&file_facts, &all_diagnostics, &rule_limitations);
    let coverage = facts::coverage(&file_facts, diagnostics_truncated);

    let mut result = AnalysisResult {
        snapshot_id: payload.snapshot_id.clone(),
        profile: payload.profile.clone(),
        facts: SourceFacts { files: file_facts },
        diagnostics: all_diagnostics,
        rule_results: vec![rule],
        aggregate_outcome: aggregate,
        coverage,
        unavailable_capabilities: vec!["pdfFacts".to_string(), "pdfChecks".to_string()],
        configuration: None,
        reference_configuration: None,
        knu: None,
    };
    if selected_profile.is_knu() {
        let mut remaining = resolved
            .max_tokens_per_project
            .saturating_sub(budget.tokens_per_project_used);
        let mut observations = Vec::with_capacity(payload.files.len());
        for (((f, facts), &(off, len)), work) in payload
            .files
            .iter()
            .zip(&result.facts.files)
            .zip(&validated)
            .zip(lookaheads)
        {
            let eligible = f.media_kind == "text"
                && f.relative_path
                    .rsplit_once('.')
                    .is_some_and(|(_, ext)| ext.eq_ignore_ascii_case("tex"));
            observations.push(
                if selected_profile == profiles::Profile::KnuWithFormatting {
                    observations::observe_formatting(
                        facts,
                        &packed_bytes[off..off + len as usize],
                        eligible,
                        work,
                        resolved.max_tokens_per_file,
                        &mut remaining,
                    )
                } else if selected_profile == profiles::Profile::KnuWithGeometryPackage {
                    observations::observe_geometry_package(
                        facts,
                        &packed_bytes[off..off + len as usize],
                        eligible,
                        work,
                        resolved.max_tokens_per_file,
                        &mut remaining,
                    )
                } else {
                    observations::observe_version(
                        facts,
                        &packed_bytes[off..off + len as usize],
                        eligible,
                        work,
                        resolved.max_tokens_per_file,
                        &mut remaining,
                        matches!(
                            selected_profile,
                            profiles::Profile::KnuLatest | profiles::Profile::KnuWithMarginFixes
                        ),
                    )
                },
            );
        }
        rules::evaluate(
            &mut result,
            &payload,
            observations,
            packed_bytes,
            diag_cap_remaining,
        );
    } else if is_references {
        references::evaluate(
            &mut result,
            &payload,
            packed_bytes,
            &validated,
            lookaheads,
            resolved,
            resolved
                .max_tokens_per_project
                .saturating_sub(budget.tokens_per_project_used),
        );
    }

    serialize_result_bounded(Some(request_id), &result, resolved.max_result_bytes)
}

/// Recover and VALIDATE the `requestId` from a JSON envelope, immediately
/// after the bounded-size check. The id must be a nonempty string of at most
/// `MAX_ID_BYTES` UTF-8 bytes. Returns `Ok(valid_id)` or `Err(safe_message)`.
/// On error the caller emits a small `InvalidInput` envelope with NO trusted
/// requestId (never echoes a huge/invalid id). This is the FIRST validation
/// at both public boundaries; once a valid id is recovered it is kept on
/// every later failure (version/operation/snapshot/profile/file/slice/limit).
fn recover_request_id(json: &str) -> Result<String, &'static str> {
    let value: serde_json::Value = match serde_json::from_str(json) {
        Ok(v) => v,
        Err(_) => return Err("malformed envelope"),
    };
    let obj = match value.as_object() {
        Some(o) => o,
        None => return Err("malformed envelope"),
    };
    let id = match obj.get("requestId") {
        Some(v) => v,
        None => return Err("requestId is required"),
    };
    let s = match id.as_str() {
        Some(s) => s,
        None => return Err("requestId must be a string"),
    };
    if s.is_empty() {
        return Err("requestId is required");
    }
    if s.len() > MAX_ID_BYTES {
        return Err("requestId exceeds the 128-byte id limit");
    }
    Ok(s.to_string())
}

/// KNU observations never resolve includes or access a filesystem. Reject raw
/// path escapes rather than normalizing them at this untrusted boundary.
fn valid_project_path(path: &str) -> bool {
    !path.is_empty()
        && !path.contains(['\\', ':'])
        && !path.chars().any(char::is_control)
        && path.split('/').all(|part| !matches!(part, "" | "." | ".."))
}

fn first_duplicate_file_id(files: &[FileDescriptor]) -> Option<String> {
    let mut seen = std::collections::HashSet::new();
    for f in files {
        if !seen.insert(&f.file_id) {
            return Some(f.file_id.clone());
        }
    }
    None
}

/// Validate required, non-empty, bounded payload identity EXCLUDING
/// requestId (which is validated separately and first by `recover_request_id`).
/// Empty snapshotId/projectId/sessionId/profile.id/profile.version/fileId are
/// rejected; all opaque IDs are bounded (<= `MAX_ID_BYTES` bytes, not necessarily
/// UUID syntax) and relativePath <= `MAX_PATH_BYTES`. Small fixture IDs stay
/// valid. Returns `Err(safe_message)` on the first violation; the caller echoes
/// the already-validated requestId on this failure.
fn validate_payload_identity(payload: &AnalyzePayload) -> Result<(), String> {
    let id_too_long = |name: &str, v: &str| -> Option<String> {
        if v.len() > MAX_ID_BYTES {
            Some(format!("{name} exceeds the {MAX_ID_BYTES}-byte id limit"))
        } else {
            None
        }
    };
    if payload.snapshot_id.is_empty() {
        return Err("snapshotId is required".to_string());
    }
    if let Some(m) = id_too_long("snapshotId", &payload.snapshot_id) {
        return Err(m);
    }
    if payload.project_id.is_empty() {
        return Err("projectId is required".to_string());
    }
    if let Some(m) = id_too_long("projectId", &payload.project_id) {
        return Err(m);
    }
    if payload.session_id.is_empty() {
        return Err("sessionId is required".to_string());
    }
    if let Some(m) = id_too_long("sessionId", &payload.session_id) {
        return Err(m);
    }
    if payload.profile.id.is_empty() {
        return Err("profile.id is required".to_string());
    }
    if let Some(m) = id_too_long("profile.id", &payload.profile.id) {
        return Err(m);
    }
    if payload.profile.version.is_empty() {
        return Err("profile.version is required".to_string());
    }
    if let Some(m) = id_too_long("profile.version", &payload.profile.version) {
        return Err(m);
    }
    for f in &payload.files {
        if f.file_id.is_empty() {
            return Err("fileId is required".to_string());
        }
        if let Some(m) = id_too_long("fileId", &f.file_id) {
            return Err(m);
        }
        if f.relative_path.len() > MAX_PATH_BYTES {
            return Err(format!(
                "relativePath exceeds the {MAX_PATH_BYTES}-byte path limit"
            ));
        }
    }
    Ok(())
}

/// Bounds-check a file descriptor's offset+length against the packed buffer
/// length. Returns `(offset, length)` or an error message. Never panics;
/// uses `usize::try_from` so 64-bit host tests and 32-bit wasm targets agree.
fn checked_slice(f: &FileDescriptor, packed_len: usize) -> Result<(usize, u64), String> {
    let off = usize::try_from(f.byte_offset).map_err(|_| "file offset out of range".to_string())?;
    let len = usize::try_from(f.byte_length).map_err(|_| "file length out of range".to_string())?;
    let end = off
        .checked_add(len)
        .ok_or_else(|| "file offset/length overflow".to_string())?;
    if end > packed_len {
        return Err("file slice exceeds packed buffer".to_string());
    }
    Ok((off, f.byte_length))
}

// ---- native tests ----
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn recover_request_id_validates_and_recovers() {
        assert_eq!(recover_request_id(r#"{"requestId":"abc"}"#).unwrap(), "abc");
        // missing => error (no trusted id)
        assert!(recover_request_id(r#"{"x":1}"#).is_err());
        // empty => error
        assert!(recover_request_id(r#"{"requestId":""}"#).is_err());
        // non-string => error
        assert!(recover_request_id(r#"{"requestId":7}"#).is_err());
        // oversize => error
        let big = format!("{{\"requestId\":\"{}\"}}", "x".repeat(MAX_ID_BYTES + 1));
        assert!(recover_request_id(&big).is_err());
        // malformed envelope => error
        assert!(recover_request_id("not json").is_err());
        assert!(recover_request_id("[]").is_err());
    }

    #[test]
    fn resolve_limits_clamps_upward_to_defaults() {
        let r = resolve_limits(&Limits {
            max_bytes_per_file: Some(999 * 1024 * 1024),
            max_files: Some(999_999),
            max_total_bytes: Some(999 * 1024 * 1024),
            max_result_bytes: Some(999 * 1024 * 1024),
            max_tokens_per_file: Some(999_999),
            max_tokens_per_project: Some(999_999),
            max_diagnostics_per_project: Some(999_999),
            max_brace_depth: Some(999_999),
        });
        assert_eq!(r.max_bytes_per_file, D_MAX_BYTES_PER_FILE);
        assert_eq!(r.max_files, D_MAX_FILES);
        assert_eq!(r.max_total_bytes, D_MAX_TOTAL_BYTES);
        assert_eq!(r.max_result_bytes, D_MAX_RESULT_BYTES);
        assert_eq!(r.max_tokens_per_file, D_MAX_TOKENS_PER_FILE);
        assert_eq!(r.max_tokens_per_project, D_MAX_TOKENS_PER_PROJECT);
        assert_eq!(r.max_diagnostics_per_project, D_MAX_DIAGNOSTICS_PER_PROJECT);
        assert_eq!(r.max_brace_depth, D_MAX_BRACE_DEPTH);
    }

    #[test]
    fn resolve_limits_lowers_defaults() {
        let r = resolve_limits(&Limits {
            max_bytes_per_file: Some(1024),
            max_files: Some(2),
            max_total_bytes: Some(2048),
            max_result_bytes: Some(4096),
            max_tokens_per_file: Some(4),
            max_tokens_per_project: Some(8),
            max_diagnostics_per_project: Some(1),
            max_brace_depth: Some(3),
        });
        assert_eq!(r.max_bytes_per_file, 1024);
        assert_eq!(r.max_files, 2);
        assert_eq!(r.max_total_bytes, 2048);
        assert_eq!(r.max_result_bytes, 4096);
        assert_eq!(r.max_tokens_per_file, 4);
        assert_eq!(r.max_tokens_per_project, 8);
        assert_eq!(r.max_diagnostics_per_project, 1);
        assert_eq!(r.max_brace_depth, 3);
    }
}

#[cfg(test)]
mod contract_tests;
#[cfg(test)]
mod formatting_tests;
#[cfg(test)]
mod geometry_package_tests;
#[cfg(test)]
mod geometry_tests;
#[cfg(test)]
mod knu_tests;
#[cfg(test)]
mod margin_fix_tests;
#[cfg(test)]
mod reference_tests;
