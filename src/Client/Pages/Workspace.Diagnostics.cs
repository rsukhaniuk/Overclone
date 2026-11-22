using Client.Analysis;
using Client.Analysis.RenderedExtent;
using Client.Editing.Diagnostics;
using Client.Workspace.Domain;

namespace Client.Pages;

// Proposed master composition. Existing canonical navigation/DOM helpers remain
// authoritative. The diagnostic callback owns its actual import/render/selection
// await, not only a deferred selection slot.
public partial class Workspace
{
    private DiagnosticListSession? _diagnostics;
    private TaskCompletionSource<bool>? _diagnosticRender;
    private void InitializeDiagnosticList() => _diagnostics = new(Coordinator, AnalysisCoord, Navigator, CompileSession, dispatch: action => InvokeAsync(action));
    private void OnDiagnosticRenderedAvailable(RenderedExtentController? controller)
    { if (!_pageDisposed) _diagnostics?.AttachRendered(controller); }
    private async Task OnDiagnosticPageAsync(DiagnosticPageRequest request)
    {
        if (_pageDisposed || !request.IsCurrent) return;
        await ShowCheckedPdfAsync();
        if (!_pageDisposed && request.IsCurrent) await request.GoToPageAsync();
    }

    private CancellationTokenRegistration LinkDiagnosticNavigation(DiagnosticNavigationRequest request)
    {
        var owned = _navCts!;
        return request.Token.Register(() =>
        {
            // Only our own navigation CTS, never a later feature's intent.
            try { if (ReferenceEquals(_navCts, owned)) owned.Cancel(); } catch (ObjectDisposedException) { }
        });
    }
    private Task<bool> AwaitDiagnosticRender()
    {
        var actual = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _diagnosticRender = actual; StateHasChanged(); return actual.Task;
    }
    private void CompleteDiagnosticRender()
    {
        var actual = _diagnosticRender; _diagnosticRender = null;
        actual?.TrySetResult(!_pageDisposed); // OnAfterRender entry: the DOM render itself completed.
    }
    private void DisposeDiagnosticList()
    {
        _diagnostics?.Dispose();
        var deferred = _diagnosticRender; _diagnosticRender = null;
        deferred?.TrySetResult(false); // Page is gone: deferred render cannot perform our DOM selection.
    }
    private async Task OnDiagnosticSourceAsync(DiagnosticSourceRequest request)
    {
        if (_pageDisposed || !request.IsCurrent) return;
        var ct = NavCancelAndStart(); using var link = LinkDiagnosticNavigation(request);
        if (ct.IsCancellationRequested || !request.IsCurrent) return;
        // Shared import is awaited to actual settlement even if diagnostic
        // authority is cancelled. No cancellation proxy detaches this lease.
        var module = await GetEditorSelectAsync(CancellationToken.None);
        if (_pageDisposed || ct.IsCancellationRequested || !request.IsCurrent || module is null) return;
        var result = await request.PrepareAsync();
        if (_pageDisposed || ct.IsCancellationRequested || !request.IsCurrent) return;
        if (result is NavigationResult.Selected selected)
        {
            await ShowCheckedSourceAsync();
            if (_pageDisposed || ct.IsCancellationRequested || !request.IsCurrent) return;
            if (Coordinator.ActiveFileId != selected.FileId)
            {
                SourceQueryChanged(); if (!JournalSelectFile(selected.FileId)) return;
                await AwaitDiagnosticRender();
            }
            if (_pageDisposed || ct.IsCancellationRequested || !request.IsCurrent) return;
            await ApplySelectionAsync(selected, ct, () => request.IsCurrent);
        }
        else await HandleAnalysisNavigationAsync(result, ct); // canonical NonSelectable/Rejected display only
    }
    private async Task OnDiagnosticCompilerAsync(DiagnosticCompilerRequest request)
    {
        if (_pageDisposed || !request.IsCurrent) return;
        var ct = NavCancelAndStart(); using var link = LinkDiagnosticNavigation(request);
        if (ct.IsCancellationRequested || !request.IsCurrent) return;
        var owned = _navCts;
        _compilerSourceNavigating = true; _compilerSourceRequest = request.Canonical;
        _compilerSourceStatus = "Checking captured compiler source line…";
        try
        {
            var module = await GetEditorSelectAsync(CancellationToken.None);
            if (_pageDisposed || ct.IsCancellationRequested || !request.IsCurrent || module is null) return;
            var selected = await _compilerSource.PrepareAsync(request.Canonical, ct);
            if (!CompilerSourceIntentCurrent(ct) || !request.IsCurrent || selected is null) return;
            await ShowCheckedSourceAsync();
            if (!CompilerSourceIntentCurrent(ct) || !request.IsCurrent) return;
            if (Coordinator.ActiveFileId != request.Canonical.File.FileId)
            {
                if (!_compilerSource.IsCurrent(selected, ct) || !JournalSelectFile(request.Canonical.File.FileId)) return;
                await AwaitDiagnosticRender();
            }
            if (!CompilerSourceIntentCurrent(ct) || !request.IsCurrent) return;
            await ApplyCompilerSourceSelectionAsync(selected, ct, () => request.IsCurrent);
        }
        finally
        {
            if (!_pageDisposed && ReferenceEquals(_navCts, owned) && _compilerSourceNavigating && ReferenceEquals(_compilerSourceRequest, request.Canonical))
            {
                _compilerSourceNavigating = false; _compilerSourceRequest = null;
                _compilerSourceStatus = "Diagnostic source navigation finished or became unavailable. No source was edited.";
                StateHasChanged();
            }
        }
    }
}
