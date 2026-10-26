using Client.Workspace.Domain;

namespace Client.Workspace.Starters;

/// <summary>Explicit, synchronous starter for an untouched empty workspace.
/// Borrows coordinator/locks; never saves, configures tools or compiles. Called
/// on the workspace UI context, with no await between revalidation and CreateFile.</summary>
public sealed class BlankArticleStarter(WorkspaceCoordinator workspace, IWorkspaceLocks locks) : IDisposable
{
    public const string Source = "\\documentclass[10pt]{article}\n"
        + "\\begin{document}\n"
        + "\\noindent Write your document here.\n"
        + "\\end{document}\n";
    private readonly object _authority = new();
    private bool _disposed;

    /// <summary>Capture at render time; the button must retain THIS request,
    /// not create a fresh stamp when an old rendered callback finally runs.</summary>
    public Request? Capture()
    {
        if (!Eligible()) return null;
        return new(_authority, workspace.Project!.Id, workspace.SessionId,
            workspace.WorkingGeneration, workspace.CurrentRevision?.Id, workspace.LockId!);
    }

    private bool Eligible()
    {
        if (_disposed || !workspace.IsOpen || workspace.Project is not { HeadRevisionId: null }
            || workspace.CurrentRevision is not null || workspace.SessionId == Guid.Empty
            || workspace.WorkingGeneration != 0 || workspace.Files.Count != 0
            || workspace.ActiveFileId is not null || workspace.IsDirty
            || workspace.IsReadOnly || workspace.Recovery is not null
            || workspace.LockId is not { Length: > 0 } lockId) return false;
        // Same synchronous local-writer guard used at existing workspace edit
        // boundaries. No browser read/import, acquisition, unlock or permission
        // override; unsupported/disposed lock implementations fail closed.
        try { return locks.IsOwnedLocally(lockId, "oop-project:" + workspace.Project.Id); }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    public bool TryStart(Request? request)
    {
        if (request is null || !ReferenceEquals(request.Authority, _authority) || !Eligible()
            || workspace.Project?.Id != request.ProjectId || workspace.SessionId != request.SessionId
            || workspace.WorkingGeneration != request.Generation || workspace.CurrentRevision?.Id != request.Head
            || workspace.LockId != request.LockId) return false;
        // Fixed trusted path/source; no arbitrary callback arguments, async IO,
        // manual persistence or rollback. Coordinator keeps its own path/edit
        // guards and selects the new working file exactly once on actual success.
        return workspace.CreateFile("main.tex", Source);
    }

    public void Dispose() => _disposed = true;

    public sealed class Request
    {
        internal Request(object authority, ProjectId project, Guid session, long generation, RevisionId? head, string lockId)
        { Authority = authority; ProjectId = project; SessionId = session; Generation = generation; Head = head; LockId = lockId; }
        internal object Authority { get; }
        public ProjectId ProjectId { get; }
        public Guid SessionId { get; }
        public long Generation { get; }
        public RevisionId? Head { get; }
        internal string LockId { get; }
    }
}
