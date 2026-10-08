using Microsoft.JSInterop;
using System.Threading;

namespace Client.Workspace;

/// <summary>Web Locks implementation over the minimal browser-API glue module
/// <c>workspace-storage.js</c>. Holds locks for the application lifetime and
/// releases them on dispose.</summary>
public sealed class JsWorkspaceLocks : IWorkspaceLocks
{
    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;
    private readonly Dictionary<string, string> _held = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsWorkspaceLocks(IJSRuntime js) => _js = js;

    private async Task<IJSObjectReference> ModuleAsync(CancellationToken ct)
    {
        if (_module is not null) return _module;
        _module = await _js.InvokeAsync<IJSObjectReference>("import", "./js/workspace-storage.js");
        return _module;
    }

    public async Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        var c = await m.InvokeAsync<CapabilitiesDto>("capabilities");
        return new(c.Opfs, c.IndexedDB, c.WebLocks, c.Persist);
    }

    public async Task<string?> TryAcquireAsync(string name, CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        var id = await m.InvokeAsync<string?>("tryAcquireLock", name);
        if (id is not null)
        {
            await _gate.WaitAsync(ct);
            try { _held[id] = name; } finally { _gate.Release(); }
        }
        return id;
    }

    public async Task<string> WaitForAsync(string name, CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        var id = await m.InvokeAsync<string>("waitForLock", name);
        await _gate.WaitAsync(ct);
        try { _held[id] = name; } finally { _gate.Release(); }
        return id;
    }

    public async Task<bool> ReleaseAsync(string lockId, CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        await _gate.WaitAsync(ct);
        try { _held.Remove(lockId); } finally { _gate.Release(); }
        return await m.InvokeAsync<bool>("releaseLock", lockId);
    }

    public async Task<bool> IsHeldAsync(string name, CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        return await m.InvokeAsync<bool>("lockHeld", name);
    }

    public bool IsOwnedLocally(string lockId, string name)
        => _held.TryGetValue(lockId, out var heldName) && heldName == name;

    public async Task<bool> OwnsAsync(string lockId, string name, CancellationToken ct)
    {
        if (!IsOwnedLocally(lockId, name)) return false;
        var m = await ModuleAsync(ct);
        var owned = await m.InvokeAsync<bool>("ownsLock", ct, lockId, name);
        return owned && IsOwnedLocally(lockId, name);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _held.Keys.ToArray())
        {
            try { if (_module is not null) await _module.InvokeAsync<bool>("releaseLock", id); }
            catch { /* releasing after module teardown is best-effort */ }
        }
        _held.Clear();
        if (_module is not null) await _module.DisposeAsync();
    }

    private sealed class CapabilitiesDto
    {
        public bool Opfs { get; set; }
        public bool IndexedDB { get; set; }
        public bool WebLocks { get; set; }
        public bool Persist { get; set; }
    }
}
