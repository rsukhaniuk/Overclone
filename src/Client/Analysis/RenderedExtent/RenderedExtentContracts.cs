using Client.Pdf;

namespace Client.Analysis.RenderedExtent;

public static class RenderedExtentContract
{
    public const int SchemaVersion = 1, RequestMaxBytes = 1024, ResponseMaxBytes = 8192, EvaluationDeadlineMs = 5000;
    public const string ProfileId = "knu-csc-2025-rendered-extent", ProfileVersion = "1.0.0";
    public const string RuleId = "KNU-CSC-2025-PDF-EXTENT-001", Domain = "renderedObservation";
    public const string GuidelineSha256 = "8be4d724ac108488e6b1766322e7fd6b3646412aa2b6de12b6945f48fb380714";
    public const string SourceUrl = "https://csc.knu.ua/uk/filer/canonical/1740300611/2680/";
    public const string Coverage = "Total PDF pages only. Appendix and main-body pages are not identified. Overall compliance is not assessed.";
    public static bool WorkType(string? value) => value is "unspecified" or "coursework" or "bachelorQualification" or "masterQualification";
    public static bool RequestId(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool Epoch(long value) => value is > 0 and <= 9007199254740991;
}

public sealed record RenderedPageRange(int Start, int End)
{
    public bool IsValidFor(int pages) => Start >= 1 && End >= Start && End <= pages;
    public int Count => End - Start + 1;
}
public sealed record RenderedSectionRanges(RenderedPageRange? Introduction, RenderedPageRange? MainBody,
    RenderedPageRange? Conclusions, int? AppendixStartPage, bool NoAppendices)
{
    public bool IsValidFor(int pages)
    {
        if (pages is < 1 or > 512 || NoAppendices && AppendixStartPage is not null
            || AppendixStartPage is { } appendix && (appendix < 1 || appendix > pages)) return false;
        int previous = 0;
        foreach (var range in new[] { Introduction, MainBody, Conclusions })
        {
            if (range is null) continue;
            if (!range.IsValidFor(pages) || range.Start <= previous
                || AppendixStartPage is { } start && range.End >= start) return false;
            previous = range.End;
        }
        return true;
    }
}
public sealed record RenderedSectionResult(string RuleId, int? Pages, int? Minimum, int? Maximum, string Status);
public sealed record RenderedSectionResults(RenderedSectionResult Introduction, RenderedSectionResult MainBody,
    RenderedSectionResult Conclusions);
public sealed record RenderedExtentRequest(string RequestId, string PdfSha256, int PageCount, string WorkType,
    RenderedSectionRanges? Sections = null)
{
    public bool IsValid => RenderedExtentContract.RequestId(RequestId) && PdfHashes.IsSha(PdfSha256)
        && PageCount is >= 1 and <= 512 && RenderedExtentContract.WorkType(WorkType)
        && (Sections is null || Sections.IsValidFor(PageCount));
}

/// <summary>Validated Rust observation only. C# has no threshold table or rule evaluator.</summary>
public sealed class RenderedExtentObservation
{
    internal RenderedExtentObservation(RenderedExtentRequest request, string status, string reason, int? minimum, int? maximum,
        int? nonAppendixPages = null, RenderedSectionResults? sections = null)
    { Request = request; Status = status; Reason = reason; UsualMinimum = minimum; UsualMaximum = maximum; NonAppendixPages = nonAppendixPages; Sections = sections; }
    public RenderedExtentRequest Request { get; }
    public string Status { get; }
    public string Reason { get; }
    public int? UsualMinimum { get; }
    public int? UsualMaximum { get; }
    public int? NonAppendixPages { get; }
    public RenderedSectionResults? Sections { get; }
    public string ProfileVersion => Sections is null ? RenderedExtentContract.ProfileVersion : "2.0.0";
}

public enum RenderedExtentEvaluationStatus { Completed, InvalidInput, Cancelled, TimedOut, Busy, Unavailable, InvalidResponse, Disposed, TransportError }
public sealed record RenderedExtentEvaluationReply(RenderedExtentEvaluationStatus Status, string RequestId,
    long WorkerEpoch, string? ResponseJson = null);

/// <summary>Only the actual operation's returned epoch is correlation evidence;
/// a current/global epoch getter after an await is never used. Caller awaits the
/// ACTUAL task to settlement, including a noncooperative cancelled operation.</summary>
public interface IRenderedExtentEvaluator : IAsyncDisposable
{
    Task<RenderedExtentEvaluationReply> EvaluateAsync(RenderedExtentRequest request, CancellationToken cancellationToken = default);
}

public enum RenderedExtentCheckState
{
    Idle, Inspecting, Evaluating, Completed, Stale, Cancelled, TimedOut, InspectorBusy,
    RendererUnavailable, EvaluationUnavailable, InvalidResponse, Limits, NativeFailure, Error, Disposed, InvalidRanges
}
public sealed record RenderedExtentEvidence(string PdfSha256, long PdfBytes, string CompileRequestId,
    long CompileEpoch, string InputDigest, Guid CaptureId, string InspectionRequestId,
    long InspectionEpoch, PdfRuntimeIdentity Runtime, long EvaluationEpoch);
public sealed record RenderedExtentReport(RenderedExtentObservation Observation, RenderedExtentEvidence Evidence);
