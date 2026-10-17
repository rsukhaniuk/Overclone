using Microsoft.JSInterop;

namespace Client.Compilation.Native;

/// <summary>Explicit opt-in import, separate from the absent-runtime default.
/// One import/module lifetime maximum; caller cancellation doesn't free that
/// reservation until the actual import AND late disposal settle.</summary>
public sealed class NativeBrowserModuleLoader(IJSRuntime js)
{
    private readonly object _gate = new();
    private int _reserved;
    private TaskCompletionSource _idle = Completed();
    private static TaskCompletionSource Completed() { var s = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); s.SetResult(); return s; }
    public int Reserved { get { lock (_gate) return _reserved; } }
    public Task WhenIdle { get { lock (_gate) return _idle.Task; } }
    private void Release() { lock (_gate) { _reserved = 0; _idle.TrySetResult(); } }
    public async Task<NativeBrowserModuleLease> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { if (_reserved != 0) throw new NativeResourceLimitException(); _reserved = 1; _idle = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        Task<IJSObjectReference> actual;
        try { actual = js.InvokeAsync<IJSObjectReference>("import", CancellationToken.None, "./js/compile-port-bridge.js").AsTask(); }
        catch { Release(); throw; }
        try
        {
            var module = await actual.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (module is not IJSInProcessObjectReference sync) throw new NotSupportedException("WASM synchronous termination capability is unavailable.");
            return new(sync, Release);
        }
        catch
        {
            _ = Retire(actual); throw;
        }
    }
    private async Task Retire(Task<IJSObjectReference> actual)
    {
        try { var module = await actual.ConfigureAwait(false); await module.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        finally { Release(); }
    }
}
public sealed class NativeBrowserModuleLease : IAsyncDisposable
{
    private readonly IJSInProcessObjectReference _module;
    private readonly Action _release;
    private readonly object _disposeGate = new();
    private Task? _disposing;
    internal NativeBrowserModuleLease(IJSInProcessObjectReference module, Action release) { _module = module; _release = release; Transport = new(module); }
    public NativeBrowserTransport Transport { get; }
    public Task<bool> ProvisionAsync() => _module.InvokeAsync<bool>("provision", CancellationToken.None).AsTask();
    public object Observations() => _module.Invoke<System.Text.Json.JsonElement>("observations");
    public ValueTask DisposeAsync() { lock (_disposeGate) return new(_disposing ??= DisposeCore()); }
    private async Task DisposeCore()
    {
        try { try { _module.InvokeVoid("dispose"); } finally { await _module.DisposeAsync().ConfigureAwait(false); } }
        finally { _release(); }
    }
}
public sealed class NativeBrowserTransport(IJSInProcessObjectReference module) : INativePhaseTransport
{
    public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest request)
        => module.InvokeAsync<NativePhaseReply>("execute", CancellationToken.None, request).AsTask();
    public void RequestTermination(string job, long epoch) => module.InvokeVoid("terminate", job, epoch);
}
