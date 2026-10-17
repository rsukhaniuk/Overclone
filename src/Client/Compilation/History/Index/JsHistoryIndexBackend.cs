using System.Globalization;
using System.Text.Json.Serialization;
using Client.Workspace.Domain;
using Microsoft.JSInterop;

namespace Client.Compilation.History.Index;

/// <summary>Browser IO only. The owning service retains actual operations/import
/// until settlement; disposal is deferred until those operations are finished.</summary>
public sealed class JsHistoryIndexBackend(IJSRuntime js) : IHistoryIndexBackend
{
    private Task<IJSObjectReference>? _opening;
    private readonly object _gate = new();
    private bool _disposed;
    private Task<IJSObjectReference> Port()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_opening is null || _opening.IsFaulted || _opening.IsCanceled) _opening = Open();
            return _opening;
        }
    }
    private async Task<IJSObjectReference> Open()
    {
        var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/compile-history-index.js").ConfigureAwait(false);
        try { return new Owned(module, await module.InvokeAsync<IJSObjectReference>("createHistoryIndexStorage").ConfigureAwait(false)); }
        catch { await module.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async Task<HistoryIndexRead> ReadAsync(ProjectId project, int maxJsonBytes)
    {
        var reply = await Invoke("read", project.Value.ToString("N"), maxJsonBytes).ConfigureAwait(false);
        if (reply.Status != "ok") throw new HistoryIndexException(Failure(reply.Status));
        return new(reply.Revision ?? "", reply.Json);
    }
    public async Task<HistoryIndexCas> CompareExchangeAsync(ProjectId project, long expectedRevision, long nextRevision, string json, int maxJsonBytes)
    {
        var reply = await Invoke("compareExchange", project.Value.ToString("N"), expectedRevision.ToString(CultureInfo.InvariantCulture),
            nextRevision.ToString(CultureInfo.InvariantCulture), json, maxJsonBytes).ConfigureAwait(false);
        if (reply.Status is "saved") return new(HistoryIndexStatus.Saved);
        if (reply.Status is "conflict") return new(HistoryIndexStatus.Conflict);
        if (reply.Status is "corrupt" or "schema" or "unavailable" or "permission" or "quota" or "failure") return new(Failure(reply.Status));
        throw new HistoryIndexException(HistoryIndexStatus.StorageFailure); // Unknown acknowledgement may follow commit.
    }
    private async Task<Wire> Invoke(string method, params object?[] args)
    {
        // No cancellable JS wait masquerades as actual IO settlement.
        return await (await Port().ConfigureAwait(false)).InvokeAsync<Wire>(method, args).ConfigureAwait(false);
    }
    private static HistoryIndexStatus Failure(string status) => status switch
    {
        "corrupt" => HistoryIndexStatus.Corrupt, "schema" => HistoryIndexStatus.UnsupportedSchema, "unavailable" => HistoryIndexStatus.Unavailable,
        "permission" => HistoryIndexStatus.PermissionDenied, "quota" => HistoryIndexStatus.QuotaExceeded,
        _ => HistoryIndexStatus.StorageFailure
    };
    public async ValueTask DisposeAsync()
    {
        Task<IJSObjectReference>? opening; lock (_gate) { if (_disposed) return; _disposed = true; opening = _opening; }
        if (opening is null) return;
        IJSObjectReference port;
        try { port = await opening.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { return; }
        try { await port.InvokeVoidAsync("dispose").ConfigureAwait(false); }
        finally { await port.DisposeAsync().ConfigureAwait(false); }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed class Wire
    {
        public string Status { get; set; } = "";
        public string? Revision { get; set; }
        public string? Json { get; set; }
    }
    private sealed class Owned(IJSObjectReference module, IJSObjectReference port) : IJSObjectReference
    {
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => port.InvokeAsync<T>(name, args);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args) => port.InvokeAsync<T>(name, ct, args);
        public async ValueTask DisposeAsync() { try { await port.DisposeAsync().ConfigureAwait(false); } finally { await module.DisposeAsync().ConfigureAwait(false); } }
    }
}
