using Client.Compilation.Assets;
using Client.Compilation.Native;
using Client.RuntimeAssets;

namespace Client.Compilation.Provisioning;

public interface ILocalRuntimeSelectionStore
{
    Task<string?> ReadAsync();
    Task WriteAsync(string metadata);
}
public sealed record LocalRuntimeFile(string Name, long Length, Func<CancellationToken, Task<byte[]>> Read);
public enum RuntimeSetupState { Empty, Preparing, Finishing, Installed, InvalidSelection, Cancelled, Busy, Missing, Corrupt, Unavailable, QuotaExceeded, PermissionDenied, StorageFailure, Uncertain }

/// <summary>Owns one local byte operation through physical settlement, not just cancellation of a wait.</summary>
public sealed class NativeRuntimeSetup(RuntimeAssetStore store, ILocalRuntimeSelectionStore selections) : IAsyncDisposable
{
    private CancellationTokenSource? _cancel;
    private bool _disposed;
    public bool Busy { get; private set; }
    public bool CanCancel { get; private set; }
    public RuntimeSetupState State { get; private set; }
    public int Completed { get; private set; }
    public event Action? Changed;
    public void Cancel() { if (CanCancel) { _cancel?.Cancel(); Set(RuntimeSetupState.Finishing); } }
    private void Set(RuntimeSetupState state) { State = state; if (!_disposed) Changed?.Invoke(); }
    private bool Begin()
    {
        if (_disposed || Busy) return false;
        Busy = true; CanCancel = true; Completed = 0; _cancel = new(); Set(RuntimeSetupState.Preparing); return true;
    }
    public async Task InstallAsync(byte[] pinsJson, IReadOnlyList<LocalRuntimeFile> files)
    {
        if (!Begin()) return;
        TrackedReader? reader = null;
        try
        {
            var token = _cancel!.Token;
            // Own all caller descriptors before any resource IO; pin parsing and complete closure first.
            var pins = LocalRuntimeSelection.ParsePins(pinsJson);
            var profile = LocalRuntimeSelection.ProfileFor(pins);
            var limits = LocalRuntimeSelection.LimitsFor(profile);
            var budget = LocalRuntimeSelection.BudgetFor(profile);
            if (files.Count != pins.Length || files.Count > limits.MaxFiles) { Set(RuntimeSetupState.InvalidSelection); return; }
            var owned = files.ToArray(); var byName = new Dictionary<string, LocalRuntimeFile>(StringComparer.Ordinal);
            foreach (var file in owned)
                if (file is null || file.Name is null || file.Name.Length > 200 || file.Read is null || !byName.TryAdd(file.Name, file))
                { Set(RuntimeSetupState.InvalidSelection); return; }
            foreach (var pin in pins)
                if (!byName.TryGetValue(pin.Name, out var file) || file.Length != pin.Length)
                { Set(RuntimeSetupState.InvalidSelection); return; }
            token.ThrowIfCancellationRequested();
            var resources = LocalRuntimeSelection.Resources(pins); var inventory = LocalRuntimeSelection.Inventory(resources);
            reader = new(byName, () => { Completed++; Set(RuntimeSetupState.Preparing); });
            var verification = await new AssetInventoryVerifier().VerifyAsync(inventory, reader, budget.Verification, token);
            // Verifier may abandon a noncooperating selected-file read. Retain admission until it ends.
            if (verification is not AssetVerificationResult.Verified verified)
            { Set(token.IsCancellationRequested ? RuntimeSetupState.Cancelled : RuntimeSetupState.Corrupt); return; }
            token.ThrowIfCancellationRequested();
            var indices = inventory.Assets.Select((a, i) => (a.AssetId, Index: i)).ToDictionary(a => a.AssetId, a => a.Index);
            var package = NativeRuntimePackage.Create(resources.Select(r => new NativeResourceInput(r.Pin.Name,
                verified.Inventory.ToArray(indices[r.AssetId]))).ToArray(), pins, limits, profile);
            if (package.Package is null) { Set(RuntimeSetupState.Corrupt); return; }
            token.ThrowIfCancellationRequested();
            // Explicit uncancellable publication boundary. Cancel cannot roll back runtime or selection commits.
            CanCancel = false; Set(RuntimeSetupState.Finishing);
            var installed = await store.InstallAsync(verified.Inventory, budget);
            await store.WhenSettled;
            if (installed.Status != RuntimeAssetStatus.Installed || installed.InstallationId is not { } id)
            { Set(Map(installed.Status)); return; }
            var mapping = new InstalledNativeRuntimeMapping(id, inventory.Identity.Digest.Hex, resources, profile);
            // Validate actual published bytes with a fresh provider BEFORE publishing active selection.
            var check = await new InstalledNativeRuntimeProvider(store, mapping, budget, limits).ReadPackageAsync();
            await store.WhenSettled;
            if (check.Package is null) { Set(Map(check.Store.Status)); return; }
            try { await selections.WriteAsync(LocalRuntimeSelection.Encode(mapping)); }
            catch (Exception e) when (e is not OutOfMemoryException) { Set(RuntimeSetupState.Uncertain); return; }
            Set(RuntimeSetupState.Installed);
        }
        catch (OperationCanceledException) { Set(RuntimeSetupState.Cancelled); }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException or OverflowException or ArgumentException)
        { Set(RuntimeSetupState.InvalidSelection); }
        catch (Exception e) when (e is not OutOfMemoryException) { Set(RuntimeSetupState.StorageFailure); }
        finally
        {
            if (reader is not null)
            {
                if (!reader.Settled) Set(RuntimeSetupState.Finishing);
                await reader.DrainAsync(); // observe even already-faulted abandoned reads
                if (_cancel!.IsCancellationRequested) Set(RuntimeSetupState.Cancelled);
            }
            await store.WhenSettled; End();
        }
    }
    public async Task RecheckAsync()
    {
        if (!Begin()) return;
        try
        {
            var text = await selections.ReadAsync(); _cancel!.Token.ThrowIfCancellationRequested();
            if (text is null) { Set(RuntimeSetupState.Empty); return; }
            var mapping = LocalRuntimeSelection.Decode(text); // strict reconstruction before any runtime-store reads
            var result = await new InstalledNativeRuntimeProvider(store, mapping, LocalRuntimeSelection.BudgetFor(mapping.Profile), LocalRuntimeSelection.LimitsFor(mapping.Profile)).ReadPackageAsync(_cancel.Token);
            if (!store.WhenSettled.IsCompleted) Set(RuntimeSetupState.Finishing);
            await store.WhenSettled;
            Set(_cancel.IsCancellationRequested ? RuntimeSetupState.Cancelled : result.Package is not null ? RuntimeSetupState.Installed : Map(result.Store.Status));
        }
        catch (OperationCanceledException) { Set(RuntimeSetupState.Cancelled); }
        catch (RuntimeAssetIoException error) { Set(Map(error.Status)); }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException or OverflowException or ArgumentException) { Set(RuntimeSetupState.InvalidSelection); }
        catch (Exception e) when (e is not OutOfMemoryException) { Set(RuntimeSetupState.StorageFailure); }
        finally { await store.WhenSettled; End(); }
    }
    private void End() { CanCancel = false; Busy = false; _cancel?.Dispose(); _cancel = null; if (!_disposed) Changed?.Invoke(); }
    private static RuntimeSetupState Map(RuntimeAssetStatus status) => status switch
    {
        RuntimeAssetStatus.Missing or RuntimeAssetStatus.Incomplete => RuntimeSetupState.Missing,
        RuntimeAssetStatus.Corrupt or RuntimeAssetStatus.InvalidInput => RuntimeSetupState.Corrupt,
        RuntimeAssetStatus.Unavailable => RuntimeSetupState.Unavailable,
        RuntimeAssetStatus.QuotaExceeded => RuntimeSetupState.QuotaExceeded,
        RuntimeAssetStatus.PermissionDenied => RuntimeSetupState.PermissionDenied,
        RuntimeAssetStatus.Busy => RuntimeSetupState.Busy,
        RuntimeAssetStatus.Cancelled => RuntimeSetupState.Cancelled,
        RuntimeAssetStatus.Uncertain or RuntimeAssetStatus.TimedOut => RuntimeSetupState.Uncertain,
        _ => RuntimeSetupState.StorageFailure
    };
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true; Changed = null; if (CanCancel) _cancel?.Cancel();
        // Store promptly cancels/observes owned late IO; no joins or late UI callbacks.
        return store.DisposeAsync();
    }
    private sealed class TrackedReader(Dictionary<string, LocalRuntimeFile> files, Action progress) : IAssetByteReader
    {
        private readonly List<Task<byte[]?>> _reads = [];
        public bool Settled => _reads.All(t => t.IsCompleted);
        public Task<byte[]?> ReadAsync(DeclaredAsset asset, CancellationToken token)
        { var task = Actual(asset, token); _reads.Add(task); return task; }
        private async Task<byte[]?> Actual(DeclaredAsset asset, CancellationToken token)
        { var bytes = await files[asset.RelativePath[8..]].Read(token); token.ThrowIfCancellationRequested(); progress(); return bytes; }
        public async Task DrainAsync() { try { await Task.WhenAll(_reads); } catch (Exception e) when (e is not OutOfMemoryException) { } }
    }
}
