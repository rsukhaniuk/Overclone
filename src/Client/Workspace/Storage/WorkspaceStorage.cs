using System.Collections.Immutable;
using System.Text;
using Microsoft.JSInterop;
using Client.Workspace.Domain;
using Client.Workspace.Storage;

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
                new Dictionary<FileId, string>(), false, null);
        }

        var headId = project.HeadRevisionId.Value;
        var manifest = await m.InvokeAsync<RevisionRecord?>("getRevision", headId.ToString());
        if (manifest is null)
        {
            return new ProjectSnapshot(project, null,
                new Dictionary<FileId, string>(), false,
                await BuildRecoveryReportAsync(m, project.Id, headId, "Head revision manifest is missing.", ct));
        }

        var verification = await VerifyRevisionAsync(m, Mapping.ToDomain(manifest), ct);
        if (!verification.Ok)
        {
            return new ProjectSnapshot(project, null,
                new Dictionary<FileId, string>(), false,
                await BuildRecoveryReportAsync(m, project.Id, headId,
                    $"Head revision content is damaged: {verification.Reason}.", ct));
        }

        var content = await LoadContentAsync(m, Mapping.ToDomain(manifest), ct);
        return new ProjectSnapshot(project, Mapping.ToDomain(manifest), content, false, null);
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

    private async Task<IReadOnlyDictionary<FileId, string>> LoadContentAsync(
        IJSObjectReference m, ProjectRevision revision, CancellationToken ct)
    {
        var dict = new Dictionary<FileId, string>();
        foreach (var file in revision.Files)
        {
            if (file.MediaKind != "text") continue;
            var bytes = await m.InvokeAsync<byte[]>("readContent", file.ContentHash.Hex);
            dict[file.Id] = FromUtf8(bytes);
        }
        return dict;
    }

    // ---- commit ----

    public async Task<CommitResult> CommitAsync(
        ProjectCommit batch, RevisionId? expectedHead, CancellationToken ct)
    {
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

    public async Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        var records = await m.InvokeAsync<List<CheckpointRecord>>("listCheckpoints", id.ToString())
            ?? new();
        var checkpoints = records
            .OrderByDescending(r => r.CreatedAt)
            .Select(Mapping.ToDomain)
            .ToList();
        return new HistoryPage(checkpoints, null);
    }

    private static CommitResult ClassifyStorageException(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("QuotaExceeded", StringComparison.Ordinal))
            return CommitResult.Failure("QuotaExceeded", "Local storage is full.");
        return CommitResult.Failure("StorageFailure", $"Local storage error: {msg}");
    }
}
