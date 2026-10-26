using System.Text.Json;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Renaming;
using Microsoft.JSInterop;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.ProjectRename;

// Actual WorkspaceStorage adapter over controlled SDK IO. No IndexedDB/runtime
// execution: JS transaction completion still needs independent real browser proof.
public sealed class ProjectRenameStorageTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private sealed class Runtime : IJSRuntime
    {
        internal readonly Module Module = new();
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            Assert.Equal("import", identifier); Assert.Equal("./js/workspace-storage.js", args![0]);
            return new((T)(object)Module);
        }
    }
    private sealed class Module : IJSObjectReference
    {
        internal readonly TaskCompletionSource RenameReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CancelReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<string> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CancelRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal object?[]? RenameArgs, CancelArgs;
        internal bool UsedCancellableTransport;
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            UsedCancellableTransport |= token.CanBeCanceled;
            if (identifier == "renameProjectName")
            {
                RenameArgs = args; RenameReached.TrySetResult(); string status = await Reply.Task; // SDK-shaped INTERNAL capture.
                return JsonSerializer.Deserialize<T>("{\"status\":\"" + status + "\"}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            }
            Assert.Equal("cancelProjectRename", identifier); CancelArgs = args; CancelReached.TrySetResult();
            await CancelRelease.Task; return (T)(object)true;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Theory][InlineData("applied")][InlineData("cancelled")]
    public void Adapter_waits_actual_transaction_and_cancel_without_cancellation_proxy(string actual) => Run(async () =>
    {
        var js = new Runtime(); var storage = new WorkspaceStorage(js); using var stop = new CancellationTokenSource();
        var expected = new ProjectRenameSnapshot(ProjectId.New(), "Old", RevisionId.New(), 1);
        var pending = storage.RenameProjectAsync(expected, "Нове🙂", "own-1", stop.Token);
        await js.Module.RenameReached.Task.WaitAsync(Bound); stop.Cancel(); await js.Module.CancelReached.Task.WaitAsync(Bound);
        Assert.False(pending.IsCompleted); Assert.False(js.Module.UsedCancellableTransport);
        Assert.Equal(new object?[] { expected.Id.ToString(), "Old", expected.Head!.Value.ToString(), 1, "Нове🙂", "own-1" }, js.Module.RenameArgs);
        Assert.Equal(new object?[] { "own-1", expected.Id.ToString() }, js.Module.CancelArgs);
        js.Module.Reply.TrySetResult(actual); Assert.False(pending.IsCompleted); // Cancel IO still actually held.
        js.Module.CancelRelease.TrySetResult();
        var result = await pending.WaitAsync(Bound);
        Assert.Equal(actual == "applied" ? ProjectRenameCode.Applied : ProjectRenameCode.Cancelled, result.Code);
    });
    [Fact] public async Task Unsupported_old_adapter_fails_closed_without_metadata_write()
    {
        var adapter = new CompilationTestWorkspace.Repository(); IProjectRepository old = adapter;
        var before = adapter.Project;
        var result = await old.RenameProjectAsync(ProjectRenameSnapshot.Capture(before), "new", "not-owned", default);
        Assert.Equal(ProjectRenameCode.Unsupported, result.Code); Assert.Equal(before, adapter.Project); Assert.Equal(0, adapter.Commits);
    }
}
