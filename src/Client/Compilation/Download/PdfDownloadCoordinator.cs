using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Pdf;
using Client.Workspace;
using Client.Workspace.Domain;

namespace Client.Compilation.Download;

/// <summary>Explicit displayed-PDF export. Borrow workspace/compile/browser; own only
/// pending preparation and its module lease. Called on the workspace UI context.</summary>
public sealed class PdfDownloadCoordinator : IDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly CompileWorkspaceSession _compile;
    private readonly IPdfDownloadBrowser _browser;
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed, _busy;
    private (ProjectId? Project, Guid Session) _view;
    public PdfDownloadCoordinator(WorkspaceCoordinator workspace, CompileWorkspaceSession compile, IPdfDownloadBrowser browser)
    {
        _workspace = workspace; _compile = compile; _browser = browser;
        _view = View();
        workspace.WorkspaceChanged += SourceChanged;
        compile.Changed += SourceChanged;
    }
    public event Action? Changed;
    public string Notice { get; private set; } = "";
    public bool Busy => _busy;
    public bool CanDownload => !_disposed && !_busy && (OwnedOutput() is not null || OwnedRestoredPdf() is not null);
    private (ProjectId? Project, Guid Session) View() => (_workspace.Project?.Id, _workspace.SessionId);
    private AcceptedCompileOutput? OwnedOutput()
    {
        var output = _compile.DisplayedOutput;
        return !_disposed && !_compile.Disposed && _workspace.IsOpen
            && output is { Status: CompileOutputStatus.Succeeded, OutputHash: not null, OutputLength: > 0 }
            && output.OutputFormat == NativeRuntimePackage.Pdf
            && output.SnapshotIdentity.ProjectId == _workspace.Project?.Id
            && output.SnapshotIdentity.SessionId == _workspace.SessionId ? output : null;
    }
    private PdfDocument? OwnedRestoredPdf() => !_disposed && !_compile.Disposed && _workspace.IsOpen ? _compile.RestoredPdf : null;
    private void SourceChanged()
    {
        if (_disposed) return;
        if (_view != View())
        {
            _view = View();
            Notice = "";
        }
        Notify();
    }
    private void Notify()
    {
        if (_disposed) return;
        try { Changed?.Invoke(); }
        catch (Exception error) when (error is not OutOfMemoryException) { /* A view cannot strand download ownership. */ }
    }
    public async Task DownloadAsync()
    {
        if (!CanDownload) return;
        var output = OwnedOutput();
        var restored = output is null ? OwnedRestoredPdf() : null;
        if (output is null && restored is null) return;
        _busy = true;
        var view = View();
        bool Current() => View() == view && (output is not null ? ReferenceEquals(OwnedOutput(), output)
            : ReferenceEquals(OwnedRestoredPdf(), restored));
        var filename = PdfDownloadFilename.Create(_workspace.Project?.Name);
        Notice = "Preparing PDF download…"; Notify();
        bool committed = false;
        try
        {
            await using var prepared = await _browser.PrepareAsync(_stop.Token);
            // Copy accepted bytes only, with no refetch/recompile/history mutation.
            var bytes = output?.ToPayloadArray() ?? restored?.CopyBytes();
            if (bytes is null || bytes.LongLength != (output?.OutputLength ?? restored?.Length)) return;
            // Cutpoint: no await or queued JS invocation between this identity guard
            // and the synchronous browser commit. Source freshness is NOT required.
            if (!Current()) return;
            prepared.Download(filename, bytes);
            committed = true;
            Notice = "PDF download started.";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (Current()) Notice = committed
                ? "PDF download started."
                : "PDF download could not start. Try again.";
        }
        finally
        {
            _busy = false;
            if (!_disposed && !committed && !Current() && view == View())
                Notice = "PDF changed before download. Try again.";
            Notify();
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.WorkspaceChanged -= SourceChanged; _compile.Changed -= SourceChanged;
        _stop.Cancel(); _stop.Dispose(); Changed = null;
    }
}
