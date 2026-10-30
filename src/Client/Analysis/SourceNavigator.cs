using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;

namespace Client.Analysis;

// SourceNavigator (M2a-2b-2b) is a small independently-testable C# helper that
// turns a diagnostic in the CURRENT Completed report into a guarded textarea
// selection (or an honest non-selectable/rejected notice). It owns NO parser,
// mapper or scheduler: it reuses the ACCEPTED SpanMapping (Map A bytes->source
// UTF-16, Map B source->textarea under DOM newline normalization) and the
// ACCEPTED AnalysisCoordinator/AnalysisSnapshotFactory freshness identity. All
// encoding, source-mapping, rule/freshness policy stays in C#; the only JS is a
// tiny DOM-glue module (editor-select.js) that does final DOM checks +
// focus/setSelectionRange and returns a bool.
//
// GUARANTEES (see docs/milestones/m2-source-analysis.md):
// - A click references a diagnostic actually IN the current Completed report.
//   The diagnostic is RESOLVED FROM report.Result.Diagnostics by reference, then
//   by stable Id, and the CANONICAL object's file/span/location/message fields
//   are used for ALL subsequent work. A passed-in clone whose Id matches but
//   whose span/file differ CANNOT forge a different selection: the canonical
//   span/file win. The original immutable diagnostic in the report is unchanged.
// - The report is confirmed STILL current (ReferenceEquals the live coordinator
//   State) before AND after every await, including the awaited worker-epoch
//   check. An already-cancelled or now-cancelled navigation token rejects even
//   if a fake epoch query ignores the token.
// - Current project/session/workingGeneration/factory localEpoch/profile are
//   checked against the snapshot BEFORE the worker-epoch await and RE-CHECKED
//   after it (TOCTOU). A wrong file / missing/deleted file / invalid span /
//   pre-reset worker epoch is rejected with no selection and a safe notice.
// - Authoritative spans remain the EXISTING mapper's BOM-inclusive half-open
//   UTF-8 byte spans; SourceText is derived from the exact snapshot bytes via
//   the existing TextContent.TryDecode (BOM stripped, CRLF/CR unchanged); the
//   expected textarea text is SpanMapping.NormalizeNewlines(SourceText). The
//   live WorkingFile.Content is NEVER used to derive offsets and the source
//   bytes/text are NEVER modified.
// - Exact selection returns the target FileId + textarea [start,end) + the
//   expected normalized substring; the page switches the active file (via
//   WorkspaceCoordinator.SelectFile), waits for the target textarea to render,
//   re-verifies freshness immediately before selection, then calls editor-select
//   trySelect which atomically compares data-file-id AND value, then focuses +
//   setSelectionRange and re-verifies selectionStart/End + substring.
// - NonSelectable (BOM / boundary inside CRLF) shows file/line/exact-byte
//   span/expanded location and NEVER calls setSelectionRange; InvalidSpan is
//   not selectable. The expanded display is a BOUNDED LINE EXCERPT derived from
//   the decoded source text and the mapper's line number — byte offsets are
//   NEVER treated as UTF-16 char indices, so multi-byte text near EOF cannot
//   throw ArgumentOutOfRangeException. No invented "adjusted" offsets.
// - Read-only / editor-disabled behavior stays safe: analysis works and an
//   inaccessible selection still shows a location without granting writes.

/// <summary>Typed navigation outcome. <see cref="Selected"/> carries the
/// information the page needs to drive the final JS selection (after rendering
/// the target file and re-checking freshness). <see cref="NonSelectable"/>
/// carries an expanded display only (no selection). <see cref="Rejected"/>
/// means no selection is attempted and a safe notice is shown.</summary>
public abstract record NavigationResult
{
    private NavigationResult() { }

    public sealed record Selected(
        AnalysisReport Report,
        FileId FileId,
        string RelativePath,
        int TextareaStart,
        int TextareaEnd,
        string ExpectedNormalizedSubstring,
        string ExpectedTextareaText,
        int Line,
        int Column) : NavigationResult;

    public sealed record NonSelectable(
        FileId FileId,
        string RelativePath,
        long StartByte,
        long EndByte,
        int Line,
        int Column,
        string Reason,
        string Display) : NavigationResult;

    public sealed record Rejected(string Reason) : NavigationResult;
}

