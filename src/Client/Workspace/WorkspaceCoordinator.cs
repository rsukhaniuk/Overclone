using System.Collections.Immutable;
using System.Text;
using Client.Workspace.Domain;

namespace Client.Workspace;

/// <summary>
/// Owns the current immutable revision, the per-file dirty working set and the
/// serialized save queue. C# owns all workflow and persistence policy; this
/// class never touches browser storage directly. Saves are serialized so that
/// overlapping/rapid edits preserve the latest content and a valid parent chain;
/// a failed save never reports saved and never advances the durable head.
/// </summary>
public sealed class WorkspaceCoordinator : IAsyncDisposable
{
    private readonly IProjectRepository _repo;
    private readonly IWorkspaceLocks _locks;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public Project? Project { get; private set; }
    public ProjectRevision? CurrentRevision { get; private set; }
    public List<WorkingFile> Files { get; } = new();
    public FileId? ActiveFileId { get; private set; }
    public string? LockId { get; private set; }
    public bool IsReadOnly { get; private set; }
    public bool IsOpen => Project is not null;
    public WorkspaceCapabilities? Capabilities { get; private set; }

    public SavePhase SavePhase { get; private set; } = SavePhase.Idle;
    public string? LastError { get; private set; }
    public RecoveryReport? Recovery { get; private set; }

    public bool IsDirty => Files.Any(f => f.Deleted || f.IsNew || f.DirtyContent || f.TreeChanged);

    public WorkingFile? ActiveFile => ActiveFileId is null ? null : Files.FirstOrDefault(f => f.Id == ActiveFileId && !f.Deleted);

    public WorkspaceCoordinator(IProjectRepository repo, IWorkspaceLocks locks)
    {
        _repo = repo;
        _locks = locks;
    }

    // ---- capability + project list ----

