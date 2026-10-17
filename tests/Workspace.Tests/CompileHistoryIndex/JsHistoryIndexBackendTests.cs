using Client.Compilation.History;
using Client.Compilation.History.Index;
using Client.Workspace.Domain;
using Microsoft.JSInterop;
using Xunit;
using IndexService = Client.Compilation.History.Index.CompileHistoryIndex;

namespace Workspace.Tests.CompileHistoryIndex;
public sealed class JsHistoryIndexBackendTests
{
    private static readonly HistoryIndexBudget Budget = new(64, 65536, 10000);
    [Fact]
    public async Task Late_actual_import_is_admitted_until_settlement_and_disposed_exactly_once()
    {
        var js = new Runtime(); var service = new IndexService(new JsHistoryIndexBackend(js)); var project = ProjectId.New(); using var cancel = new CancellationTokenSource();
        var first = service.ListAsync(project, Budget, cancel.Token); Exception? primary = null;
        try
        {
            await js.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel(); Assert.Equal(HistoryIndexStatus.Cancelled, (await first).Status);
            Assert.False(js.Import.Task.IsCompleted); Assert.False(service.WhenSettled.IsCompleted);
            Assert.Equal(HistoryIndexStatus.Busy, (await service.ListAsync(project, Budget)).Status); Assert.Equal(1, js.Imports);
            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)); Assert.Equal(0, js.Module.Releases);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            js.Import.TrySetResult(js.Module);
            try { await service.WhenSettled.WaitAsync(TimeSpan.FromSeconds(2)); await service.DisposeAsync(); }
            catch (Exception cleanup) when (primary is not null) { throw new AggregateException(primary, cleanup); }
        }
        Assert.Equal(1, js.Module.Releases); Assert.Equal(1, js.Module.Port.Releases); Assert.Equal(1, js.Module.Port.Closes);
    }
    [Fact]
    public async Task Foreign_import_cancellation_is_failure_and_import_remains_retryable()
    {
        var js = new Runtime(); js.Import.TrySetCanceled(new CancellationToken(true)); await using var service = new IndexService(new JsHistoryIndexBackend(js)); var project = ProjectId.New();
        Assert.Equal(HistoryIndexStatus.StorageFailure, (await service.ListAsync(project, Budget)).Status);
        js.Import = new(TaskCreationOptions.RunContinuationsAsynchronously); js.Import.TrySetResult(js.Module);
        Assert.Equal(HistoryIndexStatus.Missing, (await service.ListAsync(project, Budget)).Status); Assert.Equal(2, js.Imports);
    }
    [Fact]
    public async Task Unknown_write_acknowledgement_is_uncertain_not_definitely_failed()
    {
        var js = new Runtime(); js.Import.TrySetResult(js.Module); js.Module.Port.BadWriteAck = true;
        await using var service = new IndexService(new JsHistoryIndexBackend(js)); var project = ProjectId.New();
        var key = new HistoricalArtifactKey(Guid.NewGuid(), project, new(new string('a', 64)), new(new string('b', 64)));
        var result = await service.AppendPersistedAsync(project, key, 0, Budget);
        Assert.Equal(HistoryIndexStatus.Uncertain, result.Status); Assert.True(result.WriteMayHaveCommitted); Assert.Equal(HistoryIndexStatus.StorageFailure, result.Cause);
    }
    private sealed class Runtime : IJSRuntime
    {
        public TaskCompletionSource<IJSObjectReference> Import = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Module Module = new(); public int Imports;
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => InvokeAsync<T>(name, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken token, object?[]? args)
        { Assert.Equal("import", name); Assert.Equal("./js/compile-history-index.js", args![0]); Imports++; Entered.TrySetResult(true); return new(Convert<T>()); }
        private async Task<T> Convert<T>() => (T)(object)await Import.Task;
    }
    private sealed class Module : IJSObjectReference
    {
        public readonly Port Port = new(); public int Releases;
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) { Assert.Equal("createHistoryIndexStorage", name); return ValueTask.FromResult((T)(object)Port); }
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken token, object?[]? args) => InvokeAsync<T>(name, args);
        public ValueTask DisposeAsync() { Releases++; return ValueTask.CompletedTask; }
    }
    private sealed class Port : IJSObjectReference
    {
        public bool BadWriteAck; public int Releases, Closes;
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args)
        {
            if (name == "dispose") { Closes++; return ValueTask.FromResult(default(T)!); }
            var wire = name == "read" ? new JsHistoryIndexBackend.Wire { Status = "ok", Revision = "0", Json = null } : new JsHistoryIndexBackend.Wire { Status = BadWriteAck ? "unknown-after-commit" : "saved" };
            return ValueTask.FromResult((T)(object)wire);
        }
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken token, object?[]? args) => InvokeAsync<T>(name, args);
        public ValueTask DisposeAsync() { Releases++; return ValueTask.CompletedTask; }
    }
}
