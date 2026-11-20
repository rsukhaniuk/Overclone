using Client.Analysis.RenderedExtent;
using Client.Pdf;
using System.Text.Json;

namespace Client.Analysis.RenderedReview;

public static class RenderedReviewContract
{
    public const int RequestMaxBytes = 262144, FactsMaxBytes = 4194304, ResponseMaxBytes = 524288, EvaluationDeadlineMs = 5000;
    public const string ProfileId = "knu-csc-2025-rendered-review", ProfileVersion = "1.0.0";
    public const string PaginationProfileVersion = "1.2.0";
    public const string HeadingProfileVersion = "1.3.0";
    public const string ParagraphIndentProfileVersion = "1.4.0";
    public const string HeadingPageEndProfileVersion = "1.5.0";
}
public sealed record RenderedRoleAnnotation(long AnnotationId, string Role, int PageIndex, int? CharacterStart,
    int? CharacterEnd, string? SourceFileId, AnalysisSpanDto? SourceSpan, string Provenance, string Certainty);
public sealed record RenderedReviewRequest(string RequestId, string PdfSha256, string CompileRequestId, long CompileEpoch,
    string InputDigest, Guid CaptureId, string WorkType, long AnnotationVersion, int PageCount,
    IReadOnlyList<int> RequestedPages, IReadOnlyList<RenderedRoleAnnotation> Annotations, string FactsSha256)
{
    public string ProfileVersion { get; init; } = RenderedReviewContract.ProfileVersion;
    public bool AuthorConfirmedPageTextCoverage { get; init; }

    public bool IsValid => ProfileVersion is RenderedReviewContract.ProfileVersion or RenderedReviewContract.PaginationProfileVersion
        or RenderedReviewContract.HeadingProfileVersion or RenderedReviewContract.ParagraphIndentProfileVersion
        or RenderedReviewContract.HeadingPageEndProfileVersion
        && RenderedExtentContract.RequestId(RequestId) && RenderedExtentContract.RequestId(CompileRequestId)
        && PdfHashes.IsSha(PdfSha256) && PdfHashes.IsSha(InputDigest) && PdfHashes.IsSha(FactsSha256)
        && RenderedExtentContract.Epoch(CompileEpoch) && CaptureId != Guid.Empty && AnnotationVersion is >= 0 and <= 9007199254740991
        && RenderedExtentContract.WorkType(WorkType) && PageCount is >= 1 and <= 512 && RequestedPages is { Count: > 0 and <= 8 }
        && (ProfileVersion != RenderedReviewContract.ParagraphIndentProfileVersion || RequestedPages.Count == 1)
        && (ProfileVersion == RenderedReviewContract.HeadingPageEndProfileVersion
            ? AuthorConfirmedPageTextCoverage && RequestedPages.Count == 1
            : !AuthorConfirmedPageTextCoverage)
        && RequestedPages.All(i => i >= 0 && i < PageCount) && RequestedPages.Zip(RequestedPages.Skip(1)).All(p => p.First < p.Second)
        && Annotations is { Count: <= 512 };
}
public sealed record RenderedReviewEvaluationReply(RenderedExtentEvaluationStatus Status, string RequestId, long WorkerEpoch, string? ResponseJson = null);
public interface IRenderedReviewEvaluator : IAsyncDisposable
{
    Task<RenderedReviewEvaluationReply> EvaluateAsync(RenderedReviewRequest request, ReadOnlyMemory<byte> factsUtf8, CancellationToken cancellationToken = default);
}
public sealed record RenderedRuleResult(string RuleId, string RuleVersion, string Domain, string SupportState, string Outcome,
    string Reason, string Severity, JsonElement Citation, IReadOnlyList<JsonElement> Evidence, IReadOnlyList<string> Limitations);
public sealed record RenderedReviewObservation(RenderedReviewRequest Request, IReadOnlyList<RenderedRuleResult> Results);
public sealed record RenderedReviewPageEvidence(int PageIndex, string InspectionRequestId, long InspectionEpoch,
    string FactsSha256, string EvaluationRequestId, long EvaluationEpoch);
public sealed record RenderedReviewEvidence(RenderedExtentEvidence Compilation, Client.Compilation.CompileArtifactContentIdentity ContentIdentity,
    long AnnotationVersion, IReadOnlyList<RenderedReviewPageEvidence> Pages);
public sealed record RenderedReviewReport(IReadOnlyList<RenderedRuleResult> Results, RenderedReviewEvidence Evidence,
    int RequestedPages, int EvaluatedPages, bool Complete, string Code)
{
    public string ProfileVersion { get; init; } = RenderedReviewContract.ProfileVersion;
}
