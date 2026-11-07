using System.Collections.Immutable;
using System.Text.Json;
using Client.Compilation.Assets;
using Client.RuntimeAssets;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;
using Hash = Client.Workspace.Domain.SHA256;

namespace Client.Compilation.History;

// Reuses accepted N13 content-first/lifetime/atomic-publication machinery with
// an artifact-only backend. Storage is integrity, not live compile acceptance.
public sealed class CompileArtifactHistoryStore : IAsyncDisposable
{
    // Explicit application-owned descriptor list, including retained runtime versions.
    public const int MaxAdditionalAdapters = 16;
    private readonly IRuntimeAssetBackend _backend;
    private readonly RuntimeAssetStore _content;
    private readonly IProjectRepository _repository;
    private readonly CompileOutputDescriptor _format, _adapter;
    private readonly ImmutableArray<CompileOutputDescriptor> _additionalAdapters;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task<ArtifactHistoryResult>? _active; private Task? _closing; private bool _disposed;
    private static readonly AssetId MetadataId = new(new Guid("33333333-3333-3333-3333-333333333333")), PdfId = new(new Guid("44444444-4444-4444-4444-444444444444"));
    public CompileArtifactHistoryStore(IRuntimeAssetBackend backend, IProjectRepository repository, CompileOutputDescriptor expectedPdfFormat, CompileOutputDescriptor expectedAdapter,
        IReadOnlyList<CompileOutputDescriptor>? additionalAdapters = null)
    {
        ArgumentNullException.ThrowIfNull(backend); ArgumentNullException.ThrowIfNull(repository);
        if (expectedPdfFormat is null || expectedPdfFormat.Id != "pdf" || expectedAdapter is null) throw new ArgumentException("Explicit PDF format/adapter required.");
        using var measure = new CompileArtifactCanonical(CompileArtifactCanonical.MetadataDomain, Array.MaxLength);
        measure.Descriptor(expectedPdfFormat); measure.Descriptor(expectedAdapter);
        if (additionalAdapters is { Count: > MaxAdditionalAdapters }) throw new ArgumentException("Explicit adapter count limit.");
        _additionalAdapters = additionalAdapters?.ToImmutableArray() ?? [];
        foreach (var descriptor in _additionalAdapters)
        {
            if (descriptor is null) throw new ArgumentException("Explicit adapter required.");
            measure.Descriptor(descriptor);
        }
        _backend = backend; _repository = repository; _format = expectedPdfFormat; _adapter = expectedAdapter; _content = new(new BorrowedBackend(backend));
    }

