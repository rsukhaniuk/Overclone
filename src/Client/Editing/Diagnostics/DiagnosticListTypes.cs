using System.Collections.Immutable;
using Client.Analysis;
using Client.Analysis.RenderedExtent;
using Client.Compilation.Navigation;
using Client.Compilation.Presentation;
using Client.Pdf.Preview;
using Client.Workspace.Domain;

namespace Client.Editing.Diagnostics;

public enum DiagnosticOrigin { Source, Compiler, Rendered }
public enum DiagnosticSeverity { Error, Warning, Information, Hint, Unknown }
public enum DiagnosticRowKind { Finding, CheckStatus, Transport }
public enum DiagnosticCurrentness { All, Current, Stale }
public sealed record DiagnosticFilter(DiagnosticOrigin? Origin = null, DiagnosticSeverity? Severity = null,
    string? File = null, string? Rule = null, DiagnosticCurrentness Currentness = DiagnosticCurrentness.All);
public sealed record DiagnosticChoice(string Value, string Label);
public sealed record DiagnosticOriginSummary(DiagnosticOrigin Origin, bool HasReport, bool IsCurrent,
    int AvailableRows, int RetainedRows, int FindingOccurrences, bool Incomplete, string Notice);

/// <summary>Immutable display data, not authority. No public constructor/clone,
/// mutable input DTO, source/PDF payload or independently applicable edit.</summary>
public sealed class DiagnosticRow
{
    internal DiagnosticRow(string id, DiagnosticOrigin origin, DiagnosticRowKind kind, DiagnosticSeverity severity,
        string severityText, string ruleId, FileId? fileId, string? path, int? pageIndex, bool current,
        bool source, bool page, string message, string? citation, string navigationReason, string? outcome)
    { Id = id; Origin = origin; Kind = kind; Severity = severity; SeverityText = severityText; RuleId = ruleId;
        FileId = fileId; RelativePath = path; PageIndex = pageIndex; IsCurrent = current;
        HasSourceAction = source; HasPageAction = page; Message = message; Citation = citation;
        NavigationReason = navigationReason; Outcome = outcome; }
    public string Id { get; }
    public DiagnosticOrigin Origin { get; }
    public DiagnosticRowKind Kind { get; }
    public DiagnosticSeverity Severity { get; }
    public string SeverityText { get; }
    public string RuleId { get; }
    public FileId? FileId { get; }
    public string? RelativePath { get; }
    public int? PageIndex { get; }
    public bool IsCurrent { get; }
    public bool HasSourceAction { get; }
    public bool HasPageAction { get; }
    public string Message { get; }
    public string? Citation { get; }
    public string NavigationReason { get; }
    public string? Outcome { get; }
}

public sealed record DiagnosticListView(ImmutableArray<DiagnosticRow> Rows, ImmutableArray<DiagnosticOriginSummary> Origins,
    ImmutableArray<DiagnosticChoice> Files, ImmutableArray<string> Rules, DiagnosticFilter Filter,
    int ProjectedCount, int FilteredCount, int PageIndex, int PageSize, bool HasPrevious, bool HasNext,
    bool Busy, string Notice);
public enum DiagnosticActionCode { Presented, Busy, Unavailable, Stale, Cancelled, Disposed }
public sealed record DiagnosticActionResult(DiagnosticActionCode Code, string Notice);

/// <summary>A request is session-owned and valid only for its admitted callback.
/// Consumers recheck IsCurrent at every shared render/DOM boundary and use Token
/// to link only their own navigation. Never cancel another feature's preview.</summary>
public abstract class DiagnosticNavigationRequest
{
    internal DiagnosticNavigationRequest(DiagnosticListSession owner, DiagnosticRow row, CancellationToken token)
    { Owner = owner; Row = row; Token = token; }
    internal DiagnosticListSession Owner { get; }
    public DiagnosticRow Row { get; }
    public CancellationToken Token { get; }
    public bool IsCurrent => Owner.IsCurrent(this);
}

public sealed class DiagnosticSourceRequest : DiagnosticNavigationRequest
{
    internal DiagnosticSourceRequest(DiagnosticListSession owner, DiagnosticRow row, AnalysisReport report,
        int index, AnalysisEngineDiagnosticDto diagnostic, CancellationToken token) : base(owner, row, token)
    { Report = report; DiagnosticIndex = index; Diagnostic = diagnostic; }
    public AnalysisReport Report { get; }
    public int DiagnosticIndex { get; }
    public AnalysisEngineDiagnosticDto Diagnostic { get; }
    public Task<NavigationResult> PrepareAsync() => Owner.PrepareSourceAsync(this);
}

public sealed class DiagnosticCompilerRequest : DiagnosticNavigationRequest
{
    internal DiagnosticCompilerRequest(DiagnosticListSession owner, DiagnosticRow row, int index,
        ValidatedCompileAttempt attempt, CompilerSourceNavigationRequest canonical, CancellationToken token) : base(owner, row, token)
    { DiagnosticIndex = index; Attempt = attempt; Canonical = canonical; }
    public int DiagnosticIndex { get; }
    public ValidatedCompileAttempt Attempt { get; }
    public CompilerSourceNavigationRequest Canonical { get; }
    public Task<CompilerSourceNavigationPolicy.Selection?> PrepareAsync() => Owner.PrepareCompilerAsync(this);
}

public sealed class DiagnosticPageRequest : DiagnosticNavigationRequest
{
    internal DiagnosticPageRequest(DiagnosticListSession owner, DiagnosticRow row, RenderedExtentReport report,
        int sectionIndex, int pageIndex, PdfPreviewController preview, long generation, CancellationToken token) : base(owner, row, token)
    { Report = report; SectionIndex = sectionIndex; PageIndex = pageIndex; Preview = preview; ExpectedPreviewGeneration = generation; }
    public RenderedExtentReport Report { get; }
    public int SectionIndex { get; }
    public int PageIndex { get; }
    internal PdfPreviewController Preview { get; }
    internal long ExpectedPreviewGeneration;
    internal bool Posted, CancelRequested;
    /// <summary>Only the canonical author-labelled section start is used. This
    /// awaits the actual existing preview pump, not a cancelled wait proxy.</summary>
    public Task<bool> GoToPageAsync() => Owner.GoToPageAsync(this);
}
