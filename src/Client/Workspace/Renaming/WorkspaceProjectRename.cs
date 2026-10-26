using Client.Workspace.Renaming;

namespace Client.Workspace;

public sealed partial class WorkspaceCoordinator
{
    private readonly object _dashboardGate = new();
    private int _dashboardActions;
    private bool _projectRenameBusy;
    public bool ProjectRenameBusy { get { lock (_dashboardGate) return _projectRenameBusy; } }
    public event Action? ProjectRenameChanged;
    private void BeginDashboardAction()
    {
        lock (_dashboardGate)
        {
            if (_projectRenameBusy) throw new WorkspaceException("ProjectRenameBusy", "A local project rename is still settling.");
            _dashboardActions++;
        }
    }
    private void EndDashboardAction() { lock (_dashboardGate) _dashboardActions--; }
    private void RenameChanged()
    { try { ProjectRenameChanged?.Invoke(); } catch (Exception e) when (e is not OutOfMemoryException) { /* View cannot strand a writer lease. */ } }

    public async Task<ProjectRenameResult> RenameProjectAsync(ProjectRenameSnapshot expected, string name, Guid dashboardSession, CancellationToken ct)
    {
        if (!ProjectRenameName.TryNormalize(name, out string normalized)) return new(ProjectRenameCode.InvalidName);
        if (ct.IsCancellationRequested) return new(ProjectRenameCode.Cancelled);
        lock (_dashboardGate)
        {
            if (_projectRenameBusy || _dashboardActions != 0 || IsOpen || IsReadOnly || Recovery is not null || dashboardSession != SessionId)
                return new(ProjectRenameCode.Unavailable);
            _projectRenameBusy = true;
        }
        RenameChanged();
        bool gate = false, acquiredOwn = false, releaseFailed = false;
        string? writer = null; string lockName = "oop-project:" + expected.Id;
        ProjectRenameResult result;
        try { result = await Apply(); }
        catch (OperationCanceledException) { result = new(ProjectRenameCode.Cancelled); }
        catch (Exception e) when (e is not OutOfMemoryException) { result = new(ProjectRenameCode.Failed); }
        finally
        {
            try
            {
                if (writer is not null && acquiredOwn && _locks.IsOwnedLocally(writer, lockName))
                {
                    // Only this acquired ID, after actual transaction settlement.
                    try { releaseFailed = !await _locks.ReleaseAsync(writer, CancellationToken.None); }
                    catch (Exception e) when (e is not OutOfMemoryException) { releaseFailed = true; }
                }
            }
            finally
            {
                if (gate) _gate.Release();
                lock (_dashboardGate) _projectRenameBusy = false;
                RenameChanged();
            }
        }
        return result with { WriterReleaseFailed = releaseFailed }; // Never erase durable success on cleanup failure.

        async Task<ProjectRenameResult> Apply()
        {
            // No cancellation proxy: actual gate/acquire/verify/transaction/release
            // settlement is observed. No WaitFor/takeover of another writer.
            await _gate.WaitAsync(CancellationToken.None); gate = true;
            if (ct.IsCancellationRequested) return new(ProjectRenameCode.Cancelled);
            if (IsOpen || IsReadOnly || Recovery is not null || SessionId != dashboardSession) return new(ProjectRenameCode.Stale);
            var capabilities = Capabilities ?? await _locks.GetCapabilitiesAsync(CancellationToken.None);
            if (!capabilities.IndexedDb || !capabilities.WebLocks) return new(ProjectRenameCode.Unsupported);
            if (ct.IsCancellationRequested) return new(ProjectRenameCode.Cancelled);
            writer = await _locks.TryAcquireAsync(lockName, CancellationToken.None);
            if (writer is null) return new(ProjectRenameCode.WriterUnavailable);
            acquiredOwn = _locks.IsOwnedLocally(writer, lockName);
            if (!acquiredOwn || !await _locks.OwnsAsync(writer, lockName, CancellationToken.None)) return new(ProjectRenameCode.WriterUnavailable);
            if (ct.IsCancellationRequested) return new(ProjectRenameCode.Cancelled);
            if (IsOpen || SessionId != dashboardSession) return new(ProjectRenameCode.Stale);
            return await _repo.RenameProjectAsync(expected, normalized, writer, ct);
            // No token check after actual reply: a committed success remains so.
        }
    }
}