    public Task<ArtifactHistoryResult> PersistAsync(AcceptedCompileOutput output, RevisionId? explicitRevision, ArtifactHistoryBudget budget, CancellationToken caller = default)
    {
        if (output is null || budget is null || !budget.IsValid || output.Status != CompileOutputStatus.Succeeded || output.ContentIdentity is null || output.OutputHash is null
            || output.OutputFormat != _format || output.OutputAdapter != _adapter && !_additionalAdapters.Contains(output.OutputAdapter!)
            || output.OutputLength < 0 || output.OutputLength > budget.MaxPdfBytes
            || output.PayloadBytes.Length != output.OutputLength || output.Snapshot.Files.Length > budget.Input.MaxFiles || output.Snapshot.MetadataByteLength > budget.Input.MaxMetadataBytes
            || explicitRevision?.Value == Guid.Empty)
            return Task.FromResult(new ArtifactHistoryResult(RuntimeAssetStatus.InvalidInput));
        return RunAsync(budget, caller, async op =>
        {
            var metadata = HistoricalArtifactCodec.FromAccepted(output, explicitRevision);
            var validated = HistoricalArtifactCodec.Validate(metadata, output.SnapshotIdentity.ProjectId, _format, _adapter, budget, _additionalAdapters);
            if (validated.Identity != output.ContentIdentity) throw new RuntimeAssetIoException(RuntimeAssetStatus.InvalidInput);
            var encoded = HistoricalArtifactCodec.Encode(metadata, budget.MaxMetadataBytes);
            op.Check(); var association = await AssociateAsync(validated.Files, metadata.ProjectId, explicitRevision, budget, op).ConfigureAwait(false); op.Check();
            // Metadata/count/declared+actual PDF bounds are settled BEFORE copy/hash.
            byte[] pdf = output.ToPayloadArray() ?? throw new RuntimeAssetIoException(RuntimeAssetStatus.InvalidInput);
            if (HashBytes(pdf) != output.OutputHash.Value) throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
            var owned = await WrapAsync(encoded, pdf, budget, op.Token).ConfigureAwait(false); op.Check();
            op.Project = metadata.ProjectId; op.ContentDigest = metadata.ContentDigest; op.StorageDigest = owned.Identity.Digest;
            var dispatch = new TaskCompletionSource<RuntimeAssetResult>(TaskCreationOptions.RunContinuationsAsynchronously); op.StoragePublic = dispatch.Task;
            try { dispatch.TrySetResult(await _content.InstallAsync(owned, budget.Storage, op.Token).ConfigureAwait(false)); }
            catch (Exception error) { dispatch.TrySetException(error); throw; }
            var stored = await dispatch.Task.ConfigureAwait(false); var key = Key(stored, op);
            if (stored.Status is RuntimeAssetStatus.Cancelled or RuntimeAssetStatus.TimedOut or RuntimeAssetStatus.Disposed) op.Check();
            if (stored.Status != RuntimeAssetStatus.Installed || stored.Snapshot is null) return new(stored.Status, key, PublicationUncertain: stored.PublishMayHaveChanged, Cause: stored.Cause);
            var snapshot = stored.Snapshot; int meta = Find(snapshot.Inventory, MetadataId), payload = Find(snapshot.Inventory, PdfId);
            var artifact = Restore(snapshot.ToArray(meta), snapshot.ToArray(payload), key!, budget);
            return new(RuntimeAssetStatus.Installed, key, artifact, association);
        });
    }
    public Task<ArtifactHistoryResult> ReadAsync(HistoricalArtifactKey key, ProjectId expectedProject, RevisionId? explicitRevision, ArtifactHistoryBudget budget, CancellationToken caller = default)
    {
        if (!ValidKey(key, expectedProject) || budget is null || !budget.IsValid || explicitRevision?.Value == Guid.Empty)
            return Task.FromResult(new ArtifactHistoryResult(RuntimeAssetStatus.InvalidInput));
        return RunAsync(budget, caller, async op =>
        {
            op.KnownKey = key; op.Check(); string? catalog = await _backend.CatalogAsync(key.RecordId, budget.Storage.MaxCatalogBytes).ConfigureAwait(false); op.Check();
            if (catalog is null) return new(await _backend.StageExistsAsync(key.RecordId).ConfigureAwait(false) ? RuntimeAssetStatus.Incomplete : RuntimeAssetStatus.Missing, key);
            var inventory = RuntimeAssetCatalog.Decode(catalog, key.RecordId, key.StorageInventoryDigest.Hex, budget.Storage);
            if (inventory.AssetCount != 2) throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
            int meta = Find(inventory, MetadataId), payload = Find(inventory, PdfId);
            var m = inventory.Assets[meta]; var p = inventory.Assets[payload];
            if (m.RelativePath != "history/metadata.json" || p.RelativePath != "history/output.pdf" || m.DeclaredLength > budget.MaxMetadataBytes || p.DeclaredLength > budget.MaxPdfBytes)
                throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
            // Parse/validate historical manifest and selection BEFORE reading,
            // cloning or hashing the potentially large PDF. No live context made.
            byte[] metadata = await ReadObjectAsync(key.RecordId, meta, m, budget.MaxMetadataBytes, op).ConfigureAwait(false);
            var validated = HistoricalArtifactCodec.Decode(metadata, expectedProject, _format, _adapter, budget, _additionalAdapters);
            if (validated.Identity.Digest != key.ContentDigest || validated.Metadata.PdfLength != p.DeclaredLength || validated.Metadata.PdfHash != p.DeclaredHash)
                throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
            op.Check(); byte[] pdf = await ReadObjectAsync(key.RecordId, payload, p, budget.MaxPdfBytes, op).ConfigureAwait(false);
            var artifact = Restore(metadata, pdf, key, budget);
            var association = await AssociateAsync(artifact.Files, expectedProject, explicitRevision, budget, op).ConfigureAwait(false); op.Check();
            return new(RuntimeAssetStatus.Ready, key, artifact, association);
        });
    }
    public Task<ArtifactHistoryResult> DiscardAsync(HistoricalArtifactKey key, ArtifactHistoryBudget budget, CancellationToken caller = default)
    {
        if (key is null || key.RecordId == Guid.Empty || budget is null || !budget.IsValid) return Task.FromResult(new ArtifactHistoryResult(RuntimeAssetStatus.InvalidInput));
        return RunAsync(budget, caller, async op => { op.KnownKey = key; var value = await _content.DiscardAsync(key.RecordId, budget.Storage, op.Token).ConfigureAwait(false); return new(value.Status, key, PublicationUncertain: value.PublishMayHaveChanged, Cause: value.Cause); });
    }
    private static bool ValidKey(HistoricalArtifactKey? key, ProjectId expected) => key is not null && key.RecordId != Guid.Empty && key.ProjectId == expected && expected.Value != Guid.Empty && CompilationCanonical.ValidHash(key.ContentDigest) && CompilationCanonical.ValidHash(key.StorageInventoryDigest);
    private static int Find(AssetInventory inventory, AssetId id)
    { for (int i = 0; i < inventory.AssetCount; i++) if (inventory.Assets[i].AssetId == id) return i; throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt); }
    private static Hash HashBytes(byte[] bytes) => Hash.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes));
    private static HistoricalArtifactKey? Key(RuntimeAssetResult result, Operation op) => result.InstallationId is { } id ? new(id, op.Project, op.ContentDigest, op.StorageDigest) : op.KnownKey;
    private HistoricalCompileArtifact Restore(byte[] metadata, byte[] pdf, HistoricalArtifactKey key, ArtifactHistoryBudget budget)
    {
        if (metadata.Length > budget.MaxMetadataBytes || pdf.LongLength > budget.MaxPdfBytes) throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
        var value = HistoricalArtifactCodec.Decode(metadata, key.ProjectId, _format, _adapter, budget, _additionalAdapters);
        if (value.Identity.Digest != key.ContentDigest || value.Metadata.PdfLength != pdf.LongLength || value.Metadata.PdfHash != HashBytes(pdf)) throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
        return new(key, value.Identity, value.Selection, value.Files, value.Metadata.OriginalRequest, value.Metadata.OriginalSnapshot,
            value.Metadata.OriginalWorkerEpoch, value.Metadata.OriginalRequestedRevision, pdf);
    }
    private async Task<byte[]> ReadObjectAsync(Guid id, int index, DeclaredAsset asset, int maximum, Operation op)
    {
        op.Check(); var result = await _backend.ReadAsync(id, index, asset.DeclaredLength, maximum).ConfigureAwait(false); op.Check();
        if (result is null) throw new RuntimeAssetIoException(RuntimeAssetStatus.Incomplete);
        if (result.Length != asset.DeclaredLength || result.Bytes is null || result.Bytes.LongLength != result.Length || result.Bytes.LongLength > maximum) throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
        var owned = (byte[])result.Bytes.Clone(); if (HashBytes(owned) != asset.DeclaredHash) throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt); return owned;
    }
    private static async Task<VerifiedAssetInventory> WrapAsync(byte[] metadata, byte[] pdf, ArtifactHistoryBudget budget, CancellationToken token)
    {
        var input = new AssetInventoryInput(new[]
        {
            new DeclaredAssetInput { AssetId = MetadataId, RelativePath = "history/metadata.json", DeclaredLength = metadata.Length, DeclaredHash = HashBytes(metadata), Version = "artifact-storage-v1" },
            new DeclaredAssetInput { AssetId = PdfId, RelativePath = "history/output.pdf", DeclaredLength = pdf.Length, DeclaredHash = HashBytes(pdf), Version = "artifact-storage-v1" }
        });
        var inventory = AssetInventoryFactory.Create(input, budget.Storage.Inventory).Inventory ?? throw new RuntimeAssetIoException(RuntimeAssetStatus.QuotaExceeded);
        var verified = await new AssetInventoryVerifier().VerifyAsync(inventory, new BufferReader(metadata, pdf), budget.Storage.Verification, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); return (verified as AssetVerificationResult.Verified)?.Inventory ?? throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
    }
    private async Task<RevisionFileAssociation> AssociateAsync(ImmutableArray<ProjectFile> files, ProjectId project, RevisionId? explicitRevision, ArtifactHistoryBudget budget, Operation op)
    {
        if (explicitRevision is null) return new(RevisionFileAssociationStatus.Unassociated, null);
        try
        {
            op.Check(); var result = await _repository.ReadRevisionAsync(project, explicitRevision.Value, CancellationToken.None).ConfigureAwait(false); op.Check();
            if (result is null || !result.Verified) return new(RevisionFileAssociationStatus.MissingOrCorrupt, explicitRevision);
            var revision = result.Revision;
            if (revision.ProjectId != project || revision.Id != explicitRevision.Value) return new(RevisionFileAssociationStatus.WrongProject, explicitRevision);
            if (revision.Files.IsDefault || revision.Files.Length > budget.Input.MaxFiles || revision.Files.Select(f => f.Id).Distinct().Count() != revision.Files.Length) return new(RevisionFileAssociationStatus.MissingOrCorrupt, explicitRevision);
            var ordered = revision.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ThenBy(f => f.Id.ToString(), StringComparer.Ordinal);
            return new(files.SequenceEqual(ordered) ? RevisionFileAssociationStatus.VerifiedFileManifest : RevisionFileAssociationStatus.Mismatch, explicitRevision);
        }
        catch (OperationCanceledException) when (op.Token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(RevisionFileAssociationStatus.Unavailable, explicitRevision); }
    }
    private Task<ArtifactHistoryResult> RunAsync(ArtifactHistoryBudget budget, CancellationToken caller, Func<Operation, Task<ArtifactHistoryResult>> body)
    {
        TaskCompletionSource<ArtifactHistoryResult> completion; CancellationTokenSource deadline, linked;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(new ArtifactHistoryResult(RuntimeAssetStatus.Disposed));
            if (_active is { IsCompleted: false }) return Task.FromResult(new ArtifactHistoryResult(RuntimeAssetStatus.Busy));
            if (caller.IsCancellationRequested) return Task.FromResult(new ArtifactHistoryResult(RuntimeAssetStatus.Cancelled));
            deadline = new(); deadline.CancelAfter(budget.Storage.DeadlineMs); linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _shutdown.Token, deadline.Token);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = completion.Task;
        }
        var op = new Operation(linked.Token); var wait = WaitAsync(); Observe(ExecuteAsync()); return wait;
        async Task<ArtifactHistoryResult> Interrupted()
        {
            if (op.StoragePublic is { } started)
            {
                try { var result = await started.ConfigureAwait(false); var status = result.Status is RuntimeAssetStatus.Cancelled or RuntimeAssetStatus.TimedOut or RuntimeAssetStatus.Disposed ? LocalInterruptedStatus() : result.Status; return new(status, Key(result, op), PublicationUncertain: result.PublishMayHaveChanged, Cause: result.Cause); }
                catch (Exception error) when (error is not OutOfMemoryException) { return new(RuntimeAssetStatus.Uncertain, op.KnownKey, PublicationUncertain: true); }
            }
            return new(LocalInterruptedStatus(), op.KnownKey);
        }
        RuntimeAssetStatus LocalInterruptedStatus() => Volatile.Read(ref _disposed) ? RuntimeAssetStatus.Disposed : caller.IsCancellationRequested ? RuntimeAssetStatus.Cancelled : RuntimeAssetStatus.TimedOut;
        async Task<ArtifactHistoryResult> WaitAsync()
        { try { return await completion.Task.WaitAsync(op.Token).ConfigureAwait(false); } catch (OperationCanceledException) when (op.Token.IsCancellationRequested) { return await Interrupted().ConfigureAwait(false); } }
        async Task ExecuteAsync()
        {
            ArtifactHistoryResult result;
            try { result = await body(op).ConfigureAwait(false); }
            catch (OperationCanceledException) when (op.Token.IsCancellationRequested) { result = await Interrupted().ConfigureAwait(false); }
            catch (RuntimeAssetIoException error) { result = new(error.Status, op.KnownKey, Cause: error.Status); }
            catch (Exception error) when (error is not OutOfMemoryException) { result = new(error is JsonException or CompileArtifactException or CompilationInputException or ArgumentException or InvalidOperationException or OverflowException ? RuntimeAssetStatus.Corrupt : RuntimeAssetStatus.StorageFailure, op.KnownKey); }
            finally { await _content.WhenSettled.ConfigureAwait(false); linked.Dispose(); deadline.Dispose(); }
            lock (_gate) { if (!_disposed) { completion.TrySetResult(result); return; } }
            try { await CloseAsync().ConfigureAwait(false); } finally { completion.TrySetResult(result); }
        }
    }
    public Task WhenSettled { get { lock (_gate) return _active is { IsCompleted: false } ? _active : _closing ?? _active ?? Task.CompletedTask; } }
    public ValueTask DisposeAsync()
    {
        Task? active; lock (_gate) { if (_disposed) return ValueTask.CompletedTask; _disposed = true; active = _active; }
        _shutdown.Cancel(); _shutdown.Dispose(); _ = _content.DisposeAsync(); if (active is null || active.IsCompleted) Observe(CloseAsync()); return ValueTask.CompletedTask;
    }
    private Task CloseAsync() { lock (_gate) return _closing ??= CloseActualAsync(); }
    private async Task CloseActualAsync() { await _content.WhenSettled.ConfigureAwait(false); await _backend.DisposeAsync().ConfigureAwait(false); }
    private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private sealed class Operation(CancellationToken token)
    { public CancellationToken Token { get; } = token; public HistoricalArtifactKey? KnownKey; public ProjectId Project; public Hash ContentDigest, StorageDigest; public Task<RuntimeAssetResult>? StoragePublic; public void Check() => Token.ThrowIfCancellationRequested(); }
    private sealed class BufferReader(byte[] metadata, byte[] pdf) : IAssetByteReader
    { public Task<byte[]?> ReadAsync(DeclaredAsset asset, CancellationToken token) => Task.FromResult<byte[]?>(asset.AssetId == MetadataId ? metadata : pdf); }
    private sealed class BorrowedBackend(IRuntimeAssetBackend inner) : IRuntimeAssetBackend
    {
        public Task<IRuntimeAssetLease?> TryAcquireAsync() => inner.TryAcquireAsync(); public Task CreateStageAsync(IRuntimeAssetLease lease, Guid id, int max) => inner.CreateStageAsync(lease, id, max);
        public Task WriteAsync(IRuntimeAssetLease lease, Guid id, int i, byte[] bytes) => inner.WriteAsync(lease, id, i, bytes);
        public Task<StoredAssetBytes?> ReadAsync(Guid id, int i, long length, long max) => inner.ReadAsync(id, i, length, max); public Task<string?> CatalogAsync(Guid id, int max) => inner.CatalogAsync(id, max);
        public Task<bool> StageExistsAsync(Guid id) => inner.StageExistsAsync(id); public Task PublishAsync(IRuntimeAssetLease lease, Guid id, string metadata, int max) => inner.PublishAsync(lease, id, metadata, max);
        public Task<bool> DiscardAsync(IRuntimeAssetLease lease, Guid id) => inner.DiscardAsync(lease, id); public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
