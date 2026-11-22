using System.Collections.Immutable;
using Client.Analysis;
using Client.Analysis.RenderedExtent;
using Client.Compilation.Diagnostics;
using Client.Compilation.Navigation;
using Client.Compilation.Presentation;
using Client.Pdf.Preview;
using Client.Workspace;
using Client.Workspace.Domain;

namespace Client.Editing.Diagnostics;

/// <summary>UI-owner-context projection over borrowed canonical owners. No
/// analysis/compile/save/IO dispatch on refresh/filter. One retained report per
/// origin, bounded rows and paged rendering; not a diagnostic or rule engine.</summary>
public sealed partial class DiagnosticListSession : IDisposable
{
    public const int PageSize = 50;
    private readonly WorkspaceCoordinator _workspace;
    private readonly AnalysisCoordinator _analysis;
    private readonly SourceNavigator _source;
    private readonly CompileWorkspaceSession _compile;
    private readonly CompilerSourceNavigationPolicy _compiler;
    private readonly Func<Action, Task> _dispatch;
    private readonly SynchronizationContext? _context;
    private readonly int _thread;
    private readonly object _refreshGate = new();
    private bool _refreshQueued;
    private Task _refreshTask = Task.CompletedTask;
    private RenderedExtentController? _rendered;
    private AnalysisReport? _sourceReport;
    private CompileAttemptPresentation? _attempt;
    private RenderedExtentReport? _extent;
    private RenderedExtentAuthority? _extentAuthority;
    private PdfPreviewController? _extentPreview;
    private long _extentGeneration, _extentProvenanceVersion;
    private bool _sourceRevoked, _compilerRevoked, _extentRevoked, _pageLost, _disposed;
    private (ProjectId? Project, Guid Session) _view;
    private Stamp? _stamp;
    private ImmutableArray<DiagnosticRow> _rows = [];
    private ImmutableArray<DiagnosticOriginSummary> _summaries = [];
    private readonly Dictionary<DiagnosticRow, Binding> _bindings = [];
    private DiagnosticFilter _filter = new();
    private int _page;
    private long _epoch;
    private Operation? _active;
    private string _notice = "Filters do not run checks or change files. Absence of findings does not establish compliance.";
    public event Action? Changed;
    public bool Busy => _active is not null;

