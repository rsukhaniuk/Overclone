using System.Collections.Immutable;
using System.Text;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Transfer;

namespace Client.Workspace;

/// <summary>
/// Owns the current immutable revision, the per-file dirty working set and the
/// serialized save/lifecycle queue. C# owns all workflow and persistence
/// policy; this class never touches browser storage directly. Saves, imports,
/// restores and close are serialized through a single gate so lifecycle
/// operations cannot clear a session underneath an in-flight save.
///
/// M1b additions: byte-preserving text/binary working files, a session identity
/// plus working-generation token (a durable revision ID alone cannot establish
/// preview freshness across unsaved edits), arbitrary-revision reads for
/// history, revision-bound export capture, and explicit project-wide restore
/// that creates a new revision/checkpoint (never rewinds the head).
/// </summary>
public sealed class WorkspaceCoordinator : IAsyncDisposable
{
    private readonly IProjectRepository _repo;
    private readonly IWorkspaceLocks _locks;
    private readonly HistoryService _history;
    // A single gate serializes save, import-publish, restore and close so a
    // lifecycle operation cannot clear the session underneath an in-flight save
    // and vice versa. SaveCore is the lock-free inner routine used by Save and
    // by the close/import/restore flush loops that already hold the gate.
    private readonly SemaphoreSlim _gate = new(1, 1);

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

    /// <summary>Session identity. Changes on open/takeover/close. A callback
    /// bound to one session must not affect a different project/session.</summary>
    public Guid SessionId { get; private set; } = Guid.NewGuid();

    /// <summary>Working-generation token, incremented on every mutation of the
    /// working set (create/rename/delete/edit/import/restore). Captured with a
    /// preview's expected head; any intervening edit — saved or not — invalidates
    /// the preview. A durable revision ID alone cannot establish freshness.</summary>
    public long WorkingGeneration { get; private set; }

    /// <summary>Raised synchronously when the working-set identity changes: an
    /// edit/tree mutation (via <see cref="BumpGeneration"/>), a session reset,
    /// a takeover reset, a completed load (open/takeover/recover), or a completed
    /// restore. This is the single invalidation seam for analysis; it is raised
    /// AFTER the synchronous state change is finished. Subscribers MUST NOT perform
    /// asynchronous reads inside this callback — they should only record that the
    /// workspace changed (e.g. bump a local epoch) and dispatch deferred work.</summary>
    public event Action? WorkspaceChanged;

    private void RaiseWorkspaceChanged() => WorkspaceChanged?.Invoke();

    public bool IsDirty => Files.Any(f => f.Deleted || f.IsNew || f.DirtyContent || f.DirtyBinary || f.TreeChanged);

    public WorkingFile? ActiveFile => ActiveFileId is null ? null : Files.FirstOrDefault(f => f.Id == ActiveFileId && !f.Deleted);

