//! Canonical typed wire contract for source analysis.
//!
//! All types use `#[serde(rename_all = "camelCase")]` so Rust and C#
//! (`System.Text.Json` camelCase) agree on field names and enum spellings.
//! Spans are half-open UTF-8 byte ranges into a file's exact BOM-inclusive
//! bytes, on Unicode-scalar boundaries.

use serde::{Deserialize, Serialize};

/// Canonical protocol version understood by this engine.
pub const PROTOCOL_VERSION: u32 = 1;

/// Bound on the metadata JSON string before deserialization (at most ~5000
/// descriptors). Proposed guardrail, not a measured value.
pub const MAX_METADATA_JSON_BYTES: usize = 4 * 1024 * 1024;

/// Bounded length for stored `includePath`/`label`/`reference` argument text.
/// A longer argument is not silently shortened: the token is emitted without
/// `text` and the file becomes `Indeterminate`.
pub const MAX_TOKEN_TEXT_BYTES: usize = 4096;

/// Default for [`FileDescriptor::media_kind`] (serde `default = "..."` takes a
/// function path, not a literal).
fn default_media_kind() -> String {
    "text".to_string()
}

// ---- Engine outcome (wire spellings are lowerCamelCase) ----

#[derive(Serialize, Deserialize, Clone, Copy, PartialEq, Eq, Debug)]
#[serde(rename_all = "camelCase")]
pub enum Outcome {
    Pass,
    Fail,
    Indeterminate,
    NotApplicable,
    Unavailable,
}

// ---- Token kinds ----

#[derive(Serialize, Deserialize, Clone, Copy, PartialEq, Eq, Debug)]
#[serde(rename_all = "camelCase")]
pub enum TokenKind {
    Command,
    BraceOpen,
    BraceClose,
    Group,
    OptionalArg,
    Text,
    Comment,
    Escape,
    IncludePath,
    Label,
    Reference,
    Verbatim,
    Unknown,
}

#[derive(Serialize, Deserialize, Clone, Copy, PartialEq, Eq, Debug)]
#[serde(rename_all = "lowercase")]
pub enum NewlineKind {
    Lf,
    Crlf,
    Mixed,
}

// ---- Request envelope (what Rust receives) ----

#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct RequestEnvelope {
    #[serde(rename = "protocolVersion")]
    pub protocol_version: u32,
    /// Opaque correlation id. Validated and recovered FIRST at both public
    /// boundaries (`initialize`, `analyzeSource`): it must be a nonempty string
    /// of at most 128 UTF-8 bytes. A missing/empty/oversize/non-string id yields a
    /// small `InvalidInput` envelope with NO trusted `requestId` (JSON `null`),
    /// and is never echoed. Once a valid id is recovered it is KEPT on every
    /// later failure (version/operation/snapshot/profile/file/slice/limit),
    /// never fabricated to another request.
    #[serde(rename = "requestId", default)]
    pub request_id: Option<String>,
    pub operation: String,
    #[serde(default)]
    pub payload: AnalyzePayload,
}

#[derive(Deserialize, Debug, Default)]
#[serde(rename_all = "camelCase")]
pub struct AnalyzePayload {
    #[serde(rename = "snapshotId", default)]
    pub snapshot_id: String,
    #[serde(rename = "projectId", default)]
    pub project_id: String,
    #[serde(rename = "sessionId", default)]
    pub session_id: String,
    #[serde(rename = "workingGeneration", default)]
    pub working_generation: i64,
    #[serde(rename = "baseHeadRevisionId", default)]
    pub base_head_revision_id: Option<String>,
    #[serde(default)]
    pub profile: ProfileRef,
    #[serde(default, deserialize_with = "super::knu_dto::configuration")]
    pub configuration: Option<super::knu_dto::Configuration>,
    #[serde(default, deserialize_with = "super::references::configuration")]
    pub reference_configuration: Option<super::references::ReferenceConfiguration>,
    #[serde(default)]
    pub limits: Limits,
    #[serde(default)]
    pub files: Vec<FileDescriptor>,
}

