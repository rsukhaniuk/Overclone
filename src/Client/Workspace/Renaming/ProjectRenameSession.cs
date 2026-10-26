using Client.Workspace.Domain;

namespace Client.Workspace.Renaming;

public enum ProjectRenameState { Idle, Ready, Saving, Applied, Cancelled, Stale, Unavailable, Failed }

/// <summary>Page-owned, closed-dashboard proposal. Commands/orchestration stay
/// UI-affine; only notifications dispatch. Never opens/saves/selects a project.</summary>
public sealed class ProjectRenameSession : IDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly Func<Action, Task> _dispatch;
    private bool _disposed;
    private Proposal? _proposal, _settled;
    private CancellationTokenSource? _active;
    private ProjectRenameState _state;
    private bool _unchanged, _releaseFailed, _refreshFailed;
    public event Action? Changed;
    public Proposal? CurrentProposal => _proposal;
    public bool Busy => _active is not null || _workspace.ProjectRenameBusy;
    public ProjectRenameState State => _state;
    public string Notice => _releaseFailed ? "Rename settled but writer lock cleanup failed. Reload before editing or renaming again." :
        _refreshFailed ? "Rename settled but the project list could not be refreshed. Reload to see current names; the settled result is not rolled back." : _state switch
    {
        ProjectRenameState.Ready => "Edit the display name, then Confirm or Cancel. Nothing has changed.",
        ProjectRenameState.Saving => "Renaming the captured local project. Waiting for actual storage and lock cleanup…",
        ProjectRenameState.Applied => _unchanged ? "Project name unchanged." : "Local project renamed. Files and revision history are unchanged.",
        ProjectRenameState.Cancelled => Busy ? "Cancellation requested; actual local work is still settling." : "Project rename cancelled.",
        ProjectRenameState.Stale => "Rename proposal is stale. Choose Rename again for the intended project.",
        ProjectRenameState.Unavailable => "Rename requires the closed local dashboard and an available project writer lock.",
        ProjectRenameState.Failed => "Rename failed or the name is invalid. Use a nonempty name of at most 256 Unicode scalar values without controls or line breaks.",
        _ => "Rename changes a local project's display name only."
    };
    public ProjectRenameSession(WorkspaceCoordinator workspace, Func<Action, Task> dispatch)
    {
        _workspace = workspace; _dispatch = dispatch;
        workspace.WorkspaceChanged += SourceChanged; workspace.ProjectRenameChanged += AdmissionChanged;
    }
    private bool Closed => !_disposed && !_workspace.IsOpen && !_workspace.IsReadOnly && _workspace.Recovery is null;
    public bool ScopeCurrent(Proposal proposal) => Closed && proposal.Session == _workspace.SessionId;
    public bool Show(Project project)
    {
        if (Busy) return false;
        if (!Closed) { _state = ProjectRenameState.Unavailable; Notify(); return false; }
        _releaseFailed = _refreshFailed = false; _settled = null;
        _proposal = new(ProjectRenameSnapshot.Capture(project), _workspace.SessionId);
        _state = ProjectRenameState.Ready; Notify(); return true;
    }
    /// <summary>All competing dashboard actions use this BEFORE any await. They
    /// may drop an UNUSED proposal, never overlap an actual rename.</summary>
    public bool TryOtherAction()
    {
        if (_disposed || Busy) return false;
        if (_proposal is not null) { _proposal = null; _state = ProjectRenameState.Stale; Notify(); }
        return true;
    }
    public void Cancel(Proposal proposal)
    {
        if (_disposed || !ReferenceEquals(_proposal, proposal)) return;
        if (_active is { } active)
        {
            try { active.Cancel(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        }
        else _proposal = null;
        _state = ProjectRenameState.Cancelled; Notify();
    }
    public async Task<ProjectRenameResult> ConfirmAsync(Proposal proposal, string name)
    {
        if (_disposed || Busy || !ReferenceEquals(_proposal, proposal) || proposal.Consumed || !ScopeCurrent(proposal))
            return new(ProjectRenameCode.Stale);
        if (!ProjectRenameName.TryNormalize(name, out _))
        { _state = ProjectRenameState.Failed; Notify(); return new(ProjectRenameCode.InvalidName); }
        proposal.Consumed = true;
        var stop = _active = new CancellationTokenSource(); _state = ProjectRenameState.Saving; Notify();
        try
        {
            var result = await _workspace.RenameProjectAsync(proposal.Expected, name, proposal.Session, stop.Token);
            if (!_disposed && ReferenceEquals(_proposal, proposal) && ScopeCurrent(proposal))
            {
                _unchanged = result.Code == ProjectRenameCode.Unchanged; _releaseFailed = result.WriterReleaseFailed;
                _state = result.Code switch
                {
                    ProjectRenameCode.Applied or ProjectRenameCode.Unchanged => ProjectRenameState.Applied,
                    ProjectRenameCode.Cancelled => ProjectRenameState.Cancelled,
                    ProjectRenameCode.Stale or ProjectRenameCode.Missing => ProjectRenameState.Stale,
                    ProjectRenameCode.Unsupported or ProjectRenameCode.Unavailable or ProjectRenameCode.WriterUnavailable => ProjectRenameState.Unavailable,
                    _ => ProjectRenameState.Failed
                };
                _proposal = null; _settled = proposal;
            }
            return result; // Durable success is never relabeled rollback by late Cancel/disposal.
        }
        finally
        {
            if (ReferenceEquals(_active, stop)) _active = null;
            stop.Dispose(); Notify();
        }
    }
    public void CatalogRefreshFailed(Proposal proposal)
    {
        if (_disposed || Busy || !ReferenceEquals(_settled, proposal) || !ScopeCurrent(proposal)) return;
        _refreshFailed = true; Notify(); // Preserve the actual settled result/state.
    }
    private void SourceChanged()
    {
        if (_disposed || _proposal is not { } p || ScopeCurrent(p)) return;
        if (_active is not null) Cancel(p);
        _proposal = null; _state = ProjectRenameState.Stale; Notify();
    }
    private void AdmissionChanged()
    {
        if (_disposed) return;
        try
        {
            var task = _dispatch(() => { if (!_disposed) Notify(); });
            _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (ObjectDisposedException) when (_disposed) { }
    }
    private void Notify()
    { if (!_disposed) { try { Changed?.Invoke(); } catch (Exception e) when (e is not OutOfMemoryException) { } } }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _workspace.WorkspaceChanged -= SourceChanged; _workspace.ProjectRenameChanged -= AdmissionChanged;
        _proposal = null;
        try { _active?.Cancel(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        Changed = null; // In-flight operation owns CTS until actual coordinator settlement.
    }
    public sealed class Proposal
    {
        internal Proposal(ProjectRenameSnapshot expected, Guid session) { Expected = expected; Session = session; }
        public ProjectRenameSnapshot Expected { get; }
        internal Guid Session { get; }
        internal bool Consumed;
    }
}
