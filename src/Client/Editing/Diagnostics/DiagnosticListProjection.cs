using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Client.Analysis;
using Client.Analysis.RenderedExtent;
using Client.Compilation.Diagnostics;
using Client.Compilation.Navigation;
using Client.Compilation.Presentation;
using Client.Pdf.Preview;
using Client.Workspace.Domain;

namespace Client.Editing.Diagnostics;

public sealed partial class DiagnosticListSession
{
    // Source diagnostics retain their existing1000 guard. Status/catalog rows
    // have a separate finite presentation allowance; compiler128 is the current
    // CompileWorkspaceSession parser budget. Excess is disclosed, never "clean".
    private const int SourceStatusLimit = 64, CompilerLimit = 128;
    private bool RefreshCore()
    {
        if (_disposed) return false;
        if (_view != ViewIdentity())
        {
            _view = ViewIdentity(); _sourceReport = null; _attempt = null; _extent = null; _extentAuthority = null;
            _sourceRevoked = _compilerRevoked = _extentRevoked = _pageLost = false; _stamp = null;
        }
        var state = _analysis.State;
        if (state is AnalysisState.Completed completed && completed.Report.Snapshot.ProjectId == _workspace.Project?.Id
            && completed.Report.Snapshot.SessionId == _workspace.SessionId && !ReferenceEquals(_sourceReport, completed.Report))
        { _sourceReport = completed.Report; _sourceRevoked = false; }
        if (_compile.Attempt is { Attempt: { } accepted } presentation && accepted.SnapshotIdentity.ProjectId == _workspace.Project?.Id
            && accepted.SnapshotIdentity.SessionId == _workspace.SessionId && !ReferenceEquals(_attempt, presentation))
        { _attempt = presentation; _compilerRevoked = false; }
        if (_rendered?.Report is { } extent && !ReferenceEquals(_extent, extent))
        {
            _extent = extent; _extentRevoked = false; _pageLost = false;
            _extentAuthority = RenderedExtentAuthority.Capture(_workspace, _compile);
            _extentPreview = _compile.Preview; _extentGeneration = _extentPreview.State.Generation;
            _extentProvenanceVersion = _extentPreview.ProvenanceVersion;
        }
        bool sourceCurrent = !_sourceRevoked && _sourceReport is { } sourceReport && _source.IsStillCurrent(sourceReport);
        bool compilerCurrent = !_compilerRevoked && CompilerCurrent();
        bool extentCurrent = !_extentRevoked && ExtentCurrent();
        if (_sourceReport is not null && !sourceCurrent) _sourceRevoked = true;
        if (_attempt is not null && !compilerCurrent) _compilerRevoked = true;
        if (_extent is not null && !extentCurrent) _extentRevoked = true;
        if (_extent is not null && _extentPreview?.ProvenanceVersion != _extentProvenanceVersion) _pageLost = true;
        if (_extent is not null && !_pageLost && _extentPreview is { } preview && preview.State.Generation != _extentGeneration)
        {
            // A deliberate owned page action may advance exactly once. Any
            // other page/document refresh retires page authority until a NEW
            // narrow report. Same-hash historical/zoom/document ABA cannot
            // silently resurrect an old page request.
            if (_active?.Request is DiagnosticPageRequest own && own.Posted
                && own.ExpectedPreviewGeneration == preview.State.Generation && PagePreviewCurrent())
                _extentGeneration = preview.State.Generation;
            else _pageLost = true;
        }
        bool pageCurrent = extentCurrent && !_pageLost && PagePreviewCurrent();
        var next = new Stamp(new(_sourceReport), sourceCurrent, state.GetType().Name, new(_attempt), compilerCurrent,
            _compile.LastReason?.ToString() ?? "No attempt", new(_extent), extentCurrent, pageCurrent, new(_rendered),
            _rendered?.State.ToString() ?? "Not supplied", _rendered?.WorkType ?? "", new(_rendered?.Sections),
            _workspace.Project?.Id, _workspace.SessionId, _workspace.WorkingGeneration, _workspace.Project?.HeadRevisionId,
            _analysis.CurrentWorkspaceEpoch, _analysis.CurrentSelectionVersion, _compile.SelectionVersion,
            new(_compile.LastTicket), new(_compile.DisplayedOutput), new(_workspace.CurrentRevision));
        if (_stamp == next) return false;
        _stamp = next; Revoke(DiagnosticActionCode.Stale);
        var rows = ImmutableArray.CreateBuilder<DiagnosticRow>(); var summaries = ImmutableArray.CreateBuilder<DiagnosticOriginSummary>(); _bindings.Clear();
        AddSource(rows, summaries, sourceCurrent, state.GetType().Name);
        AddCompiler(rows, summaries, compilerCurrent);
        AddRendered(rows, summaries, extentCurrent, pageCurrent);
        _rows = rows.ToImmutable(); _summaries = summaries.ToImmutable();
        bool reset = _filter.File is { } file && file != "none" && !_rows.Any(r => r.FileId?.ToString() == file)
            || _filter.Rule is { } rule && !_rows.Any(r => r.RuleId == rule);
        if (reset)
        {
            _filter = _filter with { File = _filter.File is "none" || _rows.Any(r => r.FileId?.ToString() == _filter.File) ? _filter.File : null,
                Rule = _rows.Any(r => r.RuleId == _filter.Rule) ? _filter.Rule : null };
            _page = 0; _notice = "A file/rule filter no longer exists in the retained reports and was reset. Other filters were preserved.";
        }
        return true;
    }
    private bool CompilerCurrent()
    {
        if (_compile.Disposed || _attempt?.Attempt is not { } attempt || !ReferenceEquals(_compile.Attempt, _attempt)) return false;
        var identity = attempt.SnapshotIdentity; var state = _compile.State;
        return state is { IsAttemptCurrent: true, Disposed: false } && ReferenceEquals(state.LastAttempt, attempt)
            && _compile.LastTicket is { } ticket && ReferenceEquals(ticket.Snapshot, attempt.Snapshot)
            && ticket.Completion.IsCompletedSuccessfully && ReferenceEquals(ticket.Completion.Result.Attempt, attempt)
            && _workspace.Project?.Id == identity.ProjectId && _workspace.SessionId == identity.SessionId
            && _workspace.WorkingGeneration == identity.WorkingGeneration && _workspace.CurrentRevision?.Id == identity.BaseHeadRevisionId
            && _workspace.Project?.HeadRevisionId == identity.BaseHeadRevisionId && _compile.SelectionVersion == identity.SelectionVersion;
    }
    private bool ExtentCurrent()
    {
        if (_extentRevoked || _extent is not { } report || _rendered is null || !_rendered.ReportCurrent
            || !ReferenceEquals(_rendered.Report, report) || _extentAuthority is not { } authority || !authority.Current(_workspace, _compile)) return false;
        var output = authority.Output; var evidence = report.Evidence;
        return evidence.PdfSha256 == output.OutputHash?.Hex && evidence.PdfBytes == output.OutputLength
            && evidence.CompileRequestId == output.RequestId.ToString() && evidence.CompileEpoch == output.WorkerEpoch
            && evidence.CaptureId == output.SnapshotIdentity.CaptureId && evidence.InputDigest == output.InputContentDigest.Hex
            && report.Observation.Request.PdfSha256 == evidence.PdfSha256 && report.Observation.Request.IsValid;
    }
    private bool PagePreviewCurrent()
    {
        if (!ExtentCurrent() || _extent is not { } extent || !ReferenceEquals(_compile.Preview, _extentPreview)) return false;
        var state = _compile.Preview.State;
        // CompileWorkspaceSession.Refresh publishes genuine current output as
        // ProvidedPdf. Neither that label nor CallerSaysCurrent is authority:
        // require the live controller/report/output chain above, exact hash and
        // page count below, plus the separately latched generation proof.
        return state.Status != PdfPreviewStatus.Disposed
            && (state.Provenance is PdfDisplayProvenance.ProvidedPdf or PdfDisplayProvenance.CallerSaysCurrent)
            && state.DocumentSha256 == extent.Evidence.PdfSha256 && state.PageCount == extent.Observation.Request.PageCount;
    }
    private static int? CanonicalPage(RenderedExtentReport report, int section)
    {
        var ranges = report.Observation.Request.Sections;
        var range = section switch { 0 => ranges?.Introduction, 1 => ranges?.MainBody, 2 => ranges?.Conclusions, _ => null };
        return range is { } valid && valid.IsValidFor(report.Observation.Request.PageCount) ? valid.Start - 1 : null;
    }
    private void AddSource(ImmutableArray<DiagnosticRow>.Builder rows, ImmutableArray<DiagnosticOriginSummary>.Builder summaries, bool current, string state)
    {
        if (_sourceReport is not { } report)
        { summaries.Add(new(DiagnosticOrigin.Source, false, false, 0, 0, 0, false, "No source report supplied. Source analysis state: " + state + ".")); return; }
        int before = rows.Count; var result = report.Result;
        string prefix = $"source:{report.Snapshot.Id}:{report.RequestId}:{report.WorkerEpoch}";
        var captured = report.Snapshot.Files.ToDictionary(f => f.FileId.ToString(), StringComparer.Ordinal);
        var lives = new Dictionary<FileId, LiveSource>();
        for (int i = 0; i < Math.Min(AnalysisLimits.DMaxDiagnosticsPerProject, result.Diagnostics.Count); i++)
        {
            var diagnostic = result.Diagnostics[i]; captured.TryGetValue(diagnostic.FileId, out var file);
            LiveSource? live = null;
            if (file is not null && !lives.TryGetValue(file.FileId, out live))
            {
                var model = _workspace.Files.FirstOrDefault(w => w.Id == file.FileId && !w.Deleted && w.MediaKind == "text" && w.RelativePath == file.RelativePath && w.HasBom == file.HasBom);
                if (model is not null) { live = new(_workspace, model, file); lives.Add(file.FileId, live); }
            }
            bool located = file is not null && live is not null && diagnostic.Span is { } span && span.StartByte >= 0
                && span.EndByte >= span.StartByte && span.EndByte <= file.ByteLength;
            string reason = !current ? "Retained stale source report." : !located ? "No canonical selectable source span/file. Use the richer report for context."
                : "Canonical span is checked by SourceNavigator on action; BOM/CRLF/Unicode may make it nonselectable.";
            var citation = result.RuleResults.Take(SourceStatusLimit).FirstOrDefault(r => r.RuleId == diagnostic.RuleId)?.Source?.Citation;
            var row = Row(prefix + ":diagnostic:" + i, DiagnosticOrigin.Source, DiagnosticRowKind.Finding, diagnostic.Severity,
                diagnostic.RuleId, file?.FileId, file?.RelativePath, null, current, current && located, false, diagnostic.Message, Citation(citation), reason, null);
            rows.Add(row); _bindings.Add(row, new(Source: report, Index: i, SourceDiagnostic: diagnostic, Live: live));
        }
        for (int i = 0; i < Math.Min(SourceStatusLimit, result.RuleResults.Count); i++)
        {
            var rule = result.RuleResults[i];
            var file = rule.PerFile.Count == 1 && captured.TryGetValue(rule.PerFile[0].FileId, out var only) ? only : null;
            rows.Add(Row(prefix + ":rule:" + i, DiagnosticOrigin.Source, DiagnosticRowKind.CheckStatus, rule.Source?.Severity ?? "unknown", rule.RuleId,
                file?.FileId, file?.RelativePath, null, current, false, false,
                "Source check status: " + rule.Outcome + ". " + string.Join(", ", rule.Limitations.Take(8).Select(s => Display(s, 128))), Citation(rule.Source?.Citation),
                "Check summary has no precise diagnostic span; canonical findings/context remain in the richer report.", rule.Outcome.ToString()));
        }
        if (result.Thesis is { } thesis)
        {
            for (int i = 0; i < Math.Min(5, thesis.RuleResults.Length); i++)
            {
                var rule = thesis.RuleResults[i];
                rows.Add(Row(prefix + ":thesis-rule:" + i, DiagnosticOrigin.Source, DiagnosticRowKind.CheckStatus, rule.Severity, rule.RuleId, null, null, null,
                    current, false, false, rule.Reason + "; " + rule.Method + (rule.KnownCounterexample ? "; known literal counterexample retained" : ""), Citation(rule.Citation),
                    "No precise finding span. Use the existing thesis report's canonical context anchors; no inferred source location/fix.", rule.Outcome.ToString()));
            }
            for (int i = 0; i < Math.Min(31, thesis.UnavailableCells.Length); i++)
            {
                var cell = thesis.UnavailableCells[i];
                rows.Add(Row(prefix + ":capability:" + i, DiagnosticOrigin.Source, DiagnosticRowKind.CheckStatus, "unknown", cell.RuleId, null, null, null, current,
                    false, false, "Source catalog capability unavailable: " + cell.Reason + ". No rendered measurement is implied.", null,
                    "Required capability is unavailable; this is not a Pass or a location.", RuleOutcome.Unavailable.ToString()));
            }
        }
        int available = Count((long)result.Diagnostics.Count + result.RuleResults.Count + (result.Thesis?.RuleResults.Length ?? 0) + (result.Thesis?.UnavailableCells.Length ?? 0));
        bool limited = rows.Count - before < available;
        summaries.Add(new(DiagnosticOrigin.Source, true, current, available, rows.Count - before, result.Diagnostics.Count,
            limited || result.Coverage.DiagnosticsTruncated || result.Coverage.ResultTruncated,
            (current ? "Current source identity. Worker epoch is reverified on navigation. " : "Retained stale source findings. ")
            + "State: " + state + ". Source-only: no scholarly/global compliance claim. Unsupported/unavailable: "
            + string.Join(", ", result.UnavailableCapabilities.Take(8).Select(s => Display(s, 128))) + (limited ? ". Projection limit reached; richer report remains available." : "")));
    }
    private void AddCompiler(ImmutableArray<DiagnosticRow>.Builder rows, ImmutableArray<DiagnosticOriginSummary>.Builder summaries, bool current)
    {
        if (_attempt?.Attempt is not { } attempt)
        { summaries.Add(new(DiagnosticOrigin.Compiler, false, false, 0, 0, 0, false, "No compiler attempt report supplied.")); return; }
        int before = rows.Count; int available; bool incomplete = true;
        string prefix = $"compiler:{attempt.JobId.Value:N}:{attempt.SnapshotIdentity.CaptureId}:{attempt.RequestContext.RequestId}:{attempt.RequestContext.WorkerEpoch}";
        if (_attempt.ParserResult is CompilerLogParseResult.Parsed parsed)
        {
            var report = parsed.Report; available = report.Diagnostics.Length;
            for (int i = 0; i < Math.Min(CompilerLimit, available); i++)
            {
                var diagnostic = report.Diagnostics[i];
                var canonical = current ? CompilerSourceNavigationRequest.TryCreate(_compile, report, diagnostic) : null;
                var row = Row(prefix + ":diagnostic:" + i, DiagnosticOrigin.Compiler, DiagnosticRowKind.Finding, diagnostic.Severity.ToString(), diagnostic.StableCode,
                    diagnostic.Location?.FileId, diagnostic.Location?.RelativePath, null, current, canonical is not null, false,
                    diagnostic.SafeMessage + (diagnostic.Location is { } line ? $" Captured line {line.Line}; not token/column precision." : ""), null,
                    !current ? "Retained stale compiler attempt." : canonical is null ? "Canonical source location unavailable: " + diagnostic.LocationIssue : "Existing compiler-line policy revalidates exact bytes before navigation.", null);
                rows.Add(row); _bindings.Add(row, new(Index: i, Compiler: report, CompilerDiagnostic: diagnostic));
            }
        }
        else
        {
            available = attempt.Diagnostics.Length;
            for (int i = 0; i < Math.Min(64, available); i++)
            {
                var entry = attempt.Diagnostics[i];
                rows.Add(Row(prefix + ":transport:" + i, DiagnosticOrigin.Compiler, DiagnosticRowKind.Transport, "unknown", entry.Code, null, null, null, current,
                    false, false, entry.SafeMessage, null, "Transport-only diagnostic has no canonical source location. Log interpretation unavailable.", null));
            }
        }
        summaries.Add(new(DiagnosticOrigin.Compiler, true, current, available, rows.Count - before, available, incomplete,
            (current ? "Current" : "Retained stale") + " attempt: " + attempt.Status + ". Log completeness is unknown; no records never proves a clean compile. "
            + (_attempt.ParserResult is CompilerLogParseResult.Parsed p ? "Parser flags: " + p.Report.Flags : "Interpretation unavailable: " + _attempt.Issue)
            + (available > rows.Count - before ? ". Projection capped; no complete listing claimed." : "")));
    }
    private void AddRendered(ImmutableArray<DiagnosticRow>.Builder rows, ImmutableArray<DiagnosticOriginSummary>.Builder summaries, bool current, bool pageCurrent)
    {
        if (_extent is not { } report)
        { summaries.Add(new(DiagnosticOrigin.Rendered, false, false, 0, 0, 0, false, "No narrow rendered extent report supplied. " + (_rendered?.Notice ?? "Rendered report owner not attached."))); return; }
        int before = rows.Count; var observation = report.Observation;
        string prefix = $"rendered:{report.Evidence.CompileRequestId}:{report.Evidence.CompileEpoch}:{report.Evidence.CaptureId}:{observation.Request.RequestId}:{report.Evidence.EvaluationEpoch}";
        rows.Add(Row(prefix + ":total", DiagnosticOrigin.Rendered, DiagnosticRowKind.CheckStatus, "unknown", RenderedExtentContract.RuleId,
            null, null, null, current, false, false, $"Measured total PDF pages: {observation.Request.PageCount}; {observation.Status}; {observation.Reason}. Overall compliance is not assessed.",
            "KNU CSC 2025, §2, printed/PDF p.11 (conditional usual-length observation)", "Total extent has no reliable page-local finding or source span.", observation.Status));
        if (observation.Sections is { } sections)
        {
            var values = new[] { sections.Introduction, sections.MainBody, sections.Conclusions };
            var labels = new[] { "Introduction", "Main body", "Conclusions" };
            for (int i = 0; i < values.Length; i++)
            {
                var value = values[i]; int? page = CanonicalPage(report, i); bool action = pageCurrent && page is not null;
                var row = Row(prefix + ":section:" + i, DiagnosticOrigin.Rendered, DiagnosticRowKind.CheckStatus, "unknown", value.RuleId, null, null, page, current, false, action,
                    $"{labels[i]}: {value.Pages?.ToString() ?? "unknown"} author-labelled pages; {value.Status}. Labels are author input, not verified structure or physical layout.",
                    "KNU CSC 2025, §2, printed/PDF p.11 (author-labelled scope)", !current ? "Retained stale rendered observation." : page is null
                        ? "No canonical author-labelled page range." : !pageCurrent ? "Current compiled-PDF/preview authority unavailable or changed; check the current PDF again."
                        : "Open the validated author-labelled start page, not an inferred source/fix location.", value.Status);
                rows.Add(row); _bindings.Add(row, new(Section: i));
            }
        }
        summaries.Add(new(DiagnosticOrigin.Rendered, true, current, rows.Count - before, rows.Count - before, 0, true,
            (current ? "Current" : "Retained stale") + " narrow extent report. " + RenderedExtentContract.Coverage + " No UserUnit, glyph ink, style or physical-rule capability is supplied."));
    }
    private static DiagnosticRow Row(string id, DiagnosticOrigin origin, DiagnosticRowKind kind, string severity, string rule, FileId? file, string? path,
        int? page, bool current, bool source, bool pageAction, string message, string? citation, string reason, string? outcome)
    {
        bool validMetadata = rule is { Length: > 0 and <= 256 } && severity is { Length: <= 64 };
        return new(id, origin, kind, Severity(severity), Display(severity, 64), validMetadata ? rule : "[unavailable rule metadata]", file, path, page, current,
            source && validMetadata, pageAction && validMetadata, Display(message, 2048), citation, validMetadata ? reason : "Canonical display metadata is invalid; navigation unavailable.", outcome);
    }
    private static DiagnosticSeverity Severity(string? value) => (value is { Length: <= 64 } ? value.ToLowerInvariant() : null) switch
    { "error" => DiagnosticSeverity.Error, "warning" => DiagnosticSeverity.Warning, "info" or "information" => DiagnosticSeverity.Information, "hint" => DiagnosticSeverity.Hint, _ => DiagnosticSeverity.Unknown };
    private static string? Citation(AnalysisCitationDto? value) => value is null ? null
        : "Guideline §" + Display(value.Section, 128) + "; printed pages " + string.Join(", ", value.PrintedPages.Take(16)) + "; PDF pages " + string.Join(", ", value.PdfPages.Take(16));
    private static string Display(string? text, int max)
    {
        if (text is null) return "unavailable"; if (text.Length <= max) return text;
        int length = max; if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + "… [display truncated]";
    }
    private static int Count(long n) => (int)Math.Min(int.MaxValue, n);
    private readonly struct Ref(object? value) : IEquatable<Ref>
    {
        public bool Equals(Ref other) => ReferenceEquals(value, other.Value);
        private object? Value => value;
        public override bool Equals(object? other) => other is Ref r && Equals(r);
        public override int GetHashCode() => value is null ? 0 : RuntimeHelpers.GetHashCode(value);
    }
    private sealed record Stamp(Ref Source, bool SourceCurrent, string SourceState, Ref Attempt, bool CompilerCurrent, string CompilerState,
        Ref Extent, bool ExtentCurrent, bool PageCurrent, Ref RenderOwner, string RenderState, string WorkType, Ref Sections,
        ProjectId? Project, Guid Session, long Generation, RevisionId? Head, long SourceEpoch, long SelectionVersion, long CompileSelection,
        Ref Ticket, Ref Output, Ref Revision);
}