    public async Task LoadCapabilitiesAsync(CancellationToken ct)
        => Capabilities = await _locks.GetCapabilitiesAsync(ct);

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct)
        => await _repo.ListProjectsAsync(ct);

    public async Task<Project> CreateProjectAsync(string name, CancellationToken ct)
        => await _repo.CreateProjectAsync(name, ct);

    public async Task DeleteProjectAsync(ProjectId id, CancellationToken ct)
        => await _repo.DeleteProjectAsync(id, ct);

    // ---- open / lock ----

    public async Task OpenAsync(ProjectId id, CancellationToken ct)
    {
        await ReleaseLockAsync(ct);
        ResetSession();
        Capabilities ??= await _locks.GetCapabilitiesAsync(ct);

        await LoadSnapshotAsync(id, ct);

        // Single-writer access. Missing Web Locks is a capability error for
        // writable projects, never permission to race.
        if (!Capabilities.WebLocks)
        {
            IsReadOnly = true;
            LastError = "This browser cannot hold a writer lock; the project is read-only.";
            return;
        }
        var lockId = await _locks.TryAcquireAsync(LockName(id), ct);
        if (lockId is null)
        {
            IsReadOnly = true;
            LastError = "Another tab is editing this project. Opened read-only.";
        }
        else
        {
            LockId = lockId;
            IsReadOnly = false;
        }
    }

    /// <summary>Explicit takeover: wait for the current writer to release, then
    /// reload the latest durable head so the new writer does not edit a stale
    /// snapshot or collide on save.</summary>
    public async Task<bool> TakeOverAsync(CancellationToken ct)
    {
        if (Project is null || Capabilities?.WebLocks != true) return false;
        if (LockId is null)
        {
            LockId = await _locks.WaitForAsync(LockName(Project.Id), ct);
        }
        IsReadOnly = false;
        LastError = null;
        // Reload the latest head/contents under the now-held lock, discarding the
        // stale read-only snapshot (a read-only tab cannot have dirty edits).
        await LoadSnapshotAsync(Project.Id, ct);
        return true;
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        await ReleaseLockAsync(ct);
        ResetSession();
    }

    private async Task ReleaseLockAsync(CancellationToken ct)
    {
        if (LockId is not null)
        {
            await _locks.ReleaseAsync(LockId, ct);
            LockId = null;
        }
    }

    private void ResetSession()
    {
        Project = null;
        CurrentRevision = null;
        Files.Clear();
        ActiveFileId = null;
        IsReadOnly = false;
        LastError = null;
        Recovery = null;
        SavePhase = SavePhase.Idle;
    }

    /// <summary>Read the durable snapshot and populate working state. Does not
    /// touch the writer lock, so it can be used for both open and takeover
    /// (takeover already holds the lock).</summary>
    private async Task LoadSnapshotAsync(ProjectId id, CancellationToken ct)
    {
        var snapshot = await _repo.OpenAsync(id, ct);
        Project = snapshot.Project;
        CurrentRevision = snapshot.Revision;
        Recovery = snapshot.Recovery;

        Files.Clear();
        if (CurrentRevision is not null)
        {
            foreach (var f in CurrentRevision.Files)
            {
                var content = snapshot.Content.TryGetValue(f.Id, out var text) ? text : "";
                // Positional args: the constructor parameter names are camelCase.
                Files.Add(new WorkingFile(
                    f.Id, f.RelativePath, f.MediaKind, content,
                    false, false, false, false,
                    f.ContentHash, f.ByteLength));
            }
        }
        ActiveFileId = Files.FirstOrDefault(f => !f.Deleted)?.Id;
    }

    private static string LockName(ProjectId id) => "oop-project:" + id.ToString();

    // ---- file commands (explicit workspace commands; dirty until save) ----

    public bool CreateFile(string path, string initialContent = "")
    {
        if (!CanEdit()) return false;
        var normalized = NormalizePath(path);
        if (normalized is null) { LastError = "Invalid file path."; return false; }
        if (PathInUse(normalized)) { LastError = $"A file named {normalized} already exists."; return false; }
        var file = new WorkingFile(
            FileId.New(), normalized, "text", initialContent,
            initialContent.Length > 0, true, true, false,
            null, 0);
        Files.Add(file);
        ActiveFileId = file.Id;
        MarkEdited();
        return true;
    }

    public bool RenameFile(FileId id, string newPath)
    {
        if (!CanEdit()) return false;
        var normalized = NormalizePath(newPath);
        if (normalized is null) { LastError = "Invalid file path."; return false; }
        var file = Files.FirstOrDefault(f => f.Id == id && !f.Deleted);
        if (file is null) { LastError = "No such file."; return false; }
        if (Files.Any(f => f != file && !f.Deleted && f.RelativePath == normalized))
        { LastError = $"A file named {normalized} already exists."; return false; }
        if (file.RelativePath == normalized) return true;
        file.RelativePath = normalized;
        file.TreeChanged = true;
        MarkEdited();
        return true;
    }

    public bool DeleteFile(FileId id)
    {
        if (!CanEdit()) return false;
        var file = Files.FirstOrDefault(f => f.Id == id && !f.Deleted);
        if (file is null) { LastError = "No such file."; return false; }
        file.Deleted = true;
        file.TreeChanged = true;
        if (ActiveFileId == id) ActiveFileId = Files.FirstOrDefault(f => f != file && !f.Deleted)?.Id;
        MarkEdited();
        return true;
    }

    public bool SelectFile(FileId id)
    {
        if (Files.All(f => f.Id != id || f.Deleted)) return false;
        ActiveFileId = id;
        return true;
    }

    public bool EditText(FileId id, string text)
    {
        if (!CanEdit()) return false;
        var file = Files.FirstOrDefault(f => f.Id == id && !f.Deleted);
        if (file is null) return false;
        file.Content = text;
        file.DirtyContent = true;
        MarkEdited();
        return true;
    }

    private bool CanEdit() => IsOpen && !IsReadOnly && Recovery is null;

    private void MarkEdited()
    {
        if (SavePhase != SavePhase.Saving) SavePhase = SavePhase.Dirty;
    }

    // ---- save (serialized) ----

    public async Task<CommitResult> SaveAsync(CancellationToken ct)
    {
        if (!CanEdit()) return CommitResult.Unavailable("Project is read-only.");
        if (!IsDirty) { SavePhase = SavePhase.Saved; return CommitResult.Saved(CurrentRevision?.Id ?? RevisionId.New()); }

        await _saveGate.WaitAsync(ct);
        try
        {
            SavePhase = SavePhase.Saving;
            // Snapshot the working state at the moment the save actually runs,
            // so edits made while a previous save was in flight are picked up by
            // the next save rather than lost.
            var snapshot = Files.Select(f => f.Clone()).ToList();
            var snapshotById = snapshot.ToDictionary(f => f.Id);

            var contentInputs = new List<ContentInput>();
            var manifestFiles = new List<ProjectFile>();
            foreach (var f in snapshot)
            {
                if (f.Deleted) continue; // deleted-at-snapshot files are excluded from this revision
                if (f.IsNew || f.DirtyContent)
                {
                    var bytes = Encoding.UTF8.GetBytes(f.Content);
                    var hash = SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes));
                    contentInputs.Add(new ContentInput(hash, bytes, bytes.Length));
                    manifestFiles.Add(new ProjectFile(f.Id, f.RelativePath, f.MediaKind, hash, bytes.Length));
                }
                else
                {
                    // Content unchanged: reuse the durable content object (same
                    // hash/length), even if the path changed.
                    manifestFiles.Add(new ProjectFile(
                        f.Id, f.RelativePath, f.MediaKind,
                        f.DurableHash!.Value, f.DurableLength));
                }
            }

            var parent = CurrentRevision?.Id;
            var newRevision = new ProjectRevision(
                RevisionId.New(), Project!.Id, parent,
                manifestFiles.ToImmutableArray(), DateTimeOffset.UtcNow);
            var commit = new ProjectCommit(
                newRevision,
                ImmutableArray<ProjectRevision>.Empty,
                contentInputs.ToImmutableArray());

            var expectedHead = Project.HeadRevisionId;
            var result = await _repo.CommitAsync(commit, expectedHead, ct);

            if (result.Status == CommitStatus.Saved)
            {
                Project = Project with { HeadRevisionId = newRevision.Id };
                CurrentRevision = newRevision;
                var committedByFile = manifestFiles.ToDictionary(f => f.Id);
                ReconcileAfterSave(snapshotById, committedByFile);
                SavePhase = SavePhase.Saved;
                LastError = null;
            }
            else
            {
                SavePhase = SavePhase.Failed;
                LastError = result.SafeMessage ?? "Save failed.";
            }
            return result;
        }
        catch (Exception ex)
        {
            SavePhase = SavePhase.Failed;
            LastError = ex.Message;
            return CommitResult.Failure("StorageFailure", ex.Message);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    // After a successful commit, clear dirty flags only for files whose current
    // content/path still match what was committed; edits and deletions made
    // during the save remain dirty for the next save. A file is only removed
    // from the working list if its deletion was part of the committed snapshot
    // (so a deletion made AFTER the snapshot is retained as a pending dirty
    // deletion, not silently applied).
    private void ReconcileAfterSave(
        IReadOnlyDictionary<FileId, WorkingFile> snapshotById,
        IReadOnlyDictionary<FileId, ProjectFile> committed)
    {
        for (int i = Files.Count - 1; i >= 0; i--)
        {
            var f = Files[i];
            if (!snapshotById.TryGetValue(f.Id, out var s))
            {
                // Created after the snapshot: not part of this commit; keep as-is (new dirty).
                continue;
            }
            if (s.Deleted)
            {
                // Deleted at snapshot time → the deletion is now durable. Remove it.
                Files.RemoveAt(i);
                continue;
            }
            // File was committed (present in the new revision). Reconcile its flags.
            var contentStable = f.Content == s.Content;
            var pathStable = f.RelativePath == s.RelativePath;
            f.IsNew = false;
            f.TreeChanged = !pathStable;
            f.DirtyContent = !contentStable;
            if (committed.TryGetValue(f.Id, out var c))
            {
                f.DurableHash = c.ContentHash;
                f.DurableLength = c.ByteLength;
            }
            // A deletion made AFTER the snapshot leaves f.Deleted == true and
            // TreeChanged == true, so IsDirty stays true and the next save
            // commits the deletion. It is NOT removed here.
        }
    }

    // ---- recovery ----

    public async Task<CommitResult> RecoverToAsync(RevisionId targetRevision, CancellationToken ct)
    {
        if (Project is null || Recovery is null) return CommitResult.Unavailable("No recovery in progress.");
        var damagedHead = Recovery.DamagedHead;
        var result = await _repo.RepairHeadAsync(Project.Id, damagedHead, targetRevision, ct);
        if (result.Status == CommitStatus.Saved)
        {
            // Reload from the now-verified head, keeping the writer lock.
            await LoadSnapshotAsync(Project.Id, ct);
        }
        else
        {
            LastError = result.SafeMessage ?? "Recovery failed.";
        }
        return result;
    }

    // ---- path normalization ----

    internal static string? NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('\\', '/');
        p = p.Trim('/');
        if (p.Length == 0) return null;
        var segments = p.Split('/');
        foreach (var seg in segments)
        {
            if (seg == ".." || seg == ".") return null;
            if (seg.Length == 0) return null;
        }
        return p;
    }

    private bool PathInUse(string normalized)
        => Files.Any(f => !f.Deleted && f.RelativePath == normalized);

    public async ValueTask DisposeAsync()
    {
        await ReleaseLockAsync(CancellationToken.None);
        _saveGate.Dispose();
    }
}

