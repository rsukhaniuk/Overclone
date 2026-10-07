//! Additive, KNU-only wire data. No executable patch operations.
use serde::{Deserialize, Deserializer, Serialize};

use super::dto::{Outcome, ProfileRef, Span};

#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum WorkType {
    Unspecified,
    Coursework,
    BachelorQualification,
    MasterQualification,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Configuration {
    pub work_type: WorkType,
}

/// Omission is supported for the old engine client; explicit null is malformed.
pub fn configuration<'de, D: Deserializer<'de>>(d: D) -> Result<Option<Configuration>, D::Error> {
    Configuration::deserialize(d).map(Some)
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum EvaluationDomain {
    SourceObservation,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Citation {
    pub section: String,
    pub printed_pages: Vec<u32>,
    pub pdf_pages: Vec<u32>,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct SourceRule {
    pub rule_id: String,
    pub version: String,
    pub origin: String,
    pub evaluation_domain: EvaluationDomain,
    pub citation: Citation,
    pub severity: String,
    pub severity_rationale: String,
    pub applicable_work_types: Vec<WorkType>,
    pub expected: String,
    pub supported_syntax: String,
    pub limitations: Vec<String>,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct ProfileMetadata {
    pub profile: ProfileRef,
    pub guideline_title: String,
    pub approved_url: String,
    pub guideline_sha256: String,
    pub rules: Vec<SourceRule>,
    pub limitations: Vec<String>,
}

/// Finite codes, never arbitrary document text in uncertainty messages.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum Limitation {
    IncompleteEngineScan,
    UnmatchedGroup,
    UnknownSemantics,
    UnsupportedContext,
    UnsupportedArgument,
    UnsupportedLength,
    ArgumentTooLong,
    ConflictingDeclarations,
    IncludeNotResolved,
    ObservationBudget,
    LookaheadBudget,
    DiagnosticBudget,
    NoSupportedObservation,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Length {
    /// Exact rational millimeters; no tolerance or nominal-font conversion.
    pub numerator_mm: u64,
    pub denominator: u64,
    pub unit: String,
}

impl Length {
    pub fn is_required(&self) -> bool {
        match (
            self.numerator_mm.checked_mul(10),
            self.denominator.checked_mul(127),
        ) {
            (Some(a), Some(b)) => self.denominator != 0 && a == b,
            _ => false,
        }
    }
    pub fn equals(&self, other: &Self) -> bool {
        match (
            self.numerator_mm.checked_mul(other.denominator),
            other.numerator_mm.checked_mul(self.denominator),
        ) {
            (Some(a), Some(b)) => self.denominator != 0 && other.denominator != 0 && a == b,
            _ => false,
        }
    }
}

#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum TitleKind {
    Short,
    Display,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum ObservationValue {
    IndentDeclaration {
        target_span: Span,
        length: Option<Length>,
    },
    HeadingTitle {
        title_kind: TitleKind,
        final_scalar: Option<char>,
    },
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Observation {
    pub id: String,
    pub rule_id: String,
    pub command_span: Span,
    pub declaration_span: Span,
    pub value_span: Span,
    pub excerpt: String,
    pub excerpt_truncated: bool,
    pub method: String,
    pub value: ObservationValue,
    pub agrees: Option<bool>,
    pub reason: Option<Limitation>,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct FileObservations {
    pub file_id: String,
    pub eligible: bool,
    pub complete: bool,
    pub observation_count: u32,
    pub observations: Vec<Observation>,
    pub limitations: Vec<Limitation>,
}

impl FileObservations {
    pub fn limit(&mut self, reason: Limitation) {
        if !self.limitations.contains(&reason) {
            self.limitations.push(reason);
        }
    }
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct FixDescriptor {
    pub id: String,
    pub version: String,
    pub rule_id: String,
    pub rule_version: String,
    pub evidence_id: String,
    pub snapshot_id: String,
    pub project_id: String,
    pub session_id: String,
    pub working_generation: i64,
    pub base_head_revision_id: Option<String>,
    pub profile: ProfileRef,
    pub configuration: Configuration,
    pub file_id: String,
    pub relative_path: String,
    /// Correlation supplied by capture, NOT independently hashed by this engine.
    /// Future review must recompute/validate this plus expected_original.
    pub expected_file_sha256: String,
    pub span: Span,
    pub expected_original: String,
    pub replacement: String,
    pub explanation: String,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct KnuAnalysis {
    pub metadata: ProfileMetadata,
    pub engine_aggregate_outcome: Outcome,
    pub files: Vec<FileObservations>,
    pub fix_descriptors: Vec<FixDescriptor>,
}
