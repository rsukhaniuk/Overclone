using System.Collections.Immutable;
using System.Text;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;
using ContentHash = Client.Workspace.Domain.SHA256;

namespace Client.Compilation;

/// <summary>Optional owner-context observation, not a wait or cancellation port.
/// Implementations must return promptly without throwing. Only local capture/
/// workflow tasks are reported, never arbitrary foreign I/O, workers or callbacks.
/// Observation may remain pending; normal DisposeAsync semantics are unchanged.</summary>
public interface ICompilationOwnedOperationObserver
{
    void ObserveOwnedOperation(Task operation, CompilationOwnedOperationKind kind);
}

public enum CompilationOwnedOperationKind { Capture, Workflow }

/// <summary>Read-only UI-context capture. All file/metadata limits are preflighted
/// before any byte copy/read. Mutable current buffers are copied before the first
/// await; unused mutable buffers are detached from clones. Never saves, acquires
/// a writer lock, changes selection, or calls analysis/review/native services.</summary>
public sealed class CompilationSnapshotFactory : IAsyncDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly CompilationSelectionState _selection;
    private readonly ICompilationOwnedOperationObserver? _observer;
    private readonly Guid _serviceId = Guid.NewGuid();
    private readonly object _lifecycle = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;
    private long _epoch;

    public CompilationSnapshotFactory(WorkspaceCoordinator workspace, CompilationSelectionState selection)
    {
        _workspace = workspace;
        _selection = selection;
        _observer = SynchronizationContext.Current as ICompilationOwnedOperationObserver;
        _workspace.WorkspaceChanged += OnWorkspaceChanged;
    }
    public long CurrentWorkspaceEpoch => Interlocked.Read(ref _epoch);
    private void OnWorkspaceChanged() => Interlocked.Increment(ref _epoch);

    public Task<CompilationCaptureResult> CaptureAsync(CompilationCaptureBudget budget, CancellationToken ct)
    {
        CancellationTokenSource linked;
        lock (_lifecycle)
        {
            if (_disposed != 0) return Task.FromResult<CompilationCaptureResult>(new CompilationCaptureResult.Disposed());
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        }
        var operation = CaptureCoreAsync(budget, ct, linked);
        _observer?.ObserveOwnedOperation(operation, CompilationOwnedOperationKind.Capture);
        return operation;
    }

    /// <summary>Local freshness only, not write permission or engine capability.
    /// Workspace mutations must use its trusted notification/generation seam.
    /// Raw external array mutation cannot retarget privately owned captured bytes.</summary>
    public bool IsCurrent(CompilationInputSnapshot snapshot)
        => snapshot is not null && Volatile.Read(ref _disposed) == 0 && snapshot.Identity.ServiceId == _serviceId
        && Matches(snapshot.Identity, snapshot.Selection);

    private bool Matches(CompilationSnapshotIdentity identity, CompilationSelection selection)
    {
        var current = _selection.Current;
        return _workspace.Project is { } project && project.Id == identity.ProjectId
            && _workspace.SessionId == identity.SessionId && _workspace.WorkingGeneration == identity.WorkingGeneration
            && CurrentWorkspaceEpoch == identity.LocalWorkspaceEpoch
            && _workspace.CurrentRevision?.Id == identity.BaseHeadRevisionId && project.HeadRevisionId == identity.BaseHeadRevisionId
            && (_workspace.CurrentRevision is null || _workspace.CurrentRevision.ProjectId == project.Id)
            && current.Version == identity.SelectionVersion && ReferenceEquals(current.Selection, selection);
    }

    private enum SourceKind { Durable, Binary, Imported, Edited, Reviewed }
    private sealed record FilePlan(WorkingFile Source, SourceKind Kind, long Length);

    private async Task<CompilationCaptureResult> CaptureCoreAsync(CompilationCaptureBudget budget,
        CancellationToken caller, CancellationTokenSource linked)
    {
        using (linked)
        {
            if (Volatile.Read(ref _disposed) != 0) return new CompilationCaptureResult.Disposed();
            if (caller.IsCancellationRequested) return new CompilationCaptureResult.Cancelled();
            if (_workspace.Project is not { } project) return new CompilationCaptureResult.NoProject();
            var stamp = _selection.Current;
            if (stamp.Selection is not { } selection) return new CompilationCaptureResult.NoSelection();
            if (budget is null || !budget.IsValid) return new CompilationCaptureResult.InvalidInput(CompilationCaptureIssue.InvalidBudget);
            var head = _workspace.CurrentRevision?.Id;
            if (project.Id.Value == Guid.Empty || _workspace.SessionId == Guid.Empty || _workspace.WorkingGeneration < 0
                || project.HeadRevisionId != head || head?.Value == Guid.Empty
                || (_workspace.CurrentRevision is not null && _workspace.CurrentRevision.ProjectId != project.Id))
                return new CompilationCaptureResult.Unavailable(CompilationCaptureIssue.InvalidWorkspace);
            var identity = new CompilationSnapshotIdentity(Guid.NewGuid(), _serviceId, project.Id, _workspace.SessionId,
                _workspace.WorkingGeneration, head, CurrentWorkspaceEpoch, stamp.Version);
            CompilationCaptureResult? Invalidated() => Volatile.Read(ref _disposed) != 0 ? new CompilationCaptureResult.Disposed()
                : caller.IsCancellationRequested ? new CompilationCaptureResult.Cancelled()
                : !Matches(identity, selection) ? new CompilationCaptureResult.Superseded() : null;

            // Pass 1: bounded metadata/length validation only; no file bytes copied
            // or materialized, including the binary assets the analyzer excludes.
            var plans = new List<FilePlan>();
            var ids = new HashSet<FileId>();
            CompilationFileReference? affected = null;
            long total = 0;
            long metadataLength;
            try
            {
                using var metadata = new CompilationCanonical(budget.MaxMetadataBytes);
                metadata.Text(CompilationCanonical.Domain); metadata.Selection(selection); metadata.Int32(0); // count-field width
                foreach (var live in _workspace.Files)
                {
                    if (live is null) return new CompilationCaptureResult.InvalidInput(CompilationCaptureIssue.InvalidFile);
                    if (live.Deleted) continue;
                    if (plans.Count >= budget.MaxFiles) return new CompilationCaptureResult.QuotaExceeded(CompilationCaptureIssue.FileCountLimit);
                    affected = new(live.Id, null);
                    if (live.Id.Value == Guid.Empty || !ids.Add(live.Id))
                        return new CompilationCaptureResult.InvalidInput(CompilationCaptureIssue.DuplicateFile, affected);
                    if (live.RelativePath is null || live.RelativePath != ProjectPathValidator.Normalize(live.RelativePath)
                        || ProjectPathValidator.Validate(live.RelativePath) is not null)
                        return new CompilationCaptureResult.InvalidInput(CompilationCaptureIssue.InvalidPath, affected);
                    var plan = Plan(live);
                    if (plan is null) return new CompilationCaptureResult.InvalidInput(CompilationCaptureIssue.InvalidFile, affected);
                    if (plan.Length > budget.MaxFileBytes)
                        return new CompilationCaptureResult.QuotaExceeded(CompilationCaptureIssue.FileByteLimit, affected);
                    if (plan.Length > budget.MaxTotalBytes - total)
                        return new CompilationCaptureResult.QuotaExceeded(CompilationCaptureIssue.TotalByteLimit, affected);
                    total = checked(total + plan.Length);
                    metadata.MeasureFile(live.Id, live.RelativePath, live.MediaKind, live.HasBom, plan.Length);
                    affected = new(live.Id, live.RelativePath); // Only validated bounded paths enter errors.
                    plans.Add(plan);
                }
                if (!plans.Any(p => p.Source.Id == selection.MainFileId && p.Source.MediaKind == "text"))
                    return new CompilationCaptureResult.InvalidInput(CompilationCaptureIssue.InvalidMainFile);
                if (ProjectTreeValidator.FindPathConflicts(plans.Select(p => p.Source.RelativePath)) is { Count: > 0 })
                    return new CompilationCaptureResult.InvalidInput(CompilationCaptureIssue.InvalidPath);
                metadataLength = metadata.Length;
            }
            catch (CompilationInputException error)
            {
                return error.Issue == CompilationCaptureIssue.MetadataLimit
                    ? new CompilationCaptureResult.QuotaExceeded(error.Issue, affected)
                    : new CompilationCaptureResult.InvalidInput(error.Issue, affected);
            }

            // Pass 2: all preflight passed. Only private clones survive into I/O.
            // Clone alone aliases BinaryContent/ImportedBytes: explicitly own used
            // buffers and detach everything not selected by GetFileBytes policy.
            var frozen = new List<FilePlan>(plans.Count);
            foreach (var plan in plans)
            {
                var clone = plan.Source.Clone();
                clone.BinaryContent = plan.Kind == SourceKind.Binary ? plan.Source.BinaryContent!.ToArray() : null;
                clone.ImportedBytes = plan.Kind == SourceKind.Imported ? plan.Source.ImportedBytes!.ToArray() : null;
                frozen.Add(new(clone, plan.Kind, plan.Length));
            }
            plans.Clear(); // Do not retain mutable live WorkingFiles across awaits.
            if (Invalidated() is { } beforeRead) return beforeRead;

            var files = ImmutableArray.CreateBuilder<CompilationInputFile>(frozen.Count);
            long actualTotal = 0;
            foreach (var plan in frozen)
            {
                var source = plan.Source;
                var file = new CompilationFileReference(source.Id, source.RelativePath);
                byte[] bytes;
                try
                {
                    // Only caller cancellation is passed to the injected port.
                    // Lifetime cancellation controls OUR wait, not foreign token
                    // callbacks; disposal never waits for noncooperative storage.
                    bytes = await _workspace.GetFileBytesAsync(source, caller).WaitAsync(linked.Token);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    return Invalidated() ?? new CompilationCaptureResult.Unavailable(CompilationCaptureIssue.ReadFailure, file);
                }
                if (Invalidated() is { } afterRead) return afterRead;
                if (bytes is null) return new CompilationCaptureResult.Unavailable(CompilationCaptureIssue.CorruptBytes, file);
                // Actual-size checks before copy/hash/decode. Existing repository
                // ports allocate their own result; capture does not duplicate an
                // over-budget or incorrectly declared result.
                if (bytes.LongLength > budget.MaxFileBytes)
                    return new CompilationCaptureResult.QuotaExceeded(CompilationCaptureIssue.FileByteLimit, file);
                if (bytes.LongLength > budget.MaxTotalBytes - actualTotal)
                    return new CompilationCaptureResult.QuotaExceeded(CompilationCaptureIssue.TotalByteLimit, file);
                actualTotal = checked(actualTotal + bytes.LongLength);
                if (bytes.LongLength != plan.Length)
                    return new CompilationCaptureResult.Unavailable(CompilationCaptureIssue.CorruptBytes, file);
                // Durable returns are untrusted aliased arrays: own before
                // verification. Other paths already return factory-private bytes.
                var owned = plan.Kind == SourceKind.Durable ? bytes.ToArray() : bytes;
                if (source.MediaKind == "text")
                {
                    if (!TextContent.TryDecode(owned, out var decoded, out var bom) || bom != source.HasBom
                        || (plan.Kind != SourceKind.Edited && decoded != source.Content))
                        return new CompilationCaptureResult.Unavailable(CompilationCaptureIssue.CorruptBytes, file);
                }
                var hash = ContentHash.FromBytes(System.Security.Cryptography.SHA256.HashData(owned));
                if (plan.Kind == SourceKind.Durable && hash != source.DurableHash)
                    return new CompilationCaptureResult.Unavailable(CompilationCaptureIssue.CorruptBytes, file);
                files.Add(new(source.Id, source.RelativePath, source.MediaKind, source.HasBom, hash, owned));
            }
            var ordered = files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ThenBy(f => f.FileId.ToString(), StringComparer.Ordinal).ToImmutableArray();
            using var canonical = new CompilationCanonical(budget.MaxMetadataBytes, hash: true);
            canonical.Text(CompilationCanonical.Domain); canonical.Selection(selection); canonical.Int32(ordered.Length);
            foreach (var file in ordered) canonical.File(file.FileId, file.RelativePath, file.MediaKind, file.HasBom, file.ByteLength, file.Hash);
            var digest = canonical.Finish();
            if (Invalidated() is { } beforePublish) return beforePublish;
            return new CompilationCaptureResult.Captured(new(identity, selection, budget, ordered, digest, metadataLength));
        }
    }

    private static FilePlan? Plan(WorkingFile file)
    {
        if (file.DurableLength < 0 || (file.DurableHash is { } hash && !CompilationCanonical.ValidHash(hash))) return null;
        if (file.MediaKind == "binary")
        {
            if (file.HasBom || file.Content != "" || file.DirtyContent || file.ImportedBytes is not null || file.PendingTextBytes is not null) return null;
            if (file.DirtyBinary)
                return file.BinaryContent is null ? null : new(file, SourceKind.Binary, file.BinaryContent.LongLength);
            if (file.DurableHash is not null) return new(file, SourceKind.Durable, file.DurableLength);
            return file.IsNew && file.BinaryContent is not null ? new(file, SourceKind.Binary, file.BinaryContent.LongLength) : null;
        }
        if (file.MediaKind != "text" || file.Content is null || file.BinaryContent is not null || file.DirtyBinary) return null;
        if (file.PendingTextBytes is { } exact)
            return exact.Content != file.Content || exact.HasBom != file.HasBom ? null : new(file, SourceKind.Reviewed, exact.Length);
        if (file.IsNew && !file.DirtyContent && file.ImportedBytes is { } imported)
            return new(file, SourceKind.Imported, imported.LongLength);
        if (file.DirtyContent || (file.IsNew && file.ImportedBytes is null))
            return TextContent.TryCountEditedBytes(file.Content, file.HasBom, out var length) ? new(file, SourceKind.Edited, length) : null;
        return file.DurableHash is null ? null : new(file, SourceKind.Durable, file.DurableLength);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposed != 0) return ValueTask.CompletedTask;
            Volatile.Write(ref _disposed, 1);
            _workspace.WorkspaceChanged -= OnWorkspaceChanged;
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }
}