public enum SavePhase { Idle, Dirty, Saving, Saved, Failed }

/// <summary>Mutable working copy of a file's state since the last durable
/// revision. Content is a decoded UTF-8 string; the durable hash/length let the
/// save path reuse an unchanged content object without re-writing it.</summary>
public sealed class WorkingFile
{
    public FileId Id { get; }
    public string RelativePath { get; set; }
    public string MediaKind { get; set; }
    public string Content { get; set; }
    public bool DirtyContent { get; set; }
    public bool TreeChanged { get; set; }
    public bool IsNew { get; set; }
    public bool Deleted { get; set; }
    public SHA256? DurableHash { get; set; }
    public long DurableLength { get; set; }

    public WorkingFile(FileId id, string relativePath, string mediaKind, string content,
        bool dirtyContent, bool treeChanged, bool isNew, bool deleted,
        SHA256? durableHash, long durableLength)
    {
        Id = id;
        RelativePath = relativePath;
        MediaKind = mediaKind;
        Content = content;
        DirtyContent = dirtyContent;
        TreeChanged = treeChanged;
        IsNew = isNew;
        Deleted = deleted;
        DurableHash = durableHash;
        DurableLength = durableLength;
    }

    public WorkingFile Clone() => new(Id, RelativePath, MediaKind, Content,
        DirtyContent, TreeChanged, IsNew, Deleted, DurableHash, DurableLength);
}
