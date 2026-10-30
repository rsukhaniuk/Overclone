//! Opt-in authoring wire v1. No editor mutation or compile authority.
use crate::analysis::dto::{FileDescriptor, Limits, Span};
use serde::{Deserialize, Serialize};

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
    pub snapshot_id: String,
    pub project_id: String,
    pub session_id: String,
    pub working_generation: i64,
    pub base_head_revision_id: Option<String>,
    pub root_file_id: String,
    pub file_id: String,
    pub caret_byte: u64,
    pub context: Context,
    pub query: String,
    pub catalog_version: String,
    pub files: Vec<FileDescriptor>,
    pub limits: Limits,
}

#[derive(Deserialize, PartialEq, Eq, Clone, Copy, Debug)]
#[serde(rename_all = "camelCase")]
pub enum Context {
    Command,
    Environment,
    Reference,
    Citation,
    InputPath,
    Definition,
    Outline,
}

#[derive(Serialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct ResultPayload {
    pub snapshot_id: String,
    pub root_file_id: String,
    pub file_id: String,
    pub caret_byte: u64,
    pub catalog_version: &'static str,
    /// Complete for this literal extraction only, never TeX semantic completeness.
    pub complete: bool,
    pub limitations: Vec<&'static str>,
    pub candidates: Vec<Candidate>,
    pub outline: Vec<Outline>,
    pub loaded_packages: Vec<Package>,
}

#[derive(Serialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Candidate {
    pub id: String,
    pub kind: &'static str,
    pub label: String,
    pub detail: String,
    pub insert_text: String,
    pub replace_span: Span,
    pub caret_offset_utf16: usize,
    pub requires_review: bool,
    pub origin: &'static str,
    pub definition: Option<Definition>,
}

#[derive(Serialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Definition {
    pub file_id: String,
    pub span: Span,
}

#[derive(Serialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Outline {
    pub kind: &'static str,
    pub label: String,
    pub file_id: String,
    pub span: Span,
    pub parent_index: Option<usize>,
}

#[derive(Serialize, Debug)]
pub struct Package {
    pub name: String,
    pub options: String,
    /// A literal source mention, not evidence of successful loading/provisioning.
    pub status: &'static str,
}
