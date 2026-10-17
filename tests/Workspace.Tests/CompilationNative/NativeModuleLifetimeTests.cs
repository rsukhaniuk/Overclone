using Client.Compilation.Native;
using Microsoft.JSInterop;
using Xunit;
using Workspace.Tests.CompilationLifecycle;
namespace Workspace.Tests.CompilationNative;

public sealed class NativeModuleLifetimeTests
{
    private sealed class Runtime : IJSRuntime
    {
        public readonly TaskCompletionSource<IJSObjectReference> Import = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier); Assert.False(cancellationToken.CanBeCanceled); Calls++; return new(Convert<T>());
        }
        private async Task<T> Convert<T>() => (T)(object)await Import.Task;
    }
    private sealed class Module : IJSInProcessObjectReference
    {
        public readonly TaskCompletionSource Disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public T Invoke<T>(string identifier, params object?[]? args) => default!;
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(T)!);
        public void Dispose() { }
        public async ValueTask DisposeAsync() { Disposing.TrySetResult(); await Release.Task; }
    }
    [Fact]
    public void Cancelled_import_and_late_disposal_keep_actual_reservation() => LifecycleTestHost.Run(async () =>
    {
        var js = new Runtime(); var module = new Module(); var loader = new NativeBrowserModuleLoader(js); using var cancel = new CancellationTokenSource();
        try
        {
            var pending = loader.LoadAsync(cancel.Token); Assert.Equal(1, loader.Reserved); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await Assert.ThrowsAsync<NativeResourceLimitException>(() => loader.LoadAsync(default)); Assert.Equal(1, js.Calls);
            js.Import.TrySetResult(module); await module.Disposing.Task; Assert.Equal(1, loader.Reserved);
        }
        finally { js.Import.TrySetResult(module); module.Release.TrySetResult(); await loader.WhenIdle; }
        Assert.Equal(0, loader.Reserved);
    });
    [Fact]
    public void Foreign_import_OCE_is_not_relabelled_as_caller_cancel() => LifecycleTestHost.Run(async () =>
    {
        var js = new Runtime(); var loader = new NativeBrowserModuleLoader(js);
        js.Import.TrySetException(new OperationCanceledException("foreign import"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadAsync(default));
        await loader.WhenIdle; Assert.Equal(0, loader.Reserved);
    });
}
