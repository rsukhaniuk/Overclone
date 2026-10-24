using Client.Compilation.Assets;
using Client.Pdf;
using Client.RuntimeAssets;

namespace Client.Compilation.Provisioning;

public interface IPdfRuntimeSelectionStore
{
    Task<string?> ReadAsync();
    Task WriteAsync(string metadata);
}
public enum PdfRuntimeSetupState { Empty, Preparing, Finishing, Installed, InvalidSelection, Cancelled, TimedOut, Busy, Missing, Corrupt, Unavailable, QuotaExceeded, PermissionDenied, StorageFailure, Uncertain }

/// <summary>Byte-only PDF resource installation. Never supplies an active inspector/provider to app composition.</summary>
public sealed class PdfRuntimeSetup(RuntimeAssetStore store, IPdfRuntimeSelectionStore selections) : IAsyncDisposable
{
    private CancellationTokenSource? _cancel;
    private volatile bool _disposed;
    private int _admitted;
    private bool _userCancelled;
    public bool Busy => Volatile.Read(ref _admitted) != 0;
    public bool CanCancel { get; private set; }
    public PdfRuntimeSetupState State { get; private set; }
    public int Completed { get; private set; }
    public event Action? Changed;
    public void Cancel() { if (CanCancel) { _userCancelled = true; _cancel?.Cancel(); Set(PdfRuntimeSetupState.Finishing); } }
    private void Set(PdfRuntimeSetupState state) { State = state; if (!_disposed) Changed?.Invoke(); }
    private bool Begin()
    {
        if (_disposed || Interlocked.CompareExchange(ref _admitted, 1, 0) != 0) return false;
        _cancel = new(); _cancel.CancelAfter(PdfRuntimeSelection.PreparationDeadlineMs);
        _userCancelled = false; CanCancel = true; Completed = 0; Set(PdfRuntimeSetupState.Preparing); return true;
    }
    private PdfRuntimeSetupState Interrupted => _userCancelled || _disposed ? PdfRuntimeSetupState.Cancelled : PdfRuntimeSetupState.TimedOut;
    public async Task InstallAsync(byte[] manifest, IReadOnlyList<LocalRuntimeFile> files)
    {
        if (!Begin()) return;
        TrackedReader? reader = null;
        try
        {
            var token = _cancel!.Token;
            var resources = PdfRuntimeSelection.ParseManifest(manifest); // complete schema/size validation BEFORE selected resource IO
            if (files.Count != 2) { Set(PdfRuntimeSetupState.InvalidSelection); return; }
            var owned = files.ToArray(); var byName = new Dictionary<string, LocalRuntimeFile>(StringComparer.Ordinal);
            foreach (var file in owned)
                if (file is null || file.Name is null || file.Read is null || !byName.TryAdd(file.Name, file))
                { Set(PdfRuntimeSetupState.InvalidSelection); return; }
            foreach (var resource in resources)
                if (!byName.TryGetValue(resource.Name, out var file) || file.Length != resource.Length)
                { Set(PdfRuntimeSetupState.InvalidSelection); return; }
            var inventory = PdfRuntimeSelection.Inventory(resources);
            token.ThrowIfCancellationRequested();
            reader = new(resources, byName, () => { Completed++; Set(PdfRuntimeSetupState.Preparing); });
            var verified = await new AssetInventoryVerifier().VerifyAsync(inventory, reader, PdfRuntimeSelection.Budget.Verification, token);
            if (verified is not AssetVerificationResult.Verified receipt)
            { Set(token.IsCancellationRequested ? Interrupted : VerificationFailure(verified)); return; }
            token.ThrowIfCancellationRequested();
            var indices = inventory.Assets.Select((a, i) => (a.AssetId, Index: i)).ToDictionary(a => a.AssetId, a => a.Index);
            var loader = resources.Single(r => r.Role == "loader"); var wasm = resources.Single(r => r.Role == "wasm");
            var package = new PdfRuntimePackage(receipt.Inventory.ToArray(indices[loader.AssetId]), receipt.Inventory.ToArray(indices[wasm.AssetId]), loader.Sha256, wasm.Sha256);
            if (!package.IsValid) { Set(PdfRuntimeSetupState.Corrupt); return; }
            token.ThrowIfCancellationRequested();
            // From here content publication and active-record commit are explicitly uncancellable.
            CanCancel = false; _cancel.CancelAfter(Timeout.Infinite); Set(PdfRuntimeSetupState.Finishing);
            var installed = await store.InstallAsync(receipt.Inventory, PdfRuntimeSelection.Budget);
            await store.WhenSettled;
            if (installed.Status != RuntimeAssetStatus.Installed || installed.InstallationId is not { } id)
            { Set(Map(installed.Status)); return; }
            var selection = new PdfResourceInstallation(id, inventory.Identity.Digest.Hex, resources);
            var fresh = await new InstalledPdfRuntimeProvider(store, selection.TechnicalMapping(), PdfRuntimeSelection.Budget).ReadPackageAsync();
            await store.WhenSettled;
            if (fresh.Package is null) { Set(Map(fresh.Store.Status)); return; }
            try { await selections.WriteAsync(PdfRuntimeSelection.Encode(selection)); }
            catch (Exception error) when (error is not OutOfMemoryException) { Set(PdfRuntimeSetupState.Uncertain); return; }
            Set(PdfRuntimeSetupState.Installed);
        }
        catch (OperationCanceledException) { Set(Interrupted); }
        catch (RuntimeAssetIoException error) { Set(Map(error.Status)); }
        catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or InvalidOperationException or OverflowException or ArgumentException)
        { Set(PdfRuntimeSetupState.InvalidSelection); }
        catch (Exception error) when (error is not OutOfMemoryException) { Set(PdfRuntimeSetupState.StorageFailure); }
        finally
        {
            if (reader is not null)
            {
                if (!reader.Settled) Set(PdfRuntimeSetupState.Finishing);
                await reader.DrainAsync(); // Includes completed-faulted tasks abandoned by verifier's cancellable wait.
                if (CanCancel && _cancel!.IsCancellationRequested) Set(Interrupted);
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
            if (text is null) { Set(PdfRuntimeSetupState.Empty); return; }
            var selection = PdfRuntimeSelection.Decode(text); // strict canonical reconstruction BEFORE runtime IO
            var result = await new InstalledPdfRuntimeProvider(store, selection.TechnicalMapping(), PdfRuntimeSelection.Budget).ReadPackageAsync(_cancel.Token);
            if (!store.WhenSettled.IsCompleted) Set(PdfRuntimeSetupState.Finishing);
            await store.WhenSettled;
            Set(_cancel.IsCancellationRequested ? Interrupted : result.Package is not null ? PdfRuntimeSetupState.Installed : Map(result.Store.Status));
        }
        catch (OperationCanceledException) { Set(Interrupted); }
        catch (RuntimeAssetIoException error) { Set(Map(error.Status)); }
        catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or InvalidOperationException or OverflowException or ArgumentException)
        { Set(PdfRuntimeSetupState.InvalidSelection); }
        catch (Exception error) when (error is not OutOfMemoryException) { Set(PdfRuntimeSetupState.StorageFailure); }
        finally { await store.WhenSettled; End(); }
    }
    private void End()
    {
        CanCancel = false; _cancel?.Dispose(); _cancel = null; Volatile.Write(ref _admitted, 0);
        if (!_disposed) Changed?.Invoke();
    }
    private static PdfRuntimeSetupState VerificationFailure(AssetVerificationResult result) => result switch
    {
        AssetVerificationResult.Unavailable { Issue: AssetInventoryIssue.CorruptBytes } => PdfRuntimeSetupState.Corrupt,
        AssetVerificationResult.Unavailable { Issue: AssetInventoryIssue.UnavailableAsset } => PdfRuntimeSetupState.Missing,
        AssetVerificationResult.QuotaExceeded => PdfRuntimeSetupState.QuotaExceeded,
        _ => PdfRuntimeSetupState.StorageFailure
    };
    private static PdfRuntimeSetupState Map(RuntimeAssetStatus status) => status switch
    {
        RuntimeAssetStatus.Missing or RuntimeAssetStatus.Incomplete => PdfRuntimeSetupState.Missing,
        RuntimeAssetStatus.Corrupt or RuntimeAssetStatus.InvalidInput => PdfRuntimeSetupState.Corrupt,
        RuntimeAssetStatus.Unavailable or RuntimeAssetStatus.Disposed => PdfRuntimeSetupState.Unavailable,
        RuntimeAssetStatus.QuotaExceeded => PdfRuntimeSetupState.QuotaExceeded,
        RuntimeAssetStatus.PermissionDenied => PdfRuntimeSetupState.PermissionDenied,
        RuntimeAssetStatus.Busy => PdfRuntimeSetupState.Busy,
        RuntimeAssetStatus.Cancelled => PdfRuntimeSetupState.Cancelled,
        RuntimeAssetStatus.Uncertain or RuntimeAssetStatus.TimedOut => PdfRuntimeSetupState.Uncertain,
        _ => PdfRuntimeSetupState.StorageFailure
    };
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true; Changed = null; if (CanCancel) _cancel?.Cancel();
        return store.DisposeAsync(); // prompt return; store owns late actual IO and lease cleanup
    }
    private sealed class TrackedReader : IAssetByteReader
    {
        private readonly Dictionary<AssetId, LocalRuntimeFile> _files;
        private readonly Action _progress;
        private readonly List<Task<byte[]?>> _reads = [];
        public TrackedReader(IEnumerable<PdfResourceSelection> resources, Dictionary<string, LocalRuntimeFile> files, Action progress)
        {
            _files = resources.ToDictionary(r => r.AssetId, r => files[r.Name]); _progress = progress;
        }
        public bool Settled => _reads.All(task => task.IsCompleted);
        public Task<byte[]?> ReadAsync(DeclaredAsset asset, CancellationToken token)
        { var task = Actual(asset, token); _reads.Add(task); return task; }
        private async Task<byte[]?> Actual(DeclaredAsset asset, CancellationToken token)
        { var bytes = await _files[asset.AssetId].Read(token); token.ThrowIfCancellationRequested(); _progress(); return bytes; }
        public async Task DrainAsync() { try { await Task.WhenAll(_reads); } catch (Exception error) when (error is not OutOfMemoryException) { } }
    }
}