    public DiagnosticListSession(WorkspaceCoordinator workspace, AnalysisCoordinator analysis, SourceNavigator source,
        CompileWorkspaceSession compile, RenderedExtentController? rendered = null, Func<Action, Task>? dispatch = null)
    {
        _workspace = workspace; _analysis = analysis; _source = source; _compile = compile;
        _context = SynchronizationContext.Current; _thread = Environment.CurrentManagedThreadId;
        _dispatch = dispatch ?? (action =>
        {
            if (_context is null) return Task.FromException(new InvalidOperationException("A UI dispatcher is required for foreign notifications."));
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _context.Post(_ => { try { action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } }, null);
            return done.Task;
        });
        _compiler = new(workspace, compile); _view = ViewIdentity();
        workspace.WorkspaceChanged += QueueRefresh; workspace.RevisionCommitted += Saved;
        analysis.Changed += AnalysisChanged; compile.Changed += QueueRefresh;
        AttachRendered(rendered); RefreshCore();
    }
    public void AttachRendered(RenderedExtentController? controller)
    {
        if (_disposed || ReferenceEquals(controller, _rendered)) return;
        if (_rendered is not null) _rendered.Changed -= QueueRefresh;
        _rendered = controller; _extentRevoked = true; _pageLost = true; _extentAuthority = null;
        if (controller is not null) controller.Changed += QueueRefresh;
        Refresh();
    }
    private (ProjectId?, Guid) ViewIdentity() => (_workspace.Project?.Id, _workspace.SessionId);
    private void Saved(WorkspaceSaveReceipt _) => QueueRefresh();
    private void AnalysisChanged(AnalysisState _) => QueueRefresh();
    private void QueueRefresh()
    {
        if (_disposed) return;
        if (Environment.CurrentManagedThreadId == _thread && ReferenceEquals(SynchronizationContext.Current, _context)) { Refresh(); return; }
        lock (_refreshGate) { if (_refreshQueued) return; _refreshQueued = true; }
        try
        {
            var actual = _dispatch(() => { if (!_disposed) Refresh(); });
            lock (_refreshGate) _refreshTask = actual;
            _ = actual.ContinueWith(task =>
            {
                _ = task.Exception;
                lock (_refreshGate) { if (ReferenceEquals(_refreshTask, actual)) _refreshQueued = false; }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { lock (_refreshGate) { _refreshQueued = false; _refreshTask = Task.CompletedTask; } }
    }
    private void Notify()
    { if (_disposed) return; foreach (var observer in Changed?.GetInvocationList().Cast<Action>().ToArray() ?? []) try { observer(); } catch { /* isolate views */ } }
    public void Refresh() { if (_disposed) return; if (RefreshCore()) Notify(); }
    private void Revoke(DiagnosticActionCode reason)
    {
        _epoch++;
        if (_active is not { } op) return;
        op.Revoked ??= reason; op.Stop.Cancel();
        if (op.Request is DiagnosticPageRequest page && page.Posted && !page.CancelRequested
            && page.Preview.ActualWorkPending && page.Preview.State.Generation == page.ExpectedPreviewGeneration
            && page.Preview.State.DocumentSha256 == page.Report.Evidence.PdfSha256)
        {
            page.CancelRequested = true;
            page.Preview.Cancel(); // exact owned generation only; never a newer feature's selection
        }
    }
    public void Cancel() { if (_disposed) return; Revoke(DiagnosticActionCode.Cancelled); _notice = "Navigation cancelled. Any started callback retains admission until it actually settles."; Notify(); }

    public DiagnosticListView Snapshot()
    {
        if (!_disposed) RefreshCore();
        var all = _rows.Where(Matches).ToArray();
        int pages = Math.Max(1, (all.Length + PageSize - 1) / PageSize); _page = Math.Clamp(_page, 0, pages - 1);
        var files = _rows.Where(r => r.FileId is not null).GroupBy(r => r.FileId!.Value).Select(g => new DiagnosticChoice(g.Key.ToString(),
            (g.First().RelativePath ?? g.Key.ToString()) + (g.Select(r => r.RelativePath).Distinct().Skip(1).Any() ? " (multiple captured paths)" : "")))
            .OrderBy(c => c.Label, StringComparer.Ordinal).ToImmutableArray();
        return new(all.Skip(_page * PageSize).Take(PageSize).ToImmutableArray(), _summaries, files,
            _rows.Select(r => r.RuleId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(), _filter,
            _rows.Length, all.Length, _page, PageSize, _page > 0, (_page + 1) * PageSize < all.Length, Busy, _notice);
    }
    private bool Matches(DiagnosticRow row) => (_filter.Origin is null || _filter.Origin == row.Origin)
        && (_filter.Severity is null || _filter.Severity == row.Severity)
        && (_filter.File is null || _filter.File == (row.FileId?.ToString() ?? "none"))
        && (_filter.Rule is null || _filter.Rule == row.RuleId)
        && (_filter.Currentness == DiagnosticCurrentness.All || row.IsCurrent == (_filter.Currentness == DiagnosticCurrentness.Current));
    public bool SetFilter(DiagnosticFilter filter)
    {
        if (_disposed || filter is null || filter.Origin is { } origin && !Enum.IsDefined(origin)
            || filter.Severity is { } severity && !Enum.IsDefined(severity) || !Enum.IsDefined(filter.Currentness)) return false;
        RefreshCore();
        if (filter.File is { } file && file != "none" && !_rows.Any(r => r.FileId?.ToString() == file)
            || filter.Rule is { } rule && !_rows.Any(r => r.RuleId == rule)) return false;
        if (filter == _filter) return true;
        _filter = filter; _page = 0; Revoke(DiagnosticActionCode.Stale); Notify(); return true;
    }
    public void ResetFilters() => SetFilter(new());
    public void Next() { if (Snapshot().HasNext) { _page++; Revoke(DiagnosticActionCode.Stale); Notify(); } }
    public void Previous() { if (Snapshot().HasPrevious) { _page--; Revoke(DiagnosticActionCode.Stale); Notify(); } }

    private bool OwnedVisible(DiagnosticRow row) => Snapshot().Rows.Any(r => ReferenceEquals(r, row));
    public bool IsCurrent(DiagnosticNavigationRequest request)
    {
        if (_disposed || !ReferenceEquals(request.Owner, this) || _active is not { } op || !ReferenceEquals(op.Request, request)
            || op.Revoked is not null || op.Epoch != _epoch || request.Token.IsCancellationRequested) return false;
        if (!OwnedVisible(request.Row) || !request.Row.IsCurrent || !_bindings.TryGetValue(request.Row, out var binding)) return false;
        if (request is DiagnosticSourceRequest source)
            return !_sourceRevoked && ReferenceEquals(source.Report, _sourceReport) && _source.IsStillCurrent(source.Report)
                && source.DiagnosticIndex == binding.Index && source.DiagnosticIndex >= 0 && source.DiagnosticIndex < source.Report.Result.Diagnostics.Count
                && ReferenceEquals(source.Report.Result.Diagnostics[source.DiagnosticIndex], source.Diagnostic) && binding.Live?.Current == true;
        if (request is DiagnosticCompilerRequest compiler)
            return !_compilerRevoked && ReferenceEquals(compiler.Attempt, _attempt?.Attempt) && compiler.DiagnosticIndex == binding.Index && ReferenceEquals(compiler.Canonical.Report, binding.Compiler)
                && _compiler.IsCurrent(compiler.Canonical, request.Token);
        return request is DiagnosticPageRequest page && !_pageLost && ExtentCurrent() && PagePreviewCurrent()
            && ReferenceEquals(page.Report, _extent) && CanonicalPage(page.Report, page.SectionIndex) == page.PageIndex
            && _compile.Preview.State.Generation == page.ExpectedPreviewGeneration;
    }

    public Task<DiagnosticActionResult> OpenSourceAsync(DiagnosticRow row, Func<DiagnosticSourceRequest, Task> callback, CancellationToken ct = default)
        => RunAsync(row, callback, ct, (binding, token) => binding.Source is { } report && binding.SourceDiagnostic is { } diagnostic
            ? new DiagnosticSourceRequest(this, row, report, binding.Index, diagnostic, token) : null);
    public Task<DiagnosticActionResult> OpenCompilerAsync(DiagnosticRow row, Func<DiagnosticCompilerRequest, Task> callback, CancellationToken ct = default)
        => RunAsync(row, callback, ct, (binding, token) => binding.Compiler is { } report && binding.CompilerDiagnostic is { } diagnostic && _attempt?.Attempt is { } attempt
            && CompilerSourceNavigationRequest.TryCreate(_compile, report, diagnostic) is { } canonical
                ? new DiagnosticCompilerRequest(this, row, binding.Index, attempt, canonical, token) : null);
    public Task<DiagnosticActionResult> OpenPageAsync(DiagnosticRow row, Func<DiagnosticPageRequest, Task> callback, CancellationToken ct = default)
        => RunAsync(row, callback, ct, (binding, token) => row.HasPageAction && _extent is { } report && binding.Section >= 0 && row.PageIndex is { } page
            ? new DiagnosticPageRequest(this, row, report, binding.Section, page, _compile.Preview, _compile.Preview.State.Generation, token) : null);

    private async Task<DiagnosticActionResult> RunAsync<T>(DiagnosticRow row, Func<T, Task> callback, CancellationToken ct,
        Func<Binding, CancellationToken, T?> create) where T : DiagnosticNavigationRequest
    {
        DiagnosticActionResult Result(DiagnosticActionCode code) => new(code, code switch
        { DiagnosticActionCode.Presented => "Navigation callback finished. No source was edited.", DiagnosticActionCode.Busy => "The previous navigation callback has not settled.",
            DiagnosticActionCode.Cancelled => "Navigation cancelled; already requested page/selection work may finish.",
            DiagnosticActionCode.Stale => "This diagnostic/report is no longer actionable; use a current canonical report.",
            _ => "This diagnostic navigation is unavailable." });
        if (_disposed) return Result(DiagnosticActionCode.Disposed);
        if (Busy) return Result(DiagnosticActionCode.Busy);
        if (row is null || callback is null || !OwnedVisible(row) || !row.IsCurrent) return Result(DiagnosticActionCode.Stale);
        if (ct.IsCancellationRequested) return Result(DiagnosticActionCode.Cancelled);
        if (!_bindings.TryGetValue(row, out var binding)) return Result(DiagnosticActionCode.Unavailable);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var request = create(binding, cts.Token);
        if (request is null || request is DiagnosticSourceRequest && !row.HasSourceAction
            || request is DiagnosticCompilerRequest && !row.HasSourceAction) { cts.Dispose(); return Result(DiagnosticActionCode.Unavailable); }
        var op = new Operation(_epoch, cts, request); _active = op; Notify();
        try
        {
            if (!IsCurrent(request)) return Result(op.Revoked ?? DiagnosticActionCode.Stale);
            if (request is DiagnosticSourceRequest source && !await VerifySourceAsync(source)) return Result(op.Revoked ?? DiagnosticActionCode.Stale);
            if (!IsCurrent(request)) return Result(op.Revoked ?? DiagnosticActionCode.Cancelled);
            await callback(request); // actual callback: no cancellation-proxy await
            if (!IsCurrent(request)) return Result(op.Revoked ?? DiagnosticActionCode.Stale);
            if (request is DiagnosticSourceRequest again && !await VerifySourceAsync(again)) return Result(op.Revoked ?? DiagnosticActionCode.Stale);
            return Result(IsCurrent(request) ? DiagnosticActionCode.Presented : op.Revoked ?? DiagnosticActionCode.Stale);
        }
        catch (OperationCanceledException) { return Result(op.Revoked ?? DiagnosticActionCode.Cancelled); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Observe the actual fault, but do not let a late foreign failure
            // overwrite an already recorded cancellation/staleness/disposal.
            return Result(op.Revoked ?? (cts.IsCancellationRequested ? DiagnosticActionCode.Cancelled : DiagnosticActionCode.Unavailable));
        }
        finally
        {
            Task refresh; lock (_refreshGate) refresh = _refreshTask;
            try { await refresh; } catch (Exception error) when (error is not OutOfMemoryException) { }
            if (ReferenceEquals(_active, op)) _active = null; cts.Dispose(); if (!_disposed) Notify();
        }
    }
    private async Task<bool> VerifySourceAsync(DiagnosticSourceRequest request)
    {
        if (!IsCurrent(request)) return false;
        var verified = await _source.VerifyCurrentAsync(request.Report, CancellationToken.None);
        if (!verified.Ok && ReferenceEquals(_sourceReport, request.Report)) { _sourceRevoked = true; Refresh(); }
        return verified.Ok && IsCurrent(request);
    }
    internal async Task<NavigationResult> PrepareSourceAsync(DiagnosticSourceRequest request)
    {
        if (!IsCurrent(request)) return new NavigationResult.Rejected("Diagnostic request is stale.");
        var result = await _source.NavigateAsync(request.Report, request.Diagnostic, CancellationToken.None);
        return IsCurrent(request) ? result : new NavigationResult.Rejected("Diagnostic request changed during navigation.");
    }
    internal async Task<CompilerSourceNavigationPolicy.Selection?> PrepareCompilerAsync(DiagnosticCompilerRequest request)
    {
        if (!IsCurrent(request)) return null;
        var selected = await _compiler.PrepareAsync(request.Canonical, request.Token);
        return IsCurrent(request) ? selected : null;
    }
    internal async Task<bool> GoToPageAsync(DiagnosticPageRequest request)
    {
        if (!IsCurrent(request) || request.Posted) return false;
        request.Posted = true; request.ExpectedPreviewGeneration++;
        if (!request.Preview.GoToPage(request.PageIndex + 1)) return false;
        // Even cancellation triggered by the synchronous preview notification
        // must await the actual posted pump before this request retires.
        var actual = request.Preview.WhenSettled;
        await actual;
        return IsCurrent(request);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; Revoke(DiagnosticActionCode.Disposed);
        _workspace.WorkspaceChanged -= QueueRefresh; _workspace.RevisionCommitted -= Saved; _analysis.Changed -= AnalysisChanged; _compile.Changed -= QueueRefresh;
        if (_rendered is not null) _rendered.Changed -= QueueRefresh;
        _compiler.Dispose(); _rows = []; _bindings.Clear(); _summaries = []; _stamp = null;
        _sourceReport = null; _attempt = null; _extent = null; _extentAuthority = null; _extentPreview = null; _rendered = null;
        _notice = "Diagnostic view disposed; borrowed reports/controllers were not disposed or cancelled.";
        Changed = null;
    }
    private sealed record Operation(long Epoch, CancellationTokenSource Stop, DiagnosticNavigationRequest Request)
    { internal DiagnosticActionCode? Revoked; }
    private sealed record Binding(AnalysisReport? Source = null, int Index = -1, AnalysisEngineDiagnosticDto? SourceDiagnostic = null,
        CompilerLogReport? Compiler = null, CompilerLogDiagnostic? CompilerDiagnostic = null, int Section = -1, LiveSource? Live = null);
    private sealed class LiveSource(WorkspaceCoordinator workspace, WorkingFile file, CapturedFile captured)
    {
        private readonly WorkingFile _frozen = file.Clone();
        internal bool Current => workspace.Files.Any(f => ReferenceEquals(f, file)) && !file.Deleted && file.Id == _frozen.Id
            && file.RelativePath == _frozen.RelativePath && file.MediaKind == "text" && file.HasBom == _frozen.HasBom
            && ReferenceEquals(file.Content, _frozen.Content) && file.IsNew == _frozen.IsNew && file.DirtyContent == _frozen.DirtyContent
            && file.DurableHash == _frozen.DurableHash && file.DurableLength == _frozen.DurableLength
            && file.TreeChanged == _frozen.TreeChanged && file.DirtyBinary == _frozen.DirtyBinary && ReferenceEquals(file.PendingTextBytes, _frozen.PendingTextBytes)
            && ReferenceEquals(file.ImportedBytes, _frozen.ImportedBytes)
            && (file.PendingTextBytes is not { } pending || pending.Span.SequenceEqual(captured.Bytes.AsSpan()))
            && (!file.IsNew || file.DirtyContent || file.ImportedBytes is not { } imported || imported.AsSpan().SequenceEqual(captured.Bytes.AsSpan()));
    }
}
