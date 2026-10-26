using System.Collections.Immutable;
using System.Text;
using Microsoft.JSInterop;
using Client.Workspace.Domain;
using Client.Workspace.Storage;
using Client.Workspace.Transfer;
using Client.Workspace.Renaming;

namespace Client.Workspace;

/// <summary>
/// OPFS + IndexedDB <see cref="IProjectRepository"/>. C# owns the commit
/// protocol, content verification and recovery decisions; the JS module is pure
/// transport. There is no cross-store transaction: content objects are written
/// and verified before the manifest/head IndexedDB transaction runs, and a
/// failed transaction leaves unreachable objects (not an advanced head).
/// </summary>
public sealed class WorkspaceStorage : IProjectRepository
{
    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;

    public WorkspaceStorage(IJSRuntime js) => _js = js;

    private async Task<IJSObjectReference> ModuleAsync(CancellationToken ct)
    {
        if (_module is not null) return _module;
        _module = await _js.InvokeAsync<IJSObjectReference>("import", "./js/workspace-storage.js");
        return _module;
    }

    private static byte[] ToUtf8(string text) => Encoding.UTF8.GetBytes(text);
    private static string FromUtf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static SHA256 HashBytes(byte[] bytes)
        => SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes));

    // Strict UTF-8 decode so invalid bytes are never silently replaced; the
    // caller (coordinator) decides text vs binary using the validator's media
    // kind. BOM presence is detected from raw bytes, not from the manifest, so
    // legacy M1a text records without HasBom still preserve BOM after an edit.
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // ---- project CRUD ----

    public async Task<Project> CreateProjectAsync(string name, CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        var project = new Project(ProjectId.New(), name, null);
        await m.InvokeVoidAsync("putProject", Mapping.ToRecord(project));
        return project;
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        var records = await m.InvokeAsync<List<ProjectRecord>>("listProjects") ?? new();
        return records.Select(Mapping.ToDomain).ToList();
    }

    public async Task DeleteProjectAsync(ProjectId id, CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        await m.InvokeVoidAsync("deleteProject", id.ToString());
    }

    public async Task<ProjectRenameResult> RenameProjectAsync(ProjectRenameSnapshot expected, string normalizedName, string writerLockId, CancellationToken ct)
    {
        if (!ProjectRenameName.TryNormalize(normalizedName, out string validated) || validated != normalizedName)
            return new(ProjectRenameCode.InvalidName);
        if (ct.IsCancellationRequested) return new(ProjectRenameCode.Cancelled);
        CancellationTokenRegistration registration = default;
        Task? cancellation = null;
        try
        {
            var module = await RenameTransport(() => ModuleAsync(CancellationToken.None)).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return new(ProjectRenameCode.Cancelled);
            registration = ct.Register(() =>
            {
                try
                {
                    cancellation = RenameTransport(() => module.InvokeAsync<bool>("cancelProjectRename", CancellationToken.None,
                        new object?[] { writerLockId, expected.Id.ToString() })).AsTask();
                }
                catch (Exception e) when (e is not OutOfMemoryException) { cancellation = Task.CompletedTask; }
            });
            if (ct.IsCancellationRequested) return new(ProjectRenameCode.Cancelled);
            // Explicit None avoids an SDK cancellation/default-timeout proxy.
            // The acquired lock remains held through the ACTUAL transaction reply.
            var reply = await RenameTransport(() => module.InvokeAsync<RenameReply>("renameProjectName", CancellationToken.None,
                new object?[] { expected.Id.ToString(), expected.Name, expected.Head?.ToString(), expected.SchemaVersion, normalizedName, writerLockId })).ConfigureAwait(false);
            return new(reply?.Status switch
            {
                "applied" => ProjectRenameCode.Applied, "unchanged" => ProjectRenameCode.Unchanged,
                "stale" => ProjectRenameCode.Stale, "missing" => ProjectRenameCode.Missing,
                "writerUnavailable" => ProjectRenameCode.WriterUnavailable, "cancelled" => ProjectRenameCode.Cancelled,
                "unsupported" => ProjectRenameCode.Unsupported, _ => ProjectRenameCode.Failed
            }); // Never turn committed success into Cancelled after completion.
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return new(ProjectRenameCode.Failed); }
        finally
        {
            registration.Dispose(); // Settle callback assignment before observing its real task.
            if (cancellation is not null)
            {
                try { await cancellation.ConfigureAwait(false); }
                catch (Exception e) when (e is not OutOfMemoryException) { }
            }
        }
    }
    private sealed class RenameReply { public RenameReply() { } public string? Status { get; set; } }
    private static T RenameTransport<T>(Func<T> begin)
    {
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
        try { return begin(); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    // ---- open + recovery ----

    public async Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        var record = await m.InvokeAsync<ProjectRecord?>("getProject", id.ToString())
            ?? throw new WorkspaceException("ProjectNotFound", "No such project in local storage.");
        var project = Mapping.ToDomain(record);

        if (project.HeadRevisionId is null)
        {
            return new ProjectSnapshot(project, null,
                new Dictionary<FileId, string>(),
                new Dictionary<FileId, byte[]>(),
                new Dictionary<FileId, bool>(), false, null);
        }

        var headId = project.HeadRevisionId.Value;
        var manifest = await m.InvokeAsync<RevisionRecord?>("getRevision", headId.ToString());
        if (manifest is null)
        {
            return new ProjectSnapshot(project, null,
                new Dictionary<FileId, string>(),
                new Dictionary<FileId, byte[]>(),
                new Dictionary<FileId, bool>(), false,
                await BuildRecoveryReportAsync(m, project.Id, headId, "Head revision manifest is missing.", ct));
        }

        var revision = Mapping.ToDomain(manifest);
        var verification = await VerifyRevisionAsync(m, revision, ct);
        if (!verification.Ok)
        {
            return new ProjectSnapshot(project, null,
                new Dictionary<FileId, string>(),
                new Dictionary<FileId, byte[]>(),
                new Dictionary<FileId, bool>(), false,
                await BuildRecoveryReportAsync(m, project.Id, headId,
                    $"Head revision content is damaged: {verification.Reason}.", ct));
        }

        var (text, binary, hasBom) = await LoadContentAsync(m, revision, ct);
        return new ProjectSnapshot(project, revision, text, binary, hasBom, false, null);
    }

    private async Task<RecoveryReport> BuildRecoveryReportAsync(
        IJSObjectReference m, ProjectId projectId, RevisionId damagedHead, string reason, CancellationToken ct)
    {
        var checkpoints = await m.InvokeAsync<List<CheckpointRecord>>("listCheckpoints", projectId.ToString())
            ?? new();
        var complete = new List<HistoryCheckpoint>();
        foreach (var c in checkpoints.OrderByDescending(c => c.CreatedAt))
        {
            var rev = await m.InvokeAsync<RevisionRecord?>("getRevision", c.RevisionId);
            if (rev is null) continue;
            var v = await VerifyRevisionAsync(m, Mapping.ToDomain(rev), ct);
            if (v.Ok) complete.Add(Mapping.ToDomain(c));
        }
        return new RecoveryReport(damagedHead, complete);
    }

    private async Task<(bool Ok, string Reason)> VerifyRevisionAsync(
        IJSObjectReference m, ProjectRevision revision, CancellationToken ct)
    {
        foreach (var file in revision.Files)
        {
            var v = await m.InvokeAsync<VerifyContentResult>("verifyContent",
                file.ContentHash.Hex, file.ByteLength);
            if (!v.Exists) return (false, $"missing content {file.ContentHash.Hex}");
            if (v.Length != file.ByteLength) return (false, $"length mismatch for {file.RelativePath}");
            if (!v.HashMatch) return (false, $"hash mismatch for {file.RelativePath}");
        }
        return (true, "");
    }

    /// <summary>Load decoded text for text files and raw bytes for binary
    /// files. Text is decoded with strict UTF-8 (BOM stripped); BOM presence is
    /// detected from raw bytes so legacy records without HasBom still round-trip.
    /// Binary files are never decoded.</summary>
    private async Task<(
        IReadOnlyDictionary<FileId, string> Text,
        IReadOnlyDictionary<FileId, byte[]> Binary,
        IReadOnlyDictionary<FileId, bool> HasBom)> LoadContentAsync(
        IJSObjectReference m, ProjectRevision revision, CancellationToken ct)
    {
        var text = new Dictionary<FileId, string>();
        var binary = new Dictionary<FileId, byte[]>();
        var bom = new Dictionary<FileId, bool>();
        foreach (var file in revision.Files)
        {
            var bytes = await m.InvokeAsync<byte[]>("readContent", file.ContentHash.Hex);
            bool hasBom = bytes.Length >= 3 &&
                bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            bom[file.Id] = hasBom;
            if (file.MediaKind == "text")
            {
                var span = hasBom ? bytes.AsSpan(3) : bytes.AsSpan();
                text[file.Id] = StrictUtf8.GetString(span);
            }
            else
            {
                binary[file.Id] = bytes;
            }
        }
        return (text, binary, bom);
    }

    // ---- commit ----

    /// <summary>Validate every manifest in a commit for duplicate/prefix path
    /// conflicts BEFORE any storage work. Returns a structured failure or null
    /// (valid). The repository must never depend on the caller having validated
    /// the manifest — a save/restore/import that would commit an invalid tree is
    /// rejected here without invoking interop or writing content/checkpoints.</summary>
    private static CommitResult? ValidateManifestPaths(IEnumerable<ProjectRevision> manifests, string label)
    {
        foreach (var manifest in manifests)
        {
            var conflicts = ProjectTreeValidator.FindPathConflicts(
                manifest.Files.Select(f => f.RelativePath));
            if (conflicts is { } c && c.Count > 0)
                return CommitResult.Failure("InvalidManifest",
                    $"{label}: revision {manifest.Id} would commit conflicting paths: "
                    + string.Join(" ", c) + ". Resolve before saving.");
        }
        return null;
    }

    public async Task<CommitResult> CommitAsync(
        ProjectCommit batch, RevisionId? expectedHead, CancellationToken ct)
    {
        // Validate the selected head AND every additional manifest before any
        // storage work — never let an invalid tree reach interop or advance head.
        if (ValidateManifestPaths(batch.Manifests, "Save") is { } invalid)
            return invalid;

        var m = await ModuleAsync(ct);

        // 1. Write and verify new content objects before any manifest write.
        foreach (var input in batch.Content)
        {
            var expectedLen = input.Bytes.Length;
            if (input.ByteLength != expectedLen)
                return CommitResult.Failure("ContentLengthMismatch",
                    "Reported byte length does not match the provided bytes.");
            if (HashBytes(input.Bytes).Hex != input.Hash.Hex)
                return CommitResult.Failure("ContentHashMismatch",
                    "Reported content hash does not match the provided bytes.");
            WriteContentResult written;
            try
            {
                written = await m.InvokeAsync<WriteContentResult>("writeContent",
                    input.Hash.Hex, input.Bytes);
            }
            catch (Exception ex)
            {
                return ClassifyStorageException(ex);
            }
            if (!written.Verified || written.ByteLength != expectedLen)
                return CommitResult.Failure("CorruptStorage",
                    "Content object failed verification after write.");
        }

        // 2. Verify all referenced content objects (including reused ones) are
        //    present and intact, so a damaged reused blob cannot advance head.
        foreach (var file in batch.SelectedHead.Files)
        {
            var v = await m.InvokeAsync<VerifyContentResult>("verifyContent",
                file.ContentHash.Hex, file.ByteLength);
            if (!v.Exists || v.Length != file.ByteLength || !v.HashMatch)
                return CommitResult.Failure("CorruptStorage",
                    $"Referenced content for {file.RelativePath} is missing or damaged.");
        }

        // 3. One IndexedDB transaction: store manifests + checkpoint, advance
        //    head only if it still matches expectedHead (optimistic CAS).
        var manifestRecords = batch.Manifests.Select(Mapping.ToRecord).ToList();
        var checkpoint = new HistoryCheckpoint(
            CheckpointId.New(), batch.SelectedHead.ProjectId, batch.SelectedHead.Id,
            "save", DateTimeOffset.UtcNow);
        var checkpointRecord = Mapping.ToRecord(checkpoint);

        CommitBatchResult result;
        try
        {
            result = await m.InvokeAsync<CommitBatchResult>("commitBatch",
                batch.SelectedHead.ProjectId.ToString(),
                expectedHead?.ToString(),
                batch.SelectedHead.Id.ToString(),
                manifestRecords,
                checkpointRecord,
                null);
        }
        catch (Exception ex)
        {
            return ClassifyStorageException(ex);
        }

        if (result.Ok)
            return CommitResult.Saved(batch.SelectedHead.Id);

        if (result.ErrorCode == "RevisionConflict")
        {
            RevisionId? actual = null;
            if (!string.IsNullOrEmpty(result.ActualHead) && Guid.TryParse(result.ActualHead, out var g))
                actual = new RevisionId(g);
            return CommitResult.Conflict(actual);
        }
        return CommitResult.Failure(result.ErrorCode ?? "StorageFailure",
            "Local storage rejected the commit.");
    }

    public async Task<CommitResult> RepairHeadAsync(
        ProjectId id, RevisionId expectedCurrentHead, RevisionId newHead, CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        CommitBatchResult result;
        try
        {
            result = await m.InvokeAsync<CommitBatchResult>("commitBatch",
                id.ToString(), expectedCurrentHead.ToString(), newHead.ToString(),
                new List<RevisionRecord>(), null, null);
        }
        catch (Exception ex)
        {
            return ClassifyStorageException(ex);
        }
        if (result.Ok) return CommitResult.Saved(newHead);
        if (result.ErrorCode == "RevisionConflict")
        {
            RevisionId? actual = null;
            if (!string.IsNullOrEmpty(result.ActualHead) && Guid.TryParse(result.ActualHead, out var g))
                actual = new RevisionId(g);
            return CommitResult.Conflict(actual);
        }
        return CommitResult.Failure(result.ErrorCode ?? "StorageFailure",
            "Local storage rejected the recovery commit.");
    }

    // ---- M1b: arbitrary revision read + raw content bytes ----

    public async Task<bool> VerifyHeadAsync(ProjectId id, RevisionId? expectedHead, CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        async Task<bool> Matches()
        {
            var p = await m.InvokeAsync<ProjectRecord?>("getProject", ct, id.ToString());
            return p is not null && Mapping.ToDomain(p).Id == id && Mapping.ToDomain(p).HeadRevisionId == expectedHead;
        }
        if (!await Matches()) return false;
        if (expectedHead is { } head && !(await ReadRevisionAsync(id, head, ct)).Verified) return false;
        // Verification can await many OPFS reads. Check the head again afterwards.
        return await Matches();
    }

    public async Task<RevisionManifest> ReadRevisionAsync(
        ProjectId projectId, RevisionId revisionId, CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        var manifest = await m.InvokeAsync<RevisionRecord?>("getRevision", revisionId.ToString());
        if (manifest is null)
            return RevisionManifest.Damaged(new ProjectRevision(
                revisionId, projectId, null, ImmutableArray<ProjectFile>.Empty, DateTimeOffset.UtcNow),
                "Revision manifest is missing.");
        if (manifest.ProjectId != projectId.ToString())
            return RevisionManifest.Damaged(Mapping.ToDomain(manifest),
                "Revision does not belong to this project.");
        var revision = Mapping.ToDomain(manifest);
        var v = await VerifyRevisionAsync(m, revision, ct);
        return v.Ok ? RevisionManifest.Ok(revision)
            : RevisionManifest.Damaged(revision, $"Content is damaged: {v.Reason}.");
    }

    public async Task<byte[]> ReadContentAsync(SHA256 hash, CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        return await m.InvokeAsync<byte[]>("readContent", hash.Hex);
    }

    // ---- M1b: atomic import publish ----

    public async Task<ImportPublishResult> PublishImportAsync(StagedProject staged, CancellationToken ct)
    {
        // Validate the staged tree before any storage work — never let an invalid
        // import (duplicate/prefix paths) reach interop or create a project row.
        var conflicts = ProjectTreeValidator.FindPathConflicts(
            staged.Files.Select(f => f.RelativePath));
        if (conflicts is { } c && c.Count > 0)
            return FailImport("InvalidManifest",
                "Import would commit conflicting paths: " + string.Join(" ", c)
                + ". Resolve before importing.");

        var m = await ModuleAsync(ct);

        // 1. Write and verify every staged content object before any project
        //    record exists. A failure here leaves only unreferenced staging
        //    objects — never an empty project in the list.
        var fileRecords = new List<FileRecord>(staged.Files.Length);
        foreach (var f in staged.Files)
        {
            if (f.ByteLength != f.Bytes.Length)
                return FailImport("ContentLengthMismatch",
                    "Reported byte length does not match staged bytes.");
            if (HashBytes(f.Bytes).Hex != f.Hash.Hex)
                return FailImport("ContentHashMismatch",
                    "Reported content hash does not match staged bytes.");
            WriteContentResult written;
            try
            {
                written = await m.InvokeAsync<WriteContentResult>("writeContent", f.Hash.Hex, f.Bytes);
            }
            catch (Exception ex)
            {
                return FailImport("StorageFailure", ClassifyMessage(ex));
            }
            if (!written.Verified || written.ByteLength != f.Bytes.Length)
                return FailImport("CorruptStorage", "Content object failed verification after write.");
            fileRecords.Add(new FileRecord
            {
                Id = FileId.New().ToString(),
                RelativePath = f.RelativePath,
                MediaKind = f.MediaKind,
                ContentHash = f.Hash.Hex,
                ByteLength = f.ByteLength,
                HasBom = f.HasBom,
            });
        }

        // 2. One IndexedDB transaction: put project + initial revision manifest +
        //    import checkpoint, set the head. If anything fails, no project row
        //    is committed (the JS function aborts before putting the project).
        var project = new Project(ProjectId.New(), staged.Name, null);
        var revision = new ProjectRevision(
            RevisionId.New(), project.Id, null,
            ImmutableArray.CreateRange(fileRecords.Select(Mapping.ToDomain)),
            DateTimeOffset.UtcNow);
        var checkpoint = new HistoryCheckpoint(
            CheckpointId.New(), project.Id, revision.Id, "import", DateTimeOffset.UtcNow);

        ImportResult inner;
        try
        {
            inner = await m.InvokeAsync<ImportResult>("publishImport",
                Mapping.ToRecord(project),
                Mapping.ToRecord(revision),
                Mapping.ToRecord(checkpoint));
        }
        catch (Exception ex)
        {
            return FailImport("StorageFailure", ClassifyMessage(ex));
        }
        if (inner.Published)
            return new ImportPublishResult(true, project, revision.Id, null, null);
        return new ImportPublishResult(false, null, null,
            string.IsNullOrEmpty(inner.ErrorCode) ? "StorageFailure" : inner.ErrorCode,
            string.IsNullOrEmpty(inner.SafeMessage) ? "Import publish failed." : inner.SafeMessage);
    }

    private static ImportPublishResult FailImport(string code, string message)
        => new(false, null, null, code, message);

    // ---- M1b: restore as a new revision ----

    public async Task<CommitResult> CreateRestoreRevisionAsync(
        ProjectId projectId,
        RevisionId expectedHead,
        ProjectRevision restoredRevision,
        RevisionId sourceRevisionId,
        string reason,
        CancellationToken ct)
    {
        // Validate the restored manifest before any storage work — never let an
        // invalid restore (duplicate/prefix paths) reach interop or advance head.
        if (ValidateManifestPaths(new[] { restoredRevision }, "Restore") is { } invalid)
            return invalid;

        var m = await ModuleAsync(ct);

        // Verify the restored revision's referenced objects exist and are intact
        // before committing — a missing/corrupt historical blob must not advance
        // the head. (The caller should also verify via ReadRevisionAsync.)
        foreach (var file in restoredRevision.Files)
        {
            var v = await m.InvokeAsync<VerifyContentResult>("verifyContent",
                file.ContentHash.Hex, file.ByteLength);
            if (!v.Exists || v.Length != file.ByteLength || !v.HashMatch)
                return CommitResult.Failure("CorruptStorage",
                    $"Referenced content for {file.RelativePath} is missing or damaged.");
        }

        var checkpoint = new HistoryCheckpoint(
            CheckpointId.New(), projectId, restoredRevision.Id,
            $"restore:{reason}:{sourceRevisionId}", DateTimeOffset.UtcNow);
        var manifestRecords = new List<RevisionRecord> { Mapping.ToRecord(restoredRevision) };
        var checkpointRecord = Mapping.ToRecord(checkpoint);

        CommitBatchResult result;
        try
        {
            result = await m.InvokeAsync<CommitBatchResult>("commitBatch",
                projectId.ToString(),
                expectedHead.ToString(),
                restoredRevision.Id.ToString(),
                manifestRecords,
                checkpointRecord,
                null);
        }
        catch (Exception ex)
        {
            return ClassifyStorageException(ex);
        }
        if (result.Ok)
            return CommitResult.Saved(restoredRevision.Id);
        if (result.ErrorCode == "RevisionConflict")
        {
            RevisionId? actual = null;
            if (!string.IsNullOrEmpty(result.ActualHead) && Guid.TryParse(result.ActualHead, out var g))
                actual = new RevisionId(g);
            return CommitResult.Conflict(actual);
        }
        return CommitResult.Failure(result.ErrorCode ?? "StorageFailure",
            "Local storage rejected the restore commit.");
    }

    // ---- paged history (newest first, 50/page, stable cursor) ----

    public async Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        var records = await m.InvokeAsync<List<CheckpointRecord>>("listCheckpoints", id.ToString())
            ?? new();
        // Stable newest-first ordering: by CreatedAt desc, then Id desc so equal
        // timestamps (common — saves within the same millisecond) are still
        // deterministically ordered and the cursor resumes without skipping.
        var ordered = records
            .Select(Mapping.ToDomain)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id.ToString())
            .ToList();

        // Decode the opaque cursor: {t, id} of the last item already returned.
        DateTimeOffset? afterTime = null;
        string? afterId = null;
        if (cursor is { Value: { } v } && !string.IsNullOrEmpty(v))
        {
            try
            {
                var parts = v.Split('|');
                afterTime = DateTimeOffset.Parse(parts[0], null,
                    System.Globalization.DateTimeStyles.AssumeUniversal);
                afterId = parts[1];
            }
            catch
            {
                // An unparseable cursor returns the first page rather than throwing.
            }
        }

        var page = ordered.AsEnumerable();
        if (afterTime is { } at && afterId is { } ai)
        {
            page = page.SkipWhile(c =>
                c.CreatedAt != at || c.Id.ToString() != ai)
              .Skip(1);
        }

        var pageSize = ArchiveLimits.HistoryPageSize;
        var taken = page.Take(pageSize + 1).ToList();
        string? next = null;
        if (taken.Count > pageSize)
        {
            var last = taken[pageSize - 1];
            next = last.CreatedAt.UtcDateTime.ToString("O",
                System.Globalization.DateTimeFormatInfo.InvariantInfo) + "|" + last.Id.ToString();
            taken = taken.Take(pageSize).ToList();
        }
        return new HistoryPage(taken, next);
    }

    private static CommitResult ClassifyStorageException(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("QuotaExceeded", StringComparison.Ordinal))
            return CommitResult.Failure("QuotaExceeded", "Local storage is full.");
        return CommitResult.Failure("StorageFailure", $"Local storage error: {msg}");
    }

    private static string ClassifyMessage(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("QuotaExceeded", StringComparison.Ordinal))
            return "Local storage is full.";
        return $"Local storage error: {msg}";
    }
}
