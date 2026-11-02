using Client.Workspace;
using Client.Workspace.Transfer;

namespace Client.Analysis;

/// <summary>Canonical report + index, never a caller-supplied substitute event,
/// synthetic engine diagnostic or guessed source relocation.</summary>
public sealed record ThesisEvidenceRequest(AnalysisReport Report, int EventIndex);

/// <summary>Source-only mapping over existing current-report and span guards.
/// Parent retains its post-render/pre-DOM verification and exact DOM checks.</summary>
public sealed class ThesisSourceNavigation(SourceNavigator source, WorkspaceCoordinator workspace)
{
    public async Task<NavigationResult> NavigateAsync(ThesisEvidenceRequest request, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !IsCurrent(request)) return Reject();
        // Cancellation retires authority but not the real epoch-query lifetime.
        var verified = await source.VerifyCurrentAsync(request.Report, CancellationToken.None).ConfigureAwait(false);
        if (!verified.Ok || ct.IsCancellationRequested || !IsCurrent(request)) return Reject();
        var e = request.Report.Result.Thesis!.Events[request.EventIndex];
        var f = request.Report.Snapshot.Files.SingleOrDefault(f => f.FileId.ToString() == e.FileId);
        if (f is null) return Reject();
        var live = workspace.Files.SingleOrDefault(l => l.Id == f.FileId && !l.Deleted);
        if (live is not { MediaKind: "text" } || live.RelativePath != f.RelativePath || live.HasBom != f.HasBom
            || !TextContent.TryDecode(f.Bytes.ToArray(), out var text, out _)
            || SpanMapping.NormalizeNewlines(live.Content) != SpanMapping.NormalizeNewlines(text)) return Reject();
        var mapping = SpanMapping.Map(f,e.FileId,e.Span.StartByte,e.Span.EndByte);
        if (ct.IsCancellationRequested || !IsCurrent(request)) return Reject();
        return mapping switch {
            SpanMappingResult.Exact m => new NavigationResult.Selected(request.Report,f.FileId,f.RelativePath,
                m.TextareaStart,m.TextareaEnd,m.ExpectedNormalizedSubstring,SpanMapping.NormalizeNewlines(text),m.Line,m.Column),
            SpanMappingResult.NonSelectable m => new NavigationResult.NonSelectable(f.FileId,f.RelativePath,m.StartByte,m.EndByte,m.Line,m.Column,
                m.Reason,$"{f.RelativePath}: bytes {m.StartByte}–{m.EndByte}, line {m.Line}, byte column {m.Column}. {m.Reason}"),
            _ => Reject(),
        };
    }
    public bool IsCurrent(ThesisEvidenceRequest request)
        => request is not null && AnalysisCatalog.IsThesis(request.Report.Snapshot.Profile)
            && request.Report.Result.Thesis is { } t && !t.Events.IsDefault
            && request.EventIndex >= 0 && request.EventIndex < t.Events.Length
            && source.IsStillCurrent(request.Report);
    private static NavigationResult.Rejected Reject() => new("This thesis source evidence is not current or selectable; analyze again.");
}
