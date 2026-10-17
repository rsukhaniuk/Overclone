using Client.Compilation.Assets;
using Client.RuntimeAssets;
using Client.Compilation.History;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.CompileArtifactHistory;

public sealed class ArtifactStorageBackendTests
{
    private static RuntimeAssetBudget Budget => new(new(4, 256, 8388608, 16777216, 16384, 256, 256), 32768, 8, 8, 30000);
    [Fact]
    public async Task Late_actual_import_retains_admission_and_releases_both_refs_after_dispose()
    {
        var js = new Runtime(); var store = new RuntimeAssetStore(new ArtifactStorageBackend(js)); using var caller = new CancellationTokenSource();
        var first = store.ReadAsync(Guid.NewGuid(), new string('0', 64), Budget, caller.Token);
        try
        {
            await js.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); caller.Cancel();
            Assert.Equal(RuntimeAssetStatus.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status);
            Assert.False(js.Import.Task.IsCompleted); Assert.False(store.WhenSettled.IsCompleted);
            Assert.Equal(RuntimeAssetStatus.Busy, (await store.ReadAsync(Guid.NewGuid(), new string('0', 64), Budget)).Status); Assert.Equal(1, js.Imports);
            await store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)); Assert.False(js.Import.Task.IsCompleted);
        }
        finally { js.Import.TrySetResult(js.Module); await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(3)); await store.DisposeAsync(); }
        Assert.Equal(1, js.Module.Releases); Assert.Equal(1, js.Module.Port.Releases); Assert.Equal(1, js.Module.Port.Closes);
        Assert.Equal(RuntimeAssetStatus.Disposed, (await store.ReadAsync(Guid.NewGuid(), new string('0', 64), Budget)).Status); Assert.Equal(1, js.Imports);
    }
    [Fact]
    public async Task Foreign_import_cancellation_is_failure_with_unexpired_deadline()
    {
        var js = new Runtime(); js.Import.TrySetCanceled(new CancellationToken(true)); await using var store = new RuntimeAssetStore(new ArtifactStorageBackend(js));
        var result = await store.ReadAsync(Guid.NewGuid(), new string('0', 64), Budget).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(RuntimeAssetStatus.StorageFailure, result.Status); Assert.Equal(1, js.Imports);
    }
    private sealed class Runtime : IJSRuntime
    {
        public readonly TaskCompletionSource<IJSObjectReference> Import = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Module Module = new(); public int Imports;
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) { Assert.Equal("import", id); Assert.Equal("./js/compile-artifacts-storage.js", args![0]); Imports++; Entered.TrySetResult(true); return new(Convert<T>()); }
        private async Task<T> Convert<T>() => (T)(object)await Import.Task;
    }
    private sealed class Module : IJSObjectReference
    {
        public int Releases; public readonly Port Port = new();
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult((T)(object)Port);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
        public ValueTask DisposeAsync() { Releases++; return ValueTask.CompletedTask; }
    }
    private sealed class Port : IJSObjectReference
    {
        public int Releases, Closes;
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args)
        {
            if (id == "dispose") { Closes++; return ValueTask.FromResult(default(T)!); }
            if (id == "catalog") return ValueTask.FromResult((T)(object)new ArtifactStorageBackend.Wire { Status = "ok" });
            throw new InvalidOperationException("Unexpected TEST IO.");
        }
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
        public ValueTask DisposeAsync() { Releases++; return ValueTask.CompletedTask; }
    }
}