/// <summary>Reusable report-verification result: <see cref="Ok"/> true means the
/// report is still the current Completed report, the workspace identity still
/// matches, and the worker epoch is still current; otherwise <see cref="Reason"/>
/// explains why for a safe user notice. Used by both NavigateAsync and the
/// page's final pre-DOM verification (after the module import / render).</summary>
public sealed record VerifyResult(bool Ok, string? Reason)
{
    internal static readonly VerifyResult Cancelled =
        new(false, "Analysis was cancelled while verifying the worker.");
}

/// <summary>Guarded click-to-source helper over the ACCEPTED coordinator,
/// workspace and analyzer. Stateless beyond its injected dependencies; safe to
/// resolve from DI (scoped) and reuse across page mounts.</summary>
public sealed class SourceNavigator
{
    /// <summary>Dedicated authoring reports have their own captured/session
    /// authority, not a fabricated AnalysisCoordinator report or running-worker
    /// epoch. Keep exact source/preimage/map/DOM guards; navigation grants no edit.</summary>
    public SpanMappingResult? VerifyAuthoringNavigation(Client.Editing.Assistance.AuthoringAssistNavigation request)
    {
        if (!request.IsCurrent()) return null;
        var captured = request.File;
        var live = _workspace.Files.SingleOrDefault(f => f.Id == captured.FileId && !f.Deleted);
        if (live is not { MediaKind: "text" } || live.RelativePath != captured.RelativePath || live.HasBom != captured.HasBom
            || !TextContent.TryDecode(captured.Bytes.ToArray(), out var text, out _)
            || SpanMapping.NormalizeNewlines(live.Content) != SpanMapping.NormalizeNewlines(text)) return null;
        var mapping = SpanMapping.Map(captured, captured.FileId.ToString(), request.Span.StartByte, request.Span.EndByte);
        return request.IsCurrent() ? mapping : null;
    }

    private const int MaxExcerptChars = 120;

    private readonly AnalysisCoordinator _analysis;
    private readonly WorkspaceCoordinator _workspace;
    private readonly ISourceAnalyzer _analyzer;

    public SourceNavigator(AnalysisCoordinator analysis, WorkspaceCoordinator workspace, ISourceAnalyzer analyzer)
    {
        _analysis = analysis;
        _workspace = workspace;
        _analyzer = analyzer;
    }

    /// <summary>Resolve a diagnostic in the CURRENT Completed report to a
    /// guarded selection / non-selectable display / rejection. Performs the
    /// worker-epoch await and re-checks identity before AND after it. Never
    /// modifies source bytes/text and never selects on a stale report. An
    /// already-cancelled token rejects immediately, even if the analyzer ignores
    /// the token during the epoch await.</summary>
    public async Task<NavigationResult> NavigateAsync(
        AnalysisReport report,
        AnalysisEngineDiagnosticDto diag,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(diag);

        // An already-cancelled navigation must reject even if a fake epoch query
        // ignores the token (the awaited epoch helper re-checks this too).
        if (ct.IsCancellationRequested)
            return new NavigationResult.Rejected("This navigation was cancelled.");

        // 1. The report must be the CURRENT Completed report (cheap synchronous
        //    reject before resolving the canonical diagnostic).
        if (CurrentCompletedReport() is not { } cur || !ReferenceEquals(cur, report))
            return new NavigationResult.Rejected("This engine report is no longer current; run analysis again.");

        // 2. Resolve the CANONICAL diagnostic FROM report.Result.Diagnostics by
        //    reference, then by stable Id. The canonical object's fields are
        //    authoritative; a passed-in clone with a matching Id but a different
        //    span/file cannot forge a different selection.
        var canonical = ResolveCanonical(report, diag);
        if (canonical is null)
            return new NavigationResult.Rejected("This diagnostic is not part of the current report.");

        // 3. Awaited worker-epoch verification: checks the report is STILL
        //    current, the workspace identity matches, the epoch is current —
        //    BEFORE and AFTER the awaited epoch query (TOCTOU). Also rejects on a
        //    now-cancelled token. This is the SAME helper the page re-runs after
        //    the DOM module import / render, so freshness is verified at every
        //    await boundary.
        var v = await VerifyCurrentAsync(report, ct).ConfigureAwait(false);
        if (!v.Ok)
            return new NavigationResult.Rejected(v.Reason ?? "This analysis is no longer current; run analysis again.");

        // 4. The diagnostic's file must be in the analyzed snapshot AND still
        //    present (not deleted) in the live workspace. Uses the CANONICAL
        //    file id, never the caller's.
        var captured = report.Snapshot.Files.FirstOrDefault(f => f.FileId.ToString() == canonical.FileId);
        if (captured is null)
            return new NavigationResult.Rejected("The diagnostic's file is not in the analyzed snapshot.");
        if (_workspace.Files.All(f => f.Id.ToString() != canonical.FileId || f.Deleted))
            return new NavigationResult.Rejected("The diagnostic's file was removed; run analysis again.");

        // 5. Map the CANONICAL byte span (BOM-inclusive half-open UTF-8) ->
        //    textarea range. The canonical diagnostic's PrimaryLocation is used
        //    only for the spanless location display.
        if (canonical.Span is null)
        {
            int line = canonical.PrimaryLocation?.Line ?? 0;
            int col = canonical.PrimaryLocation?.Column ?? 0;
            return new NavigationResult.NonSelectable(
                captured.FileId, captured.RelativePath, 0, 0, line, col,
                "no source span", FormatLocation(captured.RelativePath, line, col, 0, 0, null));
        }
        var map = SpanMapping.Map(captured, canonical.FileId, canonical.Span.StartByte, canonical.Span.EndByte);
        return map switch
        {
            SpanMappingResult.Exact e => new NavigationResult.Selected(
                report, captured.FileId, captured.RelativePath,
                e.TextareaStart, e.TextareaEnd, e.ExpectedNormalizedSubstring, ExpectedTextareaText(captured), e.Line, e.Column),
            SpanMappingResult.NonSelectable ns => new NavigationResult.NonSelectable(
                captured.FileId, captured.RelativePath, ns.StartByte, ns.EndByte, ns.Line, ns.Column,
                ns.Reason, FormatLocation(captured.RelativePath, ns.Line, ns.Column, ns.StartByte, ns.EndByte, ExpandAround(captured, ns.Line))),
            SpanMappingResult.InvalidSpan iv => new NavigationResult.Rejected(
                $"The diagnostic span is not selectable: {iv.Reason}"),
            _ => new NavigationResult.Rejected("The diagnostic span could not be mapped."),
        };
    }