    public WorkspaceCoordinator(
        IProjectRepository repo,
        IWorkspaceLocks locks,
        HistoryService history)
    {
        _repo = repo;
        _locks = locks;
        _history = history;
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
        SessionId = Guid.NewGuid();
        WorkingGeneration = 0;

        await LoadSnapshotAsync(id, ct);

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
        SessionId = Guid.NewGuid();
        WorkingGeneration = 0;
        RaiseWorkspaceChanged();
        await LoadSnapshotAsync(Project.Id, ct);
        return true;
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Never silently discard unsaved edits. Flush saves until the session
            // is clean (edits can land during a flush save and are not part of
            // that commit), or until a save fails — in which case abort the close
            // so the unsaved revision stays available for retry/export.
            while (IsDirty && LockId is not null && Recovery is null)
            {
                var saveResult = await SaveCoreAsync(ct);
                if (saveResult.Status != CommitStatus.Saved)
                    return;
            }
        }
        finally { _gate.Release(); }

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
        SessionId = Guid.NewGuid();
        WorkingGeneration = 0;
        RaiseWorkspaceChanged();
    }

    /// <summary>Read the durable snapshot and populate working state. Does not
    /// touch the writer lock, so it can be used for both open and takeover.</summary>
    private async Task LoadSnapshotAsync(ProjectId id, CancellationToken ct)
    {
        var snapshot = await _repo.OpenAsync(id, ct);
        ApplySnapshot(snapshot);
        // Raise AFTER the snapshot is applied so subscribers see the completed
        // load (covers completed OpenAsync/TakeOverAsync/RecoverToAsync).
        RaiseWorkspaceChanged();
    }

    /// <summary>Synchronously apply a durable snapshot to the working set.
    /// No awaits: callers (restore) re-check the working generation
    /// immediately before this so an edit that landed during the
    /// preceding async reads cannot be overwritten here.</summary>
    private void ApplySnapshot(ProjectSnapshot snapshot)
    {
        Project = snapshot.Project;
        CurrentRevision = snapshot.Revision;
        Recovery = snapshot.Recovery;

        Files.Clear();
        if (CurrentRevision is not null)
        {
            foreach (var f in CurrentRevision.Files)
            {
                string content = "";
                byte[]? binary = null;
                bool hasBom = false;
                if (f.MediaKind == "text")
                {
                    if (snapshot.Content.TryGetValue(f.Id, out var text)) content = text;
                    snapshot.TextHasBom.TryGetValue(f.Id, out hasBom);
                }
                else
                {
                    if (snapshot.BinaryContent.TryGetValue(f.Id, out var bin)) binary = bin;
                }
                Files.Add(new WorkingFile(
                    f.Id, f.RelativePath, f.MediaKind, content, binary, hasBom,
                    dirtyContent: false, dirtyBinary: false, treeChanged: false, isNew: false, deleted: false,
                    durableHash: f.ContentHash, durableLength: f.ByteLength,
                    importedBytes: null));
            }
        }
        ActiveFileId = Files.FirstOrDefault(f => !f.Deleted)?.Id;
        if (ActiveFileId is not null && ActiveFile?.MediaKind == "binary")
        {
            // Prefer selecting the first text file for editing; binary files have no editor.
            var firstText = Files.FirstOrDefault(f => !f.Deleted && f.MediaKind == "text");
            if (firstText is not null) ActiveFileId = firstText.Id;
        }
    }

    /// <summary>Capture the working-file changes made since <paramref name="preAcceptById"/>
    /// (the accept-start snapshot), keyed by stable file ID. Only files that were
    /// concurrently content-edited, renamed, deleted or newly created during the
    /// restore I/O are returned. Call this synchronously, before
    /// <see cref="ApplySnapshot"/>.</summary>
    private Dictionary<FileId, ConcurrentChange> CaptureConcurrentChanges(
        IReadOnlyDictionary<FileId, WorkingFile> preAcceptById)
    {
        var concurrent = new Dictionary<FileId, ConcurrentChange>();
        foreach (var f in Files)
        {
            if (!preAcceptById.TryGetValue(f.Id, out var pre))
            {
                // Created during the I/O (not present at accept time).
                concurrent[f.Id] = ConcurrentChange.ForNew(f);
                continue;
            }
            // Track each field separately, relative to the accept-start
            // snapshot — NOT relative to the restored version. A rename
            // with no content edit must not copy the (unchanged)
            // pre-restore content over the restored content.
            bool contentChanged = f.MediaKind == "binary"
                ? (f.DirtyBinary && !ReferenceEquals(f.BinaryContent, pre.BinaryContent))
                : f.Content != pre.Content;
            bool pathChanged = f.RelativePath != pre.RelativePath;
            bool deletedNow = f.Deleted && !pre.Deleted;
            if (contentChanged || pathChanged || deletedNow)
                concurrent[f.Id] = ConcurrentChange.Of(f, contentChanged, pathChanged, deletedNow);
        }
        return concurrent;
    }

    private void ApplyConcurrentChanges(
        IReadOnlyDictionary<FileId, ConcurrentChange> concurrent,
        FileId? preAcceptActive)
    {
        FileId? activeCandidate = null;
        foreach (var kv in concurrent)
        {
            var c = kv.Value;
            var idx = Files.FindIndex(f => f.Id == c.Id);
            if (idx < 0)
            {
                // A file absent from the restored revision must NOT be silently
                // dropped if the user concurrently edited/renamed it (an unreviewed
                // change): preserve it — re-add the post-I/O file (its edited
                // content/path and accept-time durable state) so the next save commits it.
                // A file DELETED during the I/O stays gone. This covers both files created
                // during the I/O and files that existed at accept but were added after the
                // historical revision (absent from it).
                if (!c.Deleted && c.File is { } keep)
                {
                    Files.Add(keep.Clone());
                    if (keep.MediaKind == "text") activeCandidate = c.Id;
                }
                continue;
            }

            var f = Files[idx];
            if (c.Deleted)
            {
                f.Deleted = true;
                f.TreeChanged = true;
                continue;
            }
            // Apply ONLY the fields that actually changed during the I/O.
            if (c.PathChanged)
            {
                f.RelativePath = c.File.RelativePath;
                f.TreeChanged = true;
            }
            if (c.ContentChanged && f.MediaKind == "text")
            {
                f.Content = c.File.Content;
                f.DirtyContent = true;
                f.HasBom = c.File.HasBom;
                activeCandidate = c.Id;
            }
            else if (c.BinaryChanged && f.MediaKind == "binary" && c.File.BinaryContent is not null)
            {
                f.BinaryContent = c.File.BinaryContent;
                f.DirtyBinary = true;
            }
        }
        if (activeCandidate is null && preAcceptActive is { } aid)
        {
            if (Files.Any(f => f.Id == aid && !f.Deleted && f.MediaKind == "text"))
                activeCandidate = aid;
        }
        if (activeCandidate is { } ac && Files.Any(f => f.Id == ac && !f.Deleted && f.MediaKind == "text"))
            ActiveFileId = ac;
    }

    private static string LockName(ProjectId id) => "oop-project:" + id.ToString();

    // ---- file commands (explicit workspace commands; dirty until save) ----

    public bool CreateFile(string path, string initialContent = "")
    {
        if (!CanEdit()) return false;
        // A new empty text file is UTF-8 without BOM; a CreateFile with explicit
        // initial content is treated as user-typed (LF on save, no BOM).
        var bytes = TextContent.EncodeEdited(initialContent, withBom: false);
        return CreateFileFromBytes(path, bytes, isUserText: true);
    }

    /// <summary>Create a file from exact bytes (single-file import or asset
    /// add). Bytes are classified by the shared validator: valid-UTF-8 editable
    /// extensions are text (decoded for display, exact bytes retained until an
    /// edit), everything else is binary. Routing single-file import through
    /// this model means adding an asset does not require building a ZIP first.</summary>
    public bool CreateFileFromBytes(string path, byte[] bytes, bool isUserText = false)
    {
        if (!CanEdit()) return false;
        var normalized = ProjectPathValidator.Normalize(path);
        if (normalized is null) { LastError = "Invalid file path."; return false; }
        var err = ProjectPathValidator.Validate(normalized);
        if (err is { } ve) { LastError = $"Invalid file path ({ve})."; return false; }
        if (PathInUse(normalized)) { LastError = $"A file named {normalized} already exists."; return false; }
        if (bytes.LongLength > ArchiveLimits.SingleFile)
        { LastError = "File exceeds the single-file limit."; return false; }

        var (mediaKind, hasBom) = ClassifyBytes(bytes, normalized);
        string content = "";
        byte[]? binary = null;
        byte[]? imported = null;
        if (mediaKind == "text")
        {
            content = TextContent.DecodeStrict(StripBom(bytes));
            // Retain exact bytes until the user edits; a user-typed CreateFile has
            // no original bytes to preserve and is encoded LF on save.
            imported = isUserText ? null : bytes;
        }
        else
        {
            binary = bytes;
        }

        var file = new WorkingFile(
            FileId.New(), normalized, mediaKind, content, binary, hasBom,
            dirtyContent: false, dirtyBinary: mediaKind == "binary",
            treeChanged: true, isNew: true, deleted: false,
            durableHash: null, durableLength: 0,
            importedBytes: imported);
        Files.Add(file);
        ActiveFileId = file.Id;
        BumpGeneration();
        SavePhase = SavePhase.Dirty;
        return true;
    }

    /// <summary>Replace a binary asset's bytes (e.g. re-import). Not a text edit.</summary>
    public bool ReplaceFileBytes(FileId id, byte[] bytes)
    {
        if (!CanEdit()) return false;
        var file = Files.FirstOrDefault(f => f.Id == id && !f.Deleted);
        if (file is null) { LastError = "No such file."; return false; }
        if (file.MediaKind != "binary") { LastError = "Only binary assets can be replaced by bytes."; return false; }
        if (bytes.LongLength > ArchiveLimits.SingleFile) { LastError = "File exceeds the single-file limit."; return false; }
        file.BinaryContent = bytes;
        file.DirtyBinary = true;
        BumpGeneration();
        SavePhase = SavePhase.Dirty;
        return true;
    }

    public bool RenameFile(FileId id, string newPath)
    {
        if (!CanEdit()) return false;
        var normalized = ProjectPathValidator.Normalize(newPath);
        if (normalized is null) { LastError = "Invalid file path."; return false; }
        var err = ProjectPathValidator.Validate(normalized);
        if (err is { } ve) { LastError = $"Invalid file path ({ve})."; return false; }
        var file = Files.FirstOrDefault(f => f.Id == id && !f.Deleted);
        if (file is null) { LastError = "No such file."; return false; }
        if (Files.Any(f => f != file && !f.Deleted && ProjectPathValidator.ComparisonKey(f.RelativePath) == ProjectPathValidator.ComparisonKey(normalized)))
        { LastError = $"A file named {normalized} already exists."; return false; }
        if (ProjectPathValidator.ComparisonKey(file.RelativePath) == ProjectPathValidator.ComparisonKey(normalized)) return true;
        file.RelativePath = normalized;
        file.TreeChanged = true;
        BumpGeneration();
        if (SavePhase != SavePhase.Saving) SavePhase = SavePhase.Dirty;
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
        BumpGeneration();
        if (SavePhase != SavePhase.Saving) SavePhase = SavePhase.Dirty;
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
        if (file.MediaKind != "text") { LastError = "Binary files cannot be text-edited."; return false; }
        file.Content = text;
        file.DirtyContent = true;
        // An edit discards the pre-edit exact bytes; save now serializes LF + BOM.
        file.ImportedBytes = null;
        BumpGeneration();
        if (SavePhase != SavePhase.Saving) SavePhase = SavePhase.Dirty;
        return true;
    }

    private bool CanEdit() => IsOpen && !IsReadOnly && Recovery is null;

    private void BumpGeneration()
    {
        WorkingGeneration++;
        RaiseWorkspaceChanged();
    }

    // ---- byte materialization ----

    /// <summary>Exact bytes for a file: unedited text reads durable bytes
    /// (preserving BOM/CRLF); edited text is LF+BOM; binary uses working or
    /// durable bytes. Used for download and export of the working set.</summary>
    public async Task<byte[]> GetFileBytesAsync(WorkingFile file, CancellationToken ct)
    {
        if (file.MediaKind == "binary")
        {
            if (file.DirtyBinary && file.BinaryContent is not null) return file.BinaryContent;
            if (file.DurableHash is { } bh) return await _repo.ReadContentAsync(bh, ct);
            return file.BinaryContent ?? Array.Empty<byte>();
        }
        // text
        if (file.IsNew && !file.DirtyContent && file.ImportedBytes is not null) return file.ImportedBytes;
        if (file.DirtyContent || (file.IsNew && file.ImportedBytes is null))
            return TextContent.EncodeEdited(file.Content, file.HasBom);
        if (file.DurableHash is { } th) return await _repo.ReadContentAsync(th, ct);
        return TextContent.EncodeEdited(file.Content, file.HasBom);
    }

    private static byte[] StripBom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            var r = new byte[bytes.Length - 3];
            Buffer.BlockCopy(bytes, 3, r, 0, r.Length);
            return r;
        }
        return bytes;
    }

    private static (string MediaKind, bool HasBom) ClassifyBytes(byte[] bytes, string normalizedPath)
    {
        if (ProjectPathValidator.MediaKindForPath(normalizedPath) != "text")
            return ("binary", false);
        if (!TextContent.TryDecode(bytes, out _, out var hasBom))
            return ("binary", false);
        return ("text", hasBom);
    }

    // ---- save (serialized) ----

    public async Task<CommitResult> SaveAsync(CancellationToken ct)
    {
        if (!CanEdit()) return CommitResult.Unavailable("Project is read-only.");
        if (!IsDirty) { SavePhase = SavePhase.Saved; return CommitResult.Saved(CurrentRevision?.Id ?? RevisionId.New()); }

        await _gate.WaitAsync(ct);
        try { return await SaveCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<CommitResult> SaveCoreAsync(CancellationToken ct)
    {
        if (!CanEdit()) return CommitResult.Unavailable("Project is read-only.");
        if (!IsDirty) { SavePhase = SavePhase.Saved; return CommitResult.Saved(CurrentRevision?.Id ?? RevisionId.New()); }

        SavePhase = SavePhase.Saving;
        // Snapshot the working state at the moment the save actually runs, so
        // edits made while a previous save was in flight are picked up by the
        // next save rather than lost.
        var snapshot = Files.Select(f => f.Clone()).ToList();
        var snapshotById = snapshot.ToDictionary(f => f.Id);

        var contentInputs = new List<ContentInput>();
        var manifestFiles = new List<ProjectFile>();
        foreach (var f in snapshot)
        {
            if (f.Deleted) continue; // deleted-at-snapshot files are excluded from this revision
            if (f.IsNew || f.DirtyContent || f.DirtyBinary)
            {
                byte[] bytes = await MaterializeWorkingBytesAsync(f, ct);
                var hash = SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes));
                contentInputs.Add(new ContentInput(hash, bytes, bytes.Length));
                manifestFiles.Add(new ProjectFile(f.Id, f.RelativePath, f.MediaKind, hash, bytes.Length, f.HasBom));
            }
            else
            {
                // Content unchanged: reuse the durable content object (same
                // hash/length), even if the path changed.
                manifestFiles.Add(new ProjectFile(
                    f.Id, f.RelativePath, f.MediaKind,
                    f.DurableHash!.Value, f.DurableLength, f.HasBom));
            }
        }

        // Reject an invalid manifest at the commit boundary: a save must never commit
        // duplicate paths (case/NFC-insensitive) or file/directory prefix
        // conflicts, which would make the project unrestorable/exportable.
        var manifestConflicts = ProjectTreeValidator.FindPathConflicts(manifestFiles.Select(f => f.RelativePath));
        if (manifestConflicts is { } mc && mc.Count > 0)
        {
            SavePhase = SavePhase.Failed;
            LastError = "Save rejected: the project would commit conflicting paths: "
                + string.Join(" ", mc) + ". Resolve the duplicate path (rename or delete one) before saving.";
            return CommitResult.Failure("DuplicatePath", LastError);
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

    /// <summary>Materialize the exact bytes that a dirty/new file commits:
    /// edited text → LF + BOM; unedited imported text → original exact bytes;
    /// binary → working bytes.</summary>
    private async Task<byte[]> MaterializeWorkingBytesAsync(WorkingFile f, CancellationToken ct)
    {
        if (f.MediaKind == "binary")
        {
            if (f.BinaryContent is not null) return f.BinaryContent;
            if (f.DurableHash is { } h) return await _repo.ReadContentAsync(h, ct);
            return Array.Empty<byte>();
        }
        // text
        if (f.IsNew && !f.DirtyContent && f.ImportedBytes is not null) return f.ImportedBytes;
        return TextContent.EncodeEdited(f.Content, f.HasBom);
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
                Files.RemoveAt(i);
                continue;
            }
            // File was committed. Reconcile its flags against what was committed.
            // contentStable compares the CURRENT working content to the
            // SNAPSHOT content (not the dirty flag, which is what we are
            // computing here); an edit made during the save makes them differ.
            bool contentStable = f.MediaKind == "binary"
                ? f.BinaryContent == s.BinaryContent
                : f.Content == s.Content;
            var pathStable = f.RelativePath == s.RelativePath;
            f.IsNew = false;
            f.TreeChanged = !pathStable;
            if (f.MediaKind == "binary")
            {
                f.DirtyBinary = !contentStable;
            }
            else
            {
                f.DirtyContent = !contentStable;
                // After the first commit, original exact bytes are no longer relevant.
                f.ImportedBytes = null;
            }
            if (committed.TryGetValue(f.Id, out var c))
            {
                f.DurableHash = c.ContentHash;
                f.DurableLength = c.ByteLength;
                f.HasBom = c.HasBom;
            }
            // A deletion made AFTER the snapshot leaves f.Deleted == true and
            // TreeChanged == true, so IsDirty stays true and the next save
            // commits the deletion. It is NOT removed here.
        }
    }

    // ---- export capture (revision-bound, separate from local save) ----

    /// <summary>Flush current edits until clean, then return an export source
    /// bound to the immutable captured revision. Edits after capture remain in
    /// the workspace and cannot change exported R. The returned ReadContent
    /// reads exact durable bytes by hash. Aborts (throws) if a save fails
    /// during the flush, so a failed save never produces a stale export of
    /// the previous revision.</summary>
    public async Task<ExportSource> CaptureExportSourceAsync(CancellationToken ct)
    {
        if (Project is null) throw new InvalidOperationException("No project is open.");
        if (!await FlushUntilCleanAsync(ct))
            throw new InvalidOperationException(
                "Cannot export: unsaved edits could not be flushed (save failed). Retry the save or export the unsaved text.");
        var revision = CurrentRevision ?? throw new InvalidOperationException("No revision to export.");
        return new ExportSource(revision, (hash, token) => _repo.ReadContentAsync(hash, token));
    }

    /// <summary>Flush saves under the lifecycle gate until the session is
    /// clean or a save fails. Returns true if the session is now clean, false
    /// if a save failed (the unsaved edits are retained for retry/export).
    /// Used before export/restore capture and close. A failed save leaves the
    /// session open — it must not let a capture proceed on the stale head.</summary>
    public async Task<bool> FlushUntilCleanAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            while (IsDirty && LockId is not null && Recovery is null)
            {
                var r = await SaveCoreAsync(ct);
                if (r.Status != CommitStatus.Saved) return false;
            }
            return true;
        }
        finally { _gate.Release(); }
    }

    // ---- import publish (always a NEW project) ----

    /// <summary>Publish a fully validated staged import as a new local project.
    /// Serialized with save/close. The currently open project is NOT replaced;
    /// the caller may open the result after success.</summary>
    public async Task<ImportPublishResult> PublishImportAsync(
        StagedProject staged, string projectName, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Publishing does not modify the current project's head; it creates a
            // new project. Still, serialize against an in-flight save/close so a
            // close cannot clear the session mid-publish.
            return await _repo.PublishImportAsync(
                staged with { Name = string.IsNullOrWhiteSpace(projectName) ? staged.Name : projectName.Trim() },
                ct);
        }
        finally { _gate.Release(); }
    }

    // ---- history + restore ----

    public Task<HistoryPage> ReadHistoryAsync(ProjectId projectId, HistoryCursor? cursor, CancellationToken ct)
        => _history.ReadHistoryAsync(projectId, cursor, ct);

    public Task<ComparisonSummary> CompareRevisionsAsync(
        ProjectRevision baseRevision, ProjectRevision historicalRevision, CancellationToken ct)
        => _history.CompareRevisions(baseRevision, historicalRevision, ct);

    /// <summary>Capture a restore preview: flush current edits, capture the
    /// base head and working generation, load the historical revision and
    /// compute the read-only change preview. The returned token binds the
    /// preview to session + generation + head so any intervening edit, tree
    /// change or session switch invalidates it.</summary>
    public async Task<RestorePreview> CaptureRestorePreviewAsync(
        RevisionId historicalRevisionId, CancellationToken ct)
    {
        if (Project is null) throw new InvalidOperationException("No project is open.");
        if (IsReadOnly || LockId is null) throw new InvalidOperationException("Restore requires the writer lock.");
        if (!await FlushUntilCleanAsync(ct))
            throw new InvalidOperationException(
                "Cannot capture a restore preview: unsaved edits could not be flushed (save failed). Retry the save first.");

        // Capture the freshness token BEFORE the async reads below. An edit
        // made during ReadRevisionAsync/CompareRevisions would bump
        // WorkingGeneration after this point, so the preview (and later
        // acceptance) is correctly rejected as stale rather than silently
        // accepting an edit that is absent from the comparison.
        long generationAtCapture = WorkingGeneration;
        RevisionId? expectedHeadAtCapture = Project.HeadRevisionId;

        var baseRevision = CurrentRevision
            ?? throw new InvalidOperationException("No current revision to restore from.");
        var historical = await _repo.ReadRevisionAsync(Project.Id, historicalRevisionId, ct);
        if (!historical.Verified)
            throw new InvalidOperationException(
                "Historical revision is damaged: " + (historical.FailureReason ?? "unknown"));
        var summary = await _history.CompareRevisions(baseRevision, historical.Revision, ct);

        var token = new PreviewToken(SessionId, generationAtCapture, expectedHeadAtCapture, historical.Revision.Id);
        return new RestorePreview(token, summary, historical.Revision);
    }

    /// <summary>Accept a restore preview under the writer lock and the same
    /// serialized boundary as save. Re-validates the token (session, generation,
    /// expected head), verifies historical objects again, and commits a NEW
    /// revision whose parent is the current head and whose file set/bytes/IDs
    /// match the historical revision. Records a restore checkpoint; old
    /// history/heads remain intact. Never rewinds the head or calls the
    /// corrupt-head repair method.</summary>
    public async Task<CommitResult> AcceptRestoreAsync(RestorePreview preview, CancellationToken ct)
    {
        if (Project is null) return CommitResult.Unavailable("No project is open.");
        if (IsReadOnly || LockId is null) return CommitResult.Unavailable("Restore requires the writer lock.");

        await _gate.WaitAsync(ct);
        try
        {
            // Re-validate freshness: session, working generation AND expected head
            // must all still match. A durable-head comparison alone is insufficient
            // because unsaved edits would change WorkingGeneration without moving head.
            var token = preview.Token;
            if (token.SessionId != SessionId)
                return CommitResult.Failure("StalePreview", "The session has changed; capture a new preview.");
            if (token.WorkingGeneration != WorkingGeneration)
                return CommitResult.Failure("StalePreview",
                    "The working set changed since the preview was captured.");
            if (token.ExpectedHead != Project.HeadRevisionId)
                return CommitResult.Failure("RevisionConflict",
                    "The project head changed; reopen to reconcile.");

            // Snapshot the working set at accept time. Edits made DURING the
            // verification/commit/reload I/O (which does not hold off
            // EditText) are reconciled onto the restored snapshot —
            // untouched files are restored to their historical version, and
            // only concurrently-edited files keep the during-I/O edit. This
            // never overwrites unreviewed changes and never silently
            // undoes the restoration of untouched files.
            long generationAtAccept = WorkingGeneration;
            var preAcceptById = new Dictionary<FileId, WorkingFile>();
            foreach (var w in Files) preAcceptById[w.Id] = w.Clone();
            FileId? preAcceptActive = ActiveFileId;

            var historical = preview.HistoricalRevision;
            // Verify referenced objects again at acceptance (in case they were
            // damaged after capture).
            var rev = await _repo.ReadRevisionAsync(Project.Id, historical.Id, ct);
            if (!rev.Verified)
                return CommitResult.Failure("CorruptStorage",
                    "Historical revision is damaged: " + (rev.FailureReason ?? "unknown"));

            var restoredFiles = ImmutableArray.CreateRange(historical.Files);
            var restored = new ProjectRevision(
                RevisionId.New(), Project.Id, CurrentRevision?.Id, restoredFiles, DateTimeOffset.UtcNow);

            var result = await _repo.CreateRestoreRevisionAsync(
                Project.Id,
                token.ExpectedHead!.Value,
                restored,
                historical.Id,
                "user-restore",
                ct);

            if (result.Status == CommitStatus.Saved)
            {
                Project = Project with { HeadRevisionId = restored.Id };
                CurrentRevision = restored;

                // Read the restored snapshot (async). Edits made during this
                // await land in Files and are reconciled below.
                var snap = await _repo.OpenAsync(Project.Id, ct);

                // Synchronous reconcile: no await between the concurrent
                // capture, the restored-snapshot apply and the superposition,
                // so no edit can interleave and overwrite the reconciliation.
                var concurrent = CaptureConcurrentChanges(preAcceptById);
                ApplySnapshot(snap);
                ApplyConcurrentChanges(concurrent, preAcceptActive);

                // Validate the COMPLETE merged working tree before declaring success:
                // a concurrent rename/create that re-introduces a path colliding
                // with a restored file (or a prefix conflict) must never silently overwrite,
                // drop or rename a file. Surface the conflict and leave the session in a
                // recoverable state (save re-validates at the commit boundary).
                var conflicts = ProjectTreeValidator.FindPathConflicts(
                    Files.Where(f => !f.Deleted).Select(f => f.RelativePath));
                if (conflicts is { } c && c.Count > 0)
                {
                    SavePhase = SavePhase.Failed;
                    LastError =
                        "Restore committed, but the working tree has path conflicts that would make save/export invalid: "
                        + string.Join(" ", c)
                        + " Resolve the duplicate path (rename or delete one) before saving.";
                }
                else if (WorkingGeneration != generationAtAccept)
                {
                    // A concurrent edit/rename/delete/create landed during
                    // the I/O; it is superimposed on the restored snapshot
                    // and stays dirty for the next save.
                    SavePhase = SavePhase.Dirty;
                    LastError = "Restore committed; your later edits were kept and are still unsaved.";
                }
                else
                {
                    SavePhase = SavePhase.Saved;
                    LastError = null;
                }
                // Raise ONCE at the end of the successful restore, AFTER the
                // synchronous reconcile (ApplySnapshot/ApplyConcurrentChanges)
                // and conflict/status logic is finished. Do NOT raise from inside
                // ApplySnapshot, and do NOT bump WorkingGeneration here.
                RaiseWorkspaceChanged();
            }
            else
            {
                LastError = result.SafeMessage ?? "Restore failed.";
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    // ---- recovery ----

    public async Task<CommitResult> RecoverToAsync(RevisionId targetRevision, CancellationToken ct)
    {
        if (Project is null || Recovery is null) return CommitResult.Unavailable("No recovery in progress.");
        var damagedHead = Recovery.DamagedHead;
        var result = await _repo.RepairHeadAsync(Project.Id, damagedHead, targetRevision, ct);
        if (result.Status == CommitStatus.Saved)
        {
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
        => Files.Any(f => !f.Deleted && ProjectPathValidator.ComparisonKey(f.RelativePath) == ProjectPathValidator.ComparisonKey(normalized));

    public async ValueTask DisposeAsync()
    {
        await ReleaseLockAsync(CancellationToken.None);
        _gate.Dispose();
    }
}

public enum SavePhase { Idle, Dirty, Saving, Saved, Failed }

/// <summary>Mutable working copy of a file's state since the last durable
/// revision. Text files carry decoded <see cref="Content"/> and an optional
/// <see cref="ImportedBytes"/> holding the exact pre-edit bytes (so an unedited
/// import preserves BOM/CRLF). Binary files carry <see cref="BinaryContent"/>
/// and are never text-edited. The durable hash/length let the save path reuse an
/// unchanged content object without re-writing it.</summary>
public sealed class WorkingFile
{
    public FileId Id { get; }
    public string RelativePath { get; set; }
    public string MediaKind { get; set; }
    public string Content { get; set; }
    public byte[]? BinaryContent { get; set; }
    public bool HasBom { get; set; }
    public bool DirtyContent { get; set; }
    public bool DirtyBinary { get; set; }
    public bool TreeChanged { get; set; }
    public bool IsNew { get; set; }
    public bool Deleted { get; set; }
    public SHA256? DurableHash { get; set; }
    public long DurableLength { get; set; }
    /// <summary>Exact bytes for an unedited imported text file (preserves
    /// BOM/CRLF); cleared on first edit or after the first commit.</summary>
    public byte[]? ImportedBytes { get; set; }

    public WorkingFile(
        FileId id, string relativePath, string mediaKind, string content, byte[]? binaryContent, bool hasBom,
        bool dirtyContent, bool dirtyBinary, bool treeChanged, bool isNew, bool deleted,
        SHA256? durableHash, long durableLength, byte[]? importedBytes)
    {
        Id = id;
        RelativePath = relativePath;
        MediaKind = mediaKind;
        Content = content;
        BinaryContent = binaryContent;
        HasBom = hasBom;
        DirtyContent = dirtyContent;
        DirtyBinary = dirtyBinary;
        TreeChanged = treeChanged;
        IsNew = isNew;
        Deleted = deleted;
        DurableHash = durableHash;
        DurableLength = durableLength;
        ImportedBytes = importedBytes;
    }

    public WorkingFile Clone() => new(Id, RelativePath, MediaKind, Content, BinaryContent, HasBom,
        DirtyContent, DirtyBinary, TreeChanged, IsNew, Deleted, DurableHash, DurableLength, ImportedBytes);
}

/// <summary>A restore preview: a freshness token, the read-only change summary
/// and the historical revision to restore. Acceptance re-validates the token and
/// commits a new revision whose parent is the current head.</summary>
public record RestorePreview(PreviewToken Token, ComparisonSummary Summary, ProjectRevision HistoricalRevision);

/// <summary>A per-field concurrent change captured during a restore's I/O,
/// tracked separately so a rename with no content edit does not copy the
/// (unchanged) pre-restore content over the restored content. Only the
/// fields actually changed during the I/O are applied.</summary>
internal sealed record ConcurrentChange(
    FileId Id,
    WorkingFile File,       // the post-I/O working file (post-edit state)
    bool IsNew,             // created during I/O (absent at accept)
    bool Deleted,
    bool ContentChanged,
    bool PathChanged,
    bool BinaryChanged)
{
    public static ConcurrentChange ForNew(WorkingFile f) => new(f.Id, f, true, f.Deleted,
        f.DirtyContent, true, f.DirtyBinary);
    public static ConcurrentChange Of(WorkingFile f, bool contentChanged, bool pathChanged, bool deleted) => new(
        f.Id, f, false, deleted, contentChanged, pathChanged, f.DirtyBinary);
}
