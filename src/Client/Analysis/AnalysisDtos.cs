using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Client.Analysis;

// Wire DTOs mirroring the ACCEPTED Rust serde schema in rust/src/analysis/dto.rs
// exactly (field names, enum spellings, nested shapes). Both sides serialize
// camelCase; enum values are lowerCamelCase STRINGS via a JsonStringEnumConverter
// with JsonNamingPolicy.CamelCase (PropertyNamingPolicy alone does NOT convert
// enum values). Wire identity IDs are plain strings — the existing domain
// FileId/ProjectId/RevisionId/AnalysisSnapshotId structs must NOT serialize as
// { value: ... }, so the request packer converts domain ids via ToString() and
// these DTOs never carry the structs. No JS runtime calls here.

/// <summary>Engine / aggregate outcome. Wire spellings (lowerCamelCase),
/// identical to Rust: "pass", "fail", "indeterminate", "notApplicable",
/// "unavailable".</summary>
public enum RuleOutcome
{
    Pass,
    Fail,
    Indeterminate,
    NotApplicable,
    Unavailable,
}

/// <summary>Token kind. Wire spellings (lowerCamelCase), identical to Rust:
/// "command", "braceOpen", "braceClose", "group", "optionalArg", "text",
/// "comment", "escape", "includePath", "label", "reference", "verbatim",
/// "unknown".</summary>
public enum TokenKind
{
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

/// <summary>Per-file newline classification. Wire spellings (lowercase),
/// identical to Rust: "lf", "crlf", "mixed".</summary>
public enum NewlineKind
{
    Lf,
    Crlf,
    Mixed,
}

/// <summary>Response status. Wire spellings (lowerCamelCase): "progress",
/// "completed", "cancelled", "failed". The Rust core emits only
/// "completed"/"failed"; progress/cancelled are produced by the later JS
/// bridge (M2a-2b) and are mirrored here for round-trip.</summary>
public enum ResponseStatus
{
    Progress,
    Completed,
    Cancelled,
    Failed,
}

// ---- Request envelope ----

public sealed record AnalysisRequestEnvelope(
    int ProtocolVersion,
    string RequestId,
    string Operation,
    AnalysisRequestPayload Payload);

public sealed record AnalysisRequestPayload(
    string SnapshotId,
    string ProjectId,
    string SessionId,
    long WorkingGeneration,
    string? BaseHeadRevisionId,
    AnalysisProfileDto Profile,
    AnalysisLimitsDto Limits,
    IReadOnlyList<AnalysisFileDescriptorDto> Files,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnalysisConfigurationDto? Configuration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnalysisReferenceConfigurationDto? ReferenceConfiguration = null);

public sealed record AnalysisProfileDto(string Id, string Version)
{
    public static AnalysisProfileDto EngineStructural() => new("engine-structural", "0");
}

public sealed record AnalysisLimitsDto(
    long? MaxBytesPerFile,
    int? MaxFiles,
    long? MaxTotalBytes,
    long? MaxResultBytes,
    int? MaxTokensPerFile,
    int? MaxTokensPerProject,
    int? MaxDiagnosticsPerProject,
    int? MaxBraceDepth)
{
    /// <summary>All-None limits (the Rust core applies its hard defaults).</summary>
    public static AnalysisLimitsDto None() => new(null, null, null, null, null, null, null, null);
}

public sealed record AnalysisFileDescriptorDto(
    string FileId,
    string RelativePath,
    string MediaKind,
    bool HasBom,
    string Sha256,
    long ByteOffset,
    long ByteLength);

// ---- Response envelope ----

public sealed record AnalysisResponseEnvelope(
    int ProtocolVersion,
    string? RequestId,
    ResponseStatus Status,
    AnalysisResultDto? Payload,
    AnalysisErrorDto? Error);

public sealed record AnalysisErrorDto(string Code, string SafeMessage, bool Retryable);

// ---- Completed analyze payload (AnalysisResult) ----

public sealed record AnalysisResultDto(
    string SnapshotId,
    AnalysisProfileDto Profile,
    AnalysisSourceFactsDto Facts,
    IReadOnlyList<AnalysisEngineDiagnosticDto> Diagnostics,
    IReadOnlyList<AnalysisRuleResultDto> RuleResults,
    RuleOutcome AggregateOutcome,
    AnalysisCoverageDto Coverage,
    IReadOnlyList<string> UnavailableCapabilities,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnalysisConfigurationDto? Configuration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnalysisKnuDto? Knu = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnalysisReferenceConfigurationDto? ReferenceConfiguration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnalysisThesisDto? Thesis = null);

public sealed record AnalysisSourceFactsDto(IReadOnlyList<AnalysisSourceFileFactsDto> Files);

public sealed record AnalysisSourceFileFactsDto(
    string FileId,
    long ByteLength,
    bool HasBom,
    NewlineKind NewlineKind,
    bool Truncated,
    bool BraceDepthExceeded,
    int TokenCount,
    RuleOutcome EngineOutcome,
    IReadOnlyList<AnalysisTokenDto> Tokens);

public sealed record AnalysisTokenDto(
    TokenKind Kind,
    long StartByte,
    long EndByte,
    string? Text,
    string? Note);

public sealed record AnalysisEngineDiagnosticDto(
    string Id,
    string RuleId,
    string Origin,
    string Severity,
    string FileId,
    AnalysisSpanDto? Span,
    string Message,
    AnalysisLocationDto? PrimaryLocation);

public sealed record AnalysisSpanDto(long StartByte, long EndByte);

public sealed record AnalysisLocationDto(int Line, int Column);

public sealed record AnalysisRuleResultDto(
    string RuleId,
    string Origin,
    RuleOutcome Outcome,
    IReadOnlyList<AnalysisPerFileOutcomeDto> PerFile,
    IReadOnlyList<string> Limitations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnalysisSourceRuleDto? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? EvidenceIds = null);

public sealed record AnalysisPerFileOutcomeDto(
    string FileId,
    RuleOutcome Outcome,
    IReadOnlyList<string> DiagnosticIds);

public sealed record AnalysisCoverageDto(
    IReadOnlyList<string> FactKinds,
    IReadOnlyList<string> Unsupported,
    IReadOnlyList<string> TruncatedFiles,
    bool DiagnosticsTruncated,
    bool ResultTruncated);

// ---- Initialize response payload ----

public sealed record InitializeResultDto(
    string EngineVersion,
    IReadOnlyList<string> Operations,
    AnalysisResolvedLimitsDto Limits);

public sealed record AnalysisResolvedLimitsDto(
    long MaxBytesPerFile,
    int MaxFiles,
    long MaxTotalBytes,
    long MaxResultBytes,
    int MaxTokensPerFile,
    int MaxTokensPerProject,
    int MaxDiagnosticsPerProject,
    int MaxBraceDepth);
