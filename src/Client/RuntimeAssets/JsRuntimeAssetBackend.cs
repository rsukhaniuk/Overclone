using Microsoft.JSInterop;
using System.Text.Json.Serialization;

namespace Client.RuntimeAssets;

// Minimal JSInterop browser IO. No inventory validation/canonical identity here.
public sealed class JsRuntimeAssetBackend(IJSRuntime js) : IRuntimeAssetBackend
{
    private readonly object _gate = new();
    private Task<IJSObjectReference>? _opening;
    private bool _disposed;
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private Task<IJSObjectReference> PortAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_opening is null || _opening.IsFaulted || _opening.IsCanceled) _opening = OpenAsync();
            return _opening;
        }
    }
    private async Task<IJSObjectReference> OpenAsync()
    {
        var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/runtime-assets-storage.js").ConfigureAwait(false);
        try
        {
            var port = await module.InvokeAsync<IJSObjectReference>("createAssetStorage", _owner).ConfigureAwait(false);
            return new OwnedPort(module, port);
        }
        catch { await module.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async Task<IRuntimeAssetLease?> TryAcquireAsync()
    {
        try
        {
            var port = await PortAsync().ConfigureAwait(false); var reference = await port.InvokeAsync<IJSObjectReference?>("tryAcquire").ConfigureAwait(false);
            return reference is null ? null : new Lease(this, reference);
        }
        catch (JSException error)
        {
            var status = error.Message.Contains("ASSET_UNSUPPORTED", StringComparison.Ordinal) ? RuntimeAssetStatus.Unavailable
                : error.Message.Contains("ASSET_IO:permission", StringComparison.Ordinal) ? RuntimeAssetStatus.PermissionDenied
                : error.Message.Contains("ASSET_IO:quota", StringComparison.Ordinal) ? RuntimeAssetStatus.QuotaExceeded : RuntimeAssetStatus.StorageFailure;
            throw new RuntimeAssetIoException(status);
        }
    }
    public async Task CreateStageAsync(IRuntimeAssetLease lease, Guid stage, int maxStageRecords) => Check(await InvokeAsync("createStage", Reference(lease), Id(stage), maxStageRecords).ConfigureAwait(false));
    public async Task WriteAsync(IRuntimeAssetLease lease, Guid stage, int index, byte[] bytes) => Check(await InvokeAsync("writeAsset", Reference(lease), Id(stage), index, bytes).ConfigureAwait(false));
    public async Task<StoredAssetBytes?> ReadAsync(Guid stage, int index, long expectedLength, long maxBytes)
    {
        var result = await InvokeAsync("readAsset", Id(stage), index, expectedLength, maxBytes).ConfigureAwait(false);
        if (result.Status == "missing") return null; Check(result);
        if (result.Bytes is null || result.Length != expectedLength || result.Bytes.LongLength != result.Length || result.Bytes.LongLength > maxBytes)
            throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
        return new(result.Length, result.Bytes);
    }
    public async Task<string?> CatalogAsync(Guid stage, int maxMetadataBytes)
    { var result = await InvokeAsync("catalog", Id(stage), maxMetadataBytes).ConfigureAwait(false); Check(result); return result.Metadata; }
    public async Task<bool> StageExistsAsync(Guid stage)
    { var result = await InvokeAsync("exists", Id(stage)).ConfigureAwait(false); Check(result); return result.Exists; }
    public async Task PublishAsync(IRuntimeAssetLease lease, Guid stage, string metadata, int maxEntries) => Check(await InvokeAsync("publish", Reference(lease), Id(stage), metadata, maxEntries).ConfigureAwait(false));
    public async Task<bool> DiscardAsync(IRuntimeAssetLease lease, Guid stage)
    { var result = await InvokeAsync("discard", Reference(lease), Id(stage)).ConfigureAwait(false); Check(result); return result.Exists; }
    private async Task<Wire> InvokeAsync(string name, params object?[] args)
    {
        try { return await (await PortAsync().ConfigureAwait(false)).InvokeAsync<Wire>(name, args).ConfigureAwait(false); }
        catch (JSException) { throw new RuntimeAssetIoException(RuntimeAssetStatus.StorageFailure); }
    }
    private static string Id(Guid id) => id.ToString("N");
    private IJSObjectReference Reference(IRuntimeAssetLease lease) => lease is Lease owned && ReferenceEquals(owned.Owner, this) ? owned.Reference : throw new ArgumentException("Foreign asset lease.", nameof(lease));
    private static void Check(Wire result)
    {
        if (result.Status == "ok") return;
        throw new RuntimeAssetIoException(result.Status switch
        {
            "unsupported" or "unavailable" => RuntimeAssetStatus.Unavailable,
            "quota" => RuntimeAssetStatus.QuotaExceeded,
            "permission" => RuntimeAssetStatus.PermissionDenied,
            "corrupt" => RuntimeAssetStatus.Corrupt,
            "not-owned" => RuntimeAssetStatus.NotOwned,
            "missing" => RuntimeAssetStatus.Incomplete,
            _ => RuntimeAssetStatus.StorageFailure
        });
    }
    public async ValueTask DisposeAsync()
    {
        Task<IJSObjectReference>? opening; lock (_gate) { if (_disposed) return; _disposed = true; opening = _opening; }
        // Store calls backend disposal only after actual operations/late leases
        // settle. No cancellation of proxies is mistaken for completion here.
        if (opening is null) return;
        try { var port = await opening.ConfigureAwait(false); await port.InvokeVoidAsync("dispose").ConfigureAwait(false); await port.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is JSException or ObjectDisposedException or OperationCanceledException) { }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed class Wire
    {
        public string Status { get; set; } = "";
        public string? Metadata { get; set; }
        public byte[]? Bytes { get; set; }
        public long Length { get; set; }
        public bool Exists { get; set; }
    }
    private sealed class Lease(JsRuntimeAssetBackend owner, IJSObjectReference reference) : IRuntimeAssetLease
    {
        public JsRuntimeAssetBackend Owner { get; } = owner;
        public IJSObjectReference Reference { get; } = reference;
        private int _released;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            try { Check(await Reference.InvokeAsync<Wire>("release").ConfigureAwait(false)); }
            finally { await Reference.DisposeAsync().ConfigureAwait(false); }
        }
    }
    private sealed class OwnedPort(IJSObjectReference module, IJSObjectReference port) : IJSObjectReference
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => port.InvokeAsync<T>(identifier, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => port.InvokeAsync<T>(identifier, cancellationToken, args);
        public async ValueTask DisposeAsync() { try { await port.DisposeAsync().ConfigureAwait(false); } finally { await module.DisposeAsync().ConfigureAwait(false); } }
    }
}