#[derive(Deserialize, Serialize, Clone, Debug, Default)]
#[serde(rename_all = "camelCase")]
pub struct ProfileRef {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub version: String,
}

#[derive(Deserialize, Debug, Default, Clone)]
#[serde(rename_all = "camelCase")]
pub struct Limits {
    #[serde(rename = "maxBytesPerFile", default)]
    pub max_bytes_per_file: Option<u64>,
    #[serde(rename = "maxFiles", default)]
    pub max_files: Option<u32>,
    #[serde(rename = "maxTotalBytes", default)]
    pub max_total_bytes: Option<u64>,
    #[serde(rename = "maxResultBytes", default)]
    pub max_result_bytes: Option<u64>,
    #[serde(rename = "maxTokensPerFile", default)]
    pub max_tokens_per_file: Option<u32>,
    #[serde(rename = "maxTokensPerProject", default)]
    pub max_tokens_per_project: Option<u32>,
    #[serde(rename = "maxDiagnosticsPerProject", default)]
    pub max_diagnostics_per_project: Option<u32>,
    #[serde(rename = "maxBraceDepth", default)]
    pub max_brace_depth: Option<u32>,
}

#[derive(Deserialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
pub struct FileDescriptor {
    #[serde(rename = "fileId")]
    pub file_id: String,
    #[serde(rename = "relativePath")]
    pub relative_path: String,
    #[serde(rename = "mediaKind", default = "default_media_kind")]
    pub media_kind: String,
    #[serde(rename = "hasBom", default)]
    pub has_bom: bool,
    /// Correlation only; not verified by Rust (no crypto dependency).
    #[serde(default)]
    pub sha256: String,
    #[serde(rename = "byteOffset", default)]
    pub byte_offset: u64,
    #[serde(rename = "byteLength", default)]
    pub byte_length: u64,
}

// ---- Response envelope (what Rust returns) ----

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Response {
    #[serde(rename = "protocolVersion")]
    pub protocol_version: u32,
    #[serde(rename = "requestId", skip_serializing_if = "Option::is_none")]
    pub request_id: Option<String>,
    pub status: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub payload: Option<serde_json::Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<ErrorBody>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ErrorBody {
    pub code: &'static str,
    pub safe_message: String,
    pub retryable: bool,
}

// ---- Completed analyze payload (AnalysisResult) ----

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct AnalysisResult {
    #[serde(rename = "snapshotId")]
    pub snapshot_id: String,
    pub profile: ProfileRef,
    pub facts: SourceFacts,
    pub diagnostics: Vec<EngineDiagnostic>,
    #[serde(rename = "ruleResults")]
    pub rule_results: Vec<RuleResult>,
    #[serde(rename = "aggregateOutcome")]
    pub aggregate_outcome: Outcome,
    pub coverage: Coverage,
    #[serde(rename = "unavailableCapabilities", default)]
    pub unavailable_capabilities: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub configuration: Option<super::knu_dto::Configuration>,
    #[serde(
        default,
        skip_serializing_if = "Option::is_none",
        deserialize_with = "super::references::configuration"
    )]
    pub reference_configuration: Option<super::references::ReferenceConfiguration>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub knu: Option<super::knu_dto::KnuAnalysis>,
}

