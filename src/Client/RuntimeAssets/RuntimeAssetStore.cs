using Client.Compilation.Assets;
using System.Text.Json;

namespace Client.RuntimeAssets;

// One real operation (including actual foreign IO and lease cleanup), no queue.
// Canceling the public WAIT does not free actual admission or its Web Lock.
public sealed class RuntimeAssetStore(IRuntimeAssetBackend backend) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<Guid, string> _ownedStages = [];
    private Task<RuntimeAssetResult>? _active;
    private bool _disposed;
    private Task? _closing;

    public Task<RuntimeAssetResult> InstallAsync(VerifiedAssetInventory receipt, RuntimeAssetBudget budget, CancellationToken token = default)
    {
        if (receipt is null || budget is null || !budget.IsValid) return Task.FromResult(new RuntimeAssetResult(RuntimeAssetStatus.InvalidInput));
        return RunAsync(budget, token, async op =>
        {
            var inventory = RuntimeAssetCatalog.Revalidate(receipt.Inventory, budget);
            // Existing owned receipt plus existing factory; no new canonical digest.
            for (int i = 0; i < receipt.AssetCount; i++)
                if (receipt.ActualLength(i) != inventory.Assets[i].DeclaredLength || receipt.ActualHash(i) != inventory.Assets[i].DeclaredHash)
                    return new(RuntimeAssetStatus.Corrupt);
            var id = Guid.NewGuid(); op.Stage = id; op.Digest = inventory.Identity.Digest.Hex;
            string metadata = RuntimeAssetCatalog.Encode(id, inventory, budget.MaxCatalogBytes);
            lock (_gate)
            {
                if (_ownedStages.Count >= budget.MaxStageRecords) return new(RuntimeAssetStatus.QuotaExceeded);
                _ownedStages.Add(id, "reserved");
            }
            IRuntimeAssetLease? lease = null; bool created = false;
            try
            {
                op.Check(); lease = await backend.TryAcquireAsync().ConfigureAwait(false); op.Check();
                if (lease is null) { lock (_gate) _ownedStages.Remove(id); return new(RuntimeAssetStatus.Busy); }
                created = true; // Attempt may create a partial stage before failing.
                await backend.CreateStageAsync(lease, id, budget.MaxStageRecords).ConfigureAwait(false); op.Check();
                SetStage(id, "writing");
                for (int i = 0; i < receipt.AssetCount; i++)
                {
                    op.Check(); await backend.WriteAsync(lease, id, i, receipt.ToArray(i)).ConfigureAwait(false); op.Check();
                }
                var verified = await VerifyBytesAsync(inventory, id, budget, op).ConfigureAwait(false); op.Check();
                if (verified.Snapshot is null) return verified;
                SetStage(id, "publishing"); op.PublishStarted = true; op.Check();
                // Once started, IDB transaction may commit after public cancellation.
                await backend.PublishAsync(lease, id, metadata, budget.MaxCatalogEntries).ConfigureAwait(false);
                lock (_gate) _ownedStages.Remove(id);
                return new(RuntimeAssetStatus.Installed, id, op.Digest, verified.Snapshot);
            }
            finally
            {
                if (!created) { lock (_gate) _ownedStages.Remove(id); }
                // No cancellable proxy wait here: real lease lifetime covers IO.
                if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
            }
        });
    }
    public Task<RuntimeAssetResult> ReadAsync(Guid installationId, string expectedInventoryDigest, RuntimeAssetBudget budget, CancellationToken token = default)
    {
        if (installationId == Guid.Empty || !RuntimeAssetCatalog.HashValid(expectedInventoryDigest) || budget is null || !budget.IsValid)
            return Task.FromResult(new RuntimeAssetResult(RuntimeAssetStatus.InvalidInput));
        return RunAsync(budget, token, async op =>
        {
            op.Stage = installationId; op.Digest = expectedInventoryDigest; op.Check();
            string? metadata = await backend.CatalogAsync(installationId, budget.MaxCatalogBytes).ConfigureAwait(false); op.Check();
            if (metadata is null)
            {
                bool exists = await backend.StageExistsAsync(installationId).ConfigureAwait(false); op.Check();
                return new(exists ? RuntimeAssetStatus.Incomplete : RuntimeAssetStatus.Missing, installationId, expectedInventoryDigest);
            }
            AssetInventory inventory;
            try { inventory = RuntimeAssetCatalog.Decode(metadata, installationId, expectedInventoryDigest, budget); }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
            { return new(RuntimeAssetStatus.Corrupt, installationId, expectedInventoryDigest); }
            return await VerifyBytesAsync(inventory, installationId, budget, op).ConfigureAwait(false);
        });
    }
    public Task<RuntimeAssetResult> DiscardAsync(Guid stage, RuntimeAssetBudget budget, CancellationToken token = default)
    {
        if (stage == Guid.Empty || budget is null || !budget.IsValid) return Task.FromResult(new RuntimeAssetResult(RuntimeAssetStatus.InvalidInput));
        return RunAsync(budget, token, async op =>
        {
            op.Stage = stage;
            lock (_gate) if (!_ownedStages.ContainsKey(stage)) return new(RuntimeAssetStatus.NotOwned, stage);
            op.Check(); var lease = await backend.TryAcquireAsync().ConfigureAwait(false);
            try
            {
                op.Check(); if (lease is null) return new(RuntimeAssetStatus.Busy, stage);
                bool discarded = await backend.DiscardAsync(lease, stage).ConfigureAwait(false);
                lock (_gate) _ownedStages.Remove(stage);
                if (!discarded) return new(RuntimeAssetStatus.NotOwned, stage);
                return new(RuntimeAssetStatus.Discarded, stage);
            }
            finally { if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); }
        });
    }
    private void SetStage(Guid id, string state) { lock (_gate) if (_ownedStages.ContainsKey(id)) _ownedStages[id] = state; }
    private async Task<RuntimeAssetResult> VerifyBytesAsync(AssetInventory inventory, Guid id, RuntimeAssetBudget budget, Operation op)
    {
        var reader = new TrackedReader(backend, id, inventory, budget); AssetVerificationResult result;
        try { result = await new AssetInventoryVerifier().VerifyAsync(inventory, reader, budget.Verification, op.Token).ConfigureAwait(false); }
        finally { await reader.DrainAsync().ConfigureAwait(false); }
        op.Check();
        if (result is AssetVerificationResult.Verified verified) return new(RuntimeAssetStatus.Ready, id, inventory.Identity.Digest.Hex, verified.Inventory);
        if (reader.Failure is { } failure) return new(failure, id, inventory.Identity.Digest.Hex);
        return new(result is AssetVerificationResult.QuotaExceeded ? RuntimeAssetStatus.QuotaExceeded : RuntimeAssetStatus.Corrupt, id, inventory.Identity.Digest.Hex);
    }
    private Task<RuntimeAssetResult> RunAsync(RuntimeAssetBudget budget, CancellationToken caller, Func<Operation, Task<RuntimeAssetResult>> body)
    {
        TaskCompletionSource<RuntimeAssetResult> completion; CancellationTokenSource deadline, linked;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(new RuntimeAssetResult(RuntimeAssetStatus.Disposed));
            if (_active is { IsCompleted: false }) return Task.FromResult(new RuntimeAssetResult(RuntimeAssetStatus.Busy));
            if (caller.IsCancellationRequested) return Task.FromResult(new RuntimeAssetResult(RuntimeAssetStatus.Cancelled));
            deadline = new(); deadline.CancelAfter(budget.DeadlineMs);
            linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _shutdown.Token, deadline.Token);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = completion.Task;
        }
        var op = new Operation(linked.Token); var waiting = WaitAsync(); var actual = ExecuteAsync(); Observe(actual); return waiting;
        async Task ExecuteAsync()
        {
            RuntimeAssetResult result;
            try { result = await body(op).ConfigureAwait(false); }
            catch (OperationCanceledException) when (op.Token.IsCancellationRequested) { result = Interrupted(); }
            catch (RuntimeAssetIoException error) { result = new(op.PublishStarted ? RuntimeAssetStatus.Uncertain : error.Status, op.Stage, op.Digest, PublishMayHaveChanged: op.PublishStarted, Cause: error.Status); }
            catch (Exception error) when (error is not OutOfMemoryException) { result = new(op.PublishStarted ? RuntimeAssetStatus.Uncertain : RuntimeAssetStatus.StorageFailure, op.Stage, op.Digest, PublishMayHaveChanged: op.PublishStarted, Cause: RuntimeAssetStatus.StorageFailure); }
            finally { linked.Dispose(); deadline.Dispose(); }
            lock (_gate)
            {
                if (!_disposed) { completion.TrySetResult(result); return; }
            }
            // Handshake with Dispose: either it observes a completed operation
            // and closes, or this core owns closing BEFORE actual settlement.
            try { await CloseBackendAsync().ConfigureAwait(false); }
            finally { completion.TrySetResult(result); }
        }
        RuntimeAssetResult Interrupted() => new(op.PublishStarted ? RuntimeAssetStatus.Uncertain : Volatile.Read(ref _disposed) ? RuntimeAssetStatus.Disposed
            : caller.IsCancellationRequested ? RuntimeAssetStatus.Cancelled : RuntimeAssetStatus.TimedOut, op.Stage, op.Digest, PublishMayHaveChanged: op.PublishStarted);
        async Task<RuntimeAssetResult> WaitAsync()
        {
            try { return await completion.Task.WaitAsync(op.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (op.Token.IsCancellationRequested) { return Interrupted(); }
        }
    }
    /// <summary>Actual owned settlement observation; may remain pending on foreign
    /// noncooperation. NOT a prompt-idle guarantee and never awaited by Dispose.</summary>
    public Task WhenSettled { get { lock (_gate) return _active is { IsCompleted: false } ? _active : _closing ?? _active ?? Task.CompletedTask; } }
    public ValueTask DisposeAsync()
    {
        Task<RuntimeAssetResult>? active;
        lock (_gate) { if (_disposed) return ValueTask.CompletedTask; _disposed = true; active = _active; }
        _shutdown.Cancel(); _shutdown.Dispose();
        if (active is null || active.IsCompleted) Observe(CloseBackendAsync());
        // Actual active operation owns late IO/lease cleanup. No foreign join.
        return ValueTask.CompletedTask;
    }
    private Task CloseBackendAsync() { lock (_gate) return _closing ??= CloseActualAsync(); }
    private async Task CloseActualAsync() => await backend.DisposeAsync().ConfigureAwait(false);
    private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private sealed class Operation(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public Guid? Stage; public string? Digest; public volatile bool PublishStarted;
        public void Check() => Token.ThrowIfCancellationRequested();
    }
    private sealed class TrackedReader(IRuntimeAssetBackend backend, Guid id, AssetInventory inventory, RuntimeAssetBudget budget) : IAssetByteReader
    {
        private readonly List<Task<byte[]?>> _actual = [];
        public RuntimeAssetStatus? Failure;
        public Task<byte[]?> ReadAsync(DeclaredAsset asset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); int index = inventory.Assets.IndexOf(asset);
            var task = ReadActualAsync(index, asset); _actual.Add(task); Observe(task); return task;
        }
        private async Task<byte[]?> ReadActualAsync(int index, DeclaredAsset asset)
        {
            try
            {
                var read = await backend.ReadAsync(id, index, asset.DeclaredLength, budget.Inventory.MaxAssetBytes).ConfigureAwait(false);
                if (read is null) { Failure = RuntimeAssetStatus.Incomplete; return null; }
                if (read.Bytes is null || read.Length != asset.DeclaredLength || read.Bytes.LongLength != read.Length) { Failure = RuntimeAssetStatus.Corrupt; return null; }
                if (read.Bytes.LongLength > budget.Inventory.MaxAssetBytes) { Failure = RuntimeAssetStatus.QuotaExceeded; return null; }
                return read.Bytes;
            }
            catch (RuntimeAssetIoException error) { Failure = error.Status; throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { Failure = RuntimeAssetStatus.StorageFailure; throw; }
        }
        public async Task DrainAsync()
        {
            // Existing verifier can abandon its WAIT. Track and drain the real
            // reader tasks in the owned core, retaining actual admission/lease.
            try { await Task.WhenAll(_actual).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }
}
