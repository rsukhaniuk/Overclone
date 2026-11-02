using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Client.Analysis;

// Data only, not report authority or executable edits. ThesisWireValidation
// closes the string enums/metadata; captured-byte and current-report guards are
// required before navigation. UI consumers render all text escaped, never HTML.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisThesisEventDto(
    string Kind, string FileId, AnalysisSpanDto Span, string Text, bool Appendix);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisThesisRuleDto(
    string RuleId, string RuleVersion, string Domain, string SupportState,
    RuleOutcome Outcome, string Reason, string Method, bool KnownCounterexample,
    string FixPolicy, string Severity, AnalysisCitationDto Citation,
    ImmutableArray<int> EvidenceIndexes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisThesisUnavailableDto(string RuleId, string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisThesisDto(
    int SchemaVersion, string CatalogId, string CatalogVersion, string GuidelineSha256,
    string RootFileId, AnalysisWorkType WorkType, string Domain,
    string GlobalCoverage, string RenderedCompliance, bool CompleteLiteralScan,
    ImmutableArray<string> Limitations, ImmutableArray<string> ClosureFileIds,
    ImmutableArray<AnalysisThesisEventDto> Events,
    ImmutableArray<AnalysisThesisRuleDto> RuleResults,
    ImmutableArray<AnalysisThesisUnavailableDto> UnavailableCells,
    string WorkTypeDependentTitle, string BibliographyMode,
    ImmutableArray<string> FirstLiteralCitationKeys);