#[derive(Serialize, Deserialize, Clone, Debug, Default)]
#[serde(rename_all = "camelCase")]
pub struct SourceFacts {
    pub files: Vec<SourceFileFacts>,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct SourceFileFacts {
    #[serde(rename = "fileId")]
    pub file_id: String,
    #[serde(rename = "byteLength")]
    pub byte_length: u64,
    #[serde(rename = "hasBom")]
    pub has_bom: bool,
    #[serde(rename = "newlineKind")]
    pub newline_kind: NewlineKind,
    #[serde(default)]
    pub truncated: bool,
    #[serde(rename = "braceDepthExceeded", default)]
    pub brace_depth_exceeded: bool,
    #[serde(rename = "tokenCount")]
    pub token_count: u32,
    #[serde(rename = "engineOutcome")]
    pub engine_outcome: Outcome,
    #[serde(default)]
    pub tokens: Vec<Token>,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Token {
    pub kind: TokenKind,
    #[serde(rename = "startByte")]
    pub start_byte: u64,
    #[serde(rename = "endByte")]
    pub end_byte: u64,
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub text: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub note: Option<String>,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct EngineDiagnostic {
    pub id: String,
    #[serde(rename = "ruleId")]
    pub rule_id: String,
    pub origin: String,
    pub severity: String,
    #[serde(rename = "fileId")]
    pub file_id: String,
    #[serde(default)]
    pub span: Option<Span>,
    pub message: String,
    #[serde(rename = "primaryLocation", default)]
    pub primary_location: Option<Location>,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Span {
    #[serde(rename = "startByte")]
    pub start_byte: u64,
    #[serde(rename = "endByte")]
    pub end_byte: u64,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Location {
    /// 1-based line.
    pub line: u32,
    /// 0-based byte column from line start.
    pub column: u32,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct RuleResult {
    #[serde(rename = "ruleId")]
    pub rule_id: String,
    pub origin: String,
    pub outcome: Outcome,
    #[serde(rename = "perFile", default)]
    pub per_file: Vec<PerFileOutcome>,
    #[serde(default)]
    pub limitations: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source: Option<super::knu_dto::SourceRule>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub evidence_ids: Option<Vec<String>>,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct PerFileOutcome {
    #[serde(rename = "fileId")]
    pub file_id: String,
    pub outcome: Outcome,
    #[serde(rename = "diagnosticIds", default)]
    pub diagnostic_ids: Vec<String>,
}

#[derive(Serialize, Deserialize, Clone, Debug, Default)]
#[serde(rename_all = "camelCase")]
pub struct Coverage {
    #[serde(rename = "factKinds", default)]
    pub fact_kinds: Vec<String>,
    #[serde(default)]
    pub unsupported: Vec<String>,
    #[serde(rename = "truncatedFiles", default)]
    pub truncated_files: Vec<String>,
    #[serde(rename = "diagnosticsTruncated", default)]
    pub diagnostics_truncated: bool,
    /// Always false from a completed Rust response: a result-byte overflow
    /// returns a small `status:"failed"` QuotaExceeded envelope, not a
    /// completed payload with this flag set.
    #[serde(rename = "resultTruncated", default)]
    pub result_truncated: bool,
}

// ---- Initialize payload ----

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InitializePayload {
    #[serde(rename = "engineVersion")]
    pub engine_version: &'static str,
    pub operations: &'static [&'static str],
    pub limits: ResolvedLimits,
}

/// Hard limits actually enforced by this engine, echoed to the bridge.
#[derive(Serialize, Clone, Copy, Debug)]
#[serde(rename_all = "camelCase")]
pub struct ResolvedLimits {
    #[serde(rename = "maxBytesPerFile")]
    pub max_bytes_per_file: u64,
    #[serde(rename = "maxFiles")]
    pub max_files: u32,
    #[serde(rename = "maxTotalBytes")]
    pub max_total_bytes: u64,
    #[serde(rename = "maxResultBytes")]
    pub max_result_bytes: u64,
    #[serde(rename = "maxTokensPerFile")]
    pub max_tokens_per_file: u32,
    #[serde(rename = "maxTokensPerProject")]
    pub max_tokens_per_project: u32,
    #[serde(rename = "maxDiagnosticsPerProject")]
    pub max_diagnostics_per_project: u32,
    #[serde(rename = "maxBraceDepth")]
    pub max_brace_depth: u32,
}