    /// <summary>Reusable async report verification: the report is STILL the
    /// current Completed report (ReferenceEquals), the workspace identity still
    /// matches, and the worker epoch is still current — checked BEFORE and AFTER
    /// the awaited <see cref="ISourceAnalyzer.IsCurrentWorkerEpochAsync"/>. An
    /// already-cancelled or now-cancelled token rejects (even if the analyzer
    /// ignores the token). The page re-runs this AFTER the DOM module import /
    /// render, immediately before the final synchronous DOM checks, so every
    /// await boundary re-verifies freshness.</summary>
    public async Task<VerifyResult> VerifyCurrentAsync(AnalysisReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (ct.IsCancellationRequested) return VerifyResult.Cancelled;

        // Sync pre-check: report currency + workspace identity (no await).
        if (CurrentCompletedReport() is not { } cur || !ReferenceEquals(cur, report))
            return new VerifyResult(false, "A newer report became current; run analysis again.");
        if (!IdentityMatches(report.Snapshot))
            return new VerifyResult(false, "The workspace changed after this analysis; run analysis again.");

        // Awaited worker-epoch query. A fake analyzer that ignores the token
        // still cannot make this pass: the now-cancelled re-check below rejects.
        bool epochCurrent;
        try { epochCurrent = await _analyzer.IsCurrentWorkerEpochAsync(report.WorkerEpoch, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return VerifyResult.Cancelled; }
        catch (Exception) { return new VerifyResult(false, "The analysis worker could not be verified; run analysis again."); }

        if (ct.IsCancellationRequested) return VerifyResult.Cancelled;

        // Sync post-check (TOCTOU): a newer report / workspace change during the
        // await drops this report.
        if (CurrentCompletedReport() is not { } cur2 || !ReferenceEquals(cur2, report))
            return new VerifyResult(false, "A newer report became current while verifying the worker; run analysis again.");
        if (!IdentityMatches(report.Snapshot))
            return new VerifyResult(false, "The workspace changed while verifying the worker; run analysis again.");
        if (!epochCurrent)
            return new VerifyResult(false, "The analysis worker was reset since this report; run analysis again.");

        return new VerifyResult(true, null);
    }

    /// <summary>Synchronous post-render re-check that the report is STILL the
    /// current Completed report AND the workspace identity still matches. Called
    /// by the page immediately before the final JS selection (after the target
    /// textarea has rendered and after the awaited verification). No await, no
    /// epoch re-check (the epoch was just verified in
    /// <see cref="VerifyCurrentAsync"/>; an edit after that bumps the intent and
    /// the report is no longer current, caught here).</summary>
    public bool IsStillCurrent(AnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (CurrentCompletedReport() is not { } cur) return false;
        if (!ReferenceEquals(cur, report)) return false;
        return IdentityMatches(report.Snapshot);
    }

    private AnalysisReport? CurrentCompletedReport()
        => _analysis.State is AnalysisState.Completed c ? c.Report : null;

    /// <summary>Resolve the canonical diagnostic from
    /// report.Result.Diagnostics: by reference first (the common page click path
    /// passes the exact object), then by stable Id. Returns null when no
    /// diagnostic in the report matches. The returned canonical object's fields
    /// are authoritative for ALL file/span/location/message work.</summary>
    private static AnalysisEngineDiagnosticDto? ResolveCanonical(AnalysisReport report, AnalysisEngineDiagnosticDto diag)
    {
        var diags = report.Result.Diagnostics;
        for (int i = 0; i < diags.Count; i++)
            if (ReferenceEquals(diags[i], diag)) return diags[i];
        for (int i = 0; i < diags.Count; i++)
            if (diags[i].Id == diag.Id) return diags[i];
        return null;
    }

    private bool IdentityMatches(AnalysisSnapshot snap)
    {
        if (_workspace.Project is not { } p) return false;
        return p.Id == snap.ProjectId
            && _workspace.SessionId == snap.SessionId
            && _workspace.WorkingGeneration == snap.WorkingGeneration
            && _analysis.CurrentWorkspaceEpoch == snap.LocalEpoch
            && _workspace.CurrentRevision?.Id == snap.BaseHeadRevisionId
            && _analysis.CurrentSelection == snap.Selection
            && _analysis.CurrentSelectionVersion == snap.SelectionVersion;
    }

    private static string FormatLocation(string path, int line, int column, long startByte, long endByte, string? excerpt)
    {
        var core = line > 0
            ? $"{path}: line {line}, byte column {column}"
            : $"{path}";
        if (startByte != 0 || endByte != 0)
            core += $" (bytes {startByte}\u2013{endByte})";
        if (!string.IsNullOrEmpty(excerpt))
            core += $": {excerpt}";
        return core;
    }

    /// <summary>A bounded LINE excerpt (display only, never used for selection)
    /// derived from the decoded source text and the mapper's 1-based line
    /// number. Byte offsets are NEVER treated as UTF-16 char indices, so
    /// multi-byte text near EOF cannot throw ArgumentOutOfRangeException. The
    /// line is found using the SAME newline semantics as
    /// SpanMapping.LineColumn (CRLF is one break at \n, lone \r is one break).
    /// The excerpt is capped to <see cref="MaxExcerptChars"/> characters and
    /// newlines are replaced with a visible glyph so it stays on one line.</summary>
    private static string ExpandAround(CapturedFile f, int line)
    {
        if (line <= 0) return string.Empty;
        if (!TextContent.TryDecode(ToByteArray(f.Bytes), out var text, out _)) return string.Empty;

        int curLine = 1;
        int lineStart = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (curLine == line) break;
            char c = text[i];
            if (c == '\n') { curLine++; lineStart = i + 1; }
            else if (c == '\r' && !(i + 1 < text.Length && text[i + 1] == '\n')) { curLine++; lineStart = i + 1; }
            // A \r immediately followed by \n defers the break to the \n.
        }
        if (curLine != line) return string.Empty; // line beyond EOF

        int end = lineStart;
        while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;

        int len = end - lineStart;
        bool truncated = len > MaxExcerptChars;
        if (truncated) len = MaxExcerptChars;
        var excerpt = text.Substring(lineStart, len);
        if (truncated) excerpt += "\u2026";
        return excerpt.Replace('\r', '\u21a9').Replace('\n', '\u21b5');
    }

    /// <summary>The full normalized textarea text the DOM must hold for this
    /// file before any selection: NormalizeNewlines(SourceText) where SourceText
    /// is strict-decoded (BOM stripped, CRLF/CR preserved) from the exact
    /// snapshot bytes. Used by the page for the final atomic DOM value check.</summary>
    private static string ExpectedTextareaText(CapturedFile f)
    {
        if (!TextContent.TryDecode(ToByteArray(f.Bytes), out var text, out _)) return string.Empty;
        return SpanMapping.NormalizeNewlines(text);
    }

    private static byte[] ToByteArray(System.Collections.Immutable.ImmutableArray<byte> bytes)
    {
        var a = new byte[bytes.Length];
        bytes.CopyTo(a);
        return a;
    }
}
