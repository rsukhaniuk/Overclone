using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Client.Analysis;

public enum AnalysisWorkType { Unspecified, Coursework, BachelorQualification, MasterQualification }
public enum AnalysisEvaluationDomain { SourceObservation }
public enum AnalysisTitleKind { Short, Display }
public enum AnalysisMarginSide { Left, Right, Top, Bottom }
public enum AnalysisPaperSize { A4, A3, A5, B5, Letter, Legal, Executive }
public enum AnalysisSpacingSyntax { OneHalfSpacing, SetStretch, LineSpread, BaselineStretch }
public enum AnalysisCaptionEnvironment { Figure, Table }
public enum AnalysisLimitation
{
    IncompleteEngineScan, UnmatchedGroup, UnknownSemantics, UnsupportedContext,
    UnsupportedArgument, UnsupportedLength, ArgumentTooLong, ConflictingDeclarations,
    IncludeNotResolved, ObservationBudget, LookaheadBudget, DiagnosticBudget, NoSupportedObservation,
    UnsupportedGeometry, MissingMarginSides, MultipleGeometryDeclarations,
    PaperUncertain, LineSpacingUncertain, CaptionUncertain,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisConfigurationDto(AnalysisWorkType WorkType);
public sealed record AnalysisCitationDto(string Section, ImmutableArray<int> PrintedPages, ImmutableArray<int> PdfPages);
public sealed record AnalysisSourceRuleDto(
    string RuleId, string Version, string Origin, AnalysisEvaluationDomain EvaluationDomain,
    AnalysisCitationDto Citation, string Severity, string SeverityRationale,
    ImmutableArray<AnalysisWorkType> ApplicableWorkTypes, string Expected, string SupportedSyntax,
    ImmutableArray<string> Limitations);
public sealed record AnalysisProfileMetadataDto(
    AnalysisProfileDto Profile, string GuidelineTitle, string ApprovedUrl, string GuidelineSha256,
    ImmutableArray<AnalysisSourceRuleDto> Rules, ImmutableArray<string> Limitations);
public sealed record AnalysisLengthDto(ulong NumeratorMm, ulong Denominator, string Unit);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AnalysisIndentValueDto), "indentDeclaration")]
[JsonDerivedType(typeof(AnalysisHeadingValueDto), "headingTitle")]
[JsonDerivedType(typeof(AnalysisMarginValueDto), "marginDeclaration")]
[JsonDerivedType(typeof(AnalysisPaperValueDto), "paperDeclaration")]
[JsonDerivedType(typeof(AnalysisSpacingValueDto), "lineSpacingDeclaration")]
[JsonDerivedType(typeof(AnalysisCaptionValueDto), "captionTitle")]
public abstract record AnalysisObservationValueDto;
public sealed record AnalysisIndentValueDto(
    AnalysisSpanDto TargetSpan,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] AnalysisLengthDto? Length) : AnalysisObservationValueDto;
public sealed record AnalysisMarginValueDto(
    AnalysisMarginSide Side,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] AnalysisLengthDto? Length) : AnalysisObservationValueDto;
public sealed record AnalysisHeadingValueDto(
    AnalysisTitleKind TitleKind,
    // Rust char is one Unicode scalar, NOT necessarily one UTF-16 code unit.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FinalScalar) : AnalysisObservationValueDto;
public sealed record AnalysisPaperValueDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] AnalysisPaperSize? Paper) : AnalysisObservationValueDto;
public sealed record AnalysisSpacingValueDto(
    AnalysisSpacingSyntax Syntax,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ulong? Numerator,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ulong? Denominator) : AnalysisObservationValueDto;
public sealed record AnalysisCaptionValueDto(
    AnalysisCaptionEnvironment Environment,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FinalScalar) : AnalysisObservationValueDto;
public sealed record AnalysisObservationDto(
    string Id, string RuleId, AnalysisSpanDto CommandSpan, AnalysisSpanDto DeclarationSpan,
    AnalysisSpanDto ValueSpan, string Excerpt, bool ExcerptTruncated, string Method,
    AnalysisObservationValueDto Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? Agrees,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] AnalysisLimitation? Reason);
public sealed record AnalysisFileObservationsDto(
    string FileId, bool Eligible, bool Complete, int ObservationCount,
    ImmutableArray<AnalysisObservationDto> Observations, ImmutableArray<AnalysisLimitation> Limitations);

/// <summary>Untrusted proposal DATA. No accept/apply behavior. Even after wire
/// correlation, future review must validate current permissions/head/bytes/hash.</summary>
public sealed record AnalysisFixDescriptorDto(
    string Id, string Version, string RuleId, string RuleVersion, string EvidenceId,
    string SnapshotId, string ProjectId, string SessionId, long WorkingGeneration,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BaseHeadRevisionId,
    AnalysisProfileDto Profile, AnalysisConfigurationDto Configuration, string FileId,
    string RelativePath, string ExpectedFileSha256, AnalysisSpanDto Span,
    string ExpectedOriginal, string Replacement, string Explanation);
public sealed record AnalysisKnuDto(
    AnalysisProfileMetadataDto Metadata, RuleOutcome EngineAggregateOutcome,
    ImmutableArray<AnalysisFileObservationsDto> Files, ImmutableArray<AnalysisFixDescriptorDto> FixDescriptors);
