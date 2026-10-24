using System.Text.Json;
using Client.Analysis.RenderedExtent;
using Client.Compilation.Presentation;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.RenderedExtent;

// Interop lifetime CONTRACTS only. No actual Worker/WASM/policy execution.
public sealed class RustRenderedExtentEvaluatorTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(3);
    private sealed class Runtime : IJSRuntime
    {
        internal int Imports;
        internal readonly Module Module = new();
        internal TaskCompletionSource<IJSObjectReference>? Import;
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args)
        {
            Assert.Equal("import", identifier); Assert.Equal("./js/rendered-extent-bridge.js", args![0]); Imports++;
            var module = Import is { } pending ? await pending.Task.ConfigureAwait(false) : Module;
            return (T)(object)module;
        }
    }
    private sealed class Module : IJSObjectReference
    {
        internal int Factories, Disposals;
        internal readonly Port Port = new();
        internal TaskCompletionSource<IJSObjectReference>? Factory;
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args)
        {
            Assert.Equal("createRenderedExtentPort", identifier); Factories++;
            return (T)(object)(Factory is { } pending ? await pending.Task.ConfigureAwait(false) : Port);
        }
        internal TaskCompletionSource? ReferenceHold;
        internal readonly TaskCompletionSource ReferenceReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SynchronizationContext? ReferenceContext;
        public async ValueTask DisposeAsync()
        {
            Disposals++; ReferenceContext = SynchronizationContext.Current;
            if (ReferenceHold is { } held) await held.Task; // Deliberately SDK-shaped INTERNAL capturing await.
            ReferenceReleased.TrySetResult();
        }
    }
    private sealed record Invocation(Type ResultType, object?[]? Arguments, SynchronizationContext? Context, CancellationToken Token);
    private sealed class Port : IJSObjectReference
    {
        internal int Calls, Cancels, Closes, Disposals;
        internal long Epoch;
        internal TaskCompletionSource<bool>? Hold, CancelHold;
        internal Invocation? CancelInvocation, CloseInvocation;
        internal TaskCompletionSource? ReferenceHold;
        internal readonly TaskCompletionSource ReferenceReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SynchronizationContext? ReferenceContext;
        internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<RenderedExtentBridgeReply, RenderedExtentBridgeReply>? Transform;
        internal string? RawEnvelope;
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args)
        {
            if (identifier == "cancel")
            {
                CancelInvocation = new(typeof(T), args?.ToArray(), SynchronizationContext.Current, ct);
                Cancels++; if (CancelHold is { } cancel) await cancel.Task.ConfigureAwait(false); return default!;
            }
            if (identifier == "dispose")
            {
                CloseInvocation = new(typeof(T), args?.ToArray(), SynchronizationContext.Current, ct);
                Closes++; return default!;
            }
            Assert.Equal("evaluate", identifier); Calls++; long epoch = ++Epoch;
            using var doc = JsonDocument.Parse((string)args![0]!); var root = doc.RootElement;
            var request = new RenderedExtentRequest(root.GetProperty("requestId").GetString()!, root.GetProperty("pdfSha256").GetString()!,
                root.GetProperty("pageCount").GetInt32(), root.GetProperty("workType").GetString()!);
            Reached.TrySetResult();
            if (Hold is { } held) await held.Task.ConfigureAwait(false); // Transport fake must not capture the caller's compile owner.
            if (RawEnvelope is { } raw)
                return JsonSerializer.Deserialize<T>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var reply = new RenderedExtentBridgeReply { Version = 1, RequestId = request.RequestId, WorkerEpoch = epoch,
                Status = "completed", ResponseJson = ExtentFixtureJson.Body(request) };
            return (T)(object)(Transform?.Invoke(reply) ?? reply);
        }
        public async ValueTask DisposeAsync()
        {
            Disposals++; ReferenceContext = SynchronizationContext.Current;
            if (ReferenceHold is { } held) await held.Task; // Emulates actual SDK JSObjectReference.DisposeAsync capture.
            ReferenceReleased.TrySetResult();
        }
    }

    [Fact] public async Task Invalid_request_never_imports_or_dispatches()
    {
        var js = new Runtime(); await using var evaluator = new RustRenderedExtentEvaluator(js);
        Assert.Equal(RenderedExtentEvaluationStatus.InvalidInput,
            (await evaluator.EvaluateAsync(ExtentFixtureJson.Request() with { RequestId = new('X', 32) })).Status);
        Assert.Equal(RenderedExtentEvaluationStatus.InvalidInput,
            (await evaluator.EvaluateAsync(ExtentFixtureJson.Request() with { WorkType = "unknown" })).Status);
        Assert.Equal(0, js.Imports); Assert.Equal(0, js.Module.Port.Calls);
    }
    [Fact] public async Task Cancellation_during_actual_import_holds_admission_and_never_dispatches_late()
    {
        var js = new Runtime { Import = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var evaluator = new RustRenderedExtentEvaluator(js); using var stop = new CancellationTokenSource();
        var checking = evaluator.EvaluateAsync(ExtentFixtureJson.Request(), stop.Token); stop.Cancel();
        Assert.False(checking.IsCompleted);
        Assert.Equal(RenderedExtentEvaluationStatus.Busy, (await evaluator.EvaluateAsync(ExtentFixtureJson.Request())).Status);
        Assert.Equal(1, js.Imports); js.Import.TrySetResult(js.Module);
        Assert.Equal(RenderedExtentEvaluationStatus.Cancelled, (await checking.WaitAsync(Bound)).Status);
        Assert.Equal(0, js.Module.Port.Calls);
        var result = await evaluator.EvaluateAsync(ExtentFixtureJson.Request());
        Assert.Equal(RenderedExtentEvaluationStatus.Completed, result.Status); Assert.Equal(1, result.WorkerEpoch);
        Assert.Equal(1, js.Imports); Assert.Equal(1, js.Module.Factories);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Disposal_is_bounded_and_late_import_or_factory_is_cleaned_once(bool factory)
    {
        var js = new Runtime();
        var pending = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (factory) js.Module.Factory = pending; else js.Import = pending;
        var evaluator = new RustRenderedExtentEvaluator(js);
        var checking = evaluator.EvaluateAsync(ExtentFixtureJson.Request());
        Assert.False(checking.IsCompleted);
        await evaluator.DisposeAsync(); Assert.False(checking.IsCompleted); // No wait on foreign init.
        pending.TrySetResult(factory ? js.Module.Port : js.Module);
        Assert.Equal(RenderedExtentEvaluationStatus.Disposed, (await checking.WaitAsync(Bound)).Status);
        Assert.Equal(0, js.Module.Port.Calls); Assert.Equal(1, js.Module.Disposals);
        if (factory) { Assert.Equal(1, js.Module.Port.Closes); Assert.Equal(1, js.Module.Port.Disposals); }
        await evaluator.DisposeAsync(); Assert.Equal(1, js.Module.Disposals);
    }
    [Fact] public async Task Actual_evaluation_task_survives_logical_cancel_until_settled_without_duplicate_work()
    {
        var js = new Runtime(); js.Module.Port.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var evaluator = new RustRenderedExtentEvaluator(js); using var stop = new CancellationTokenSource();
        var checking = evaluator.EvaluateAsync(ExtentFixtureJson.Request(), stop.Token);
        await js.Module.Port.Reached.Task.WaitAsync(Bound); stop.Cancel();
        Assert.False(checking.IsCompleted); Assert.Equal(1, js.Module.Port.Cancels);
        Assert.Equal(RenderedExtentEvaluationStatus.Busy, (await evaluator.EvaluateAsync(ExtentFixtureJson.Request())).Status);
        js.Module.Port.Hold.TrySetResult(true);
        Assert.Equal(RenderedExtentEvaluationStatus.Cancelled, (await checking.WaitAsync(Bound)).Status);
        Assert.Equal(1, js.Module.Port.Calls);
    }
    [Fact] public async Task Late_actual_cancel_call_also_owns_admission_until_settled()
    {
        var js = new Runtime(); js.Module.Port.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        js.Module.Port.CancelHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var evaluator = new RustRenderedExtentEvaluator(js); using var stop = new CancellationTokenSource();
        var checking = evaluator.EvaluateAsync(ExtentFixtureJson.Request(), stop.Token);
        await js.Module.Port.Reached.Task.WaitAsync(Bound); stop.Cancel();
        js.Module.Port.Hold.TrySetResult(true);
        Assert.Equal(RenderedExtentEvaluationStatus.Busy, (await evaluator.EvaluateAsync(ExtentFixtureJson.Request())).Status);
        Assert.False(checking.IsCompleted); Assert.Equal(1, js.Module.Port.Cancels);
        js.Module.Port.CancelHold.TrySetResult(true);
        Assert.Equal(RenderedExtentEvaluationStatus.Cancelled, (await checking.WaitAsync(Bound)).Status);
    }
    [Fact] public void Cancel_signalled_inside_compile_owner_does_not_capture_it_for_transport_completion() => Run(async () =>
    {
        var parent = SynchronizationContext.Current!; int wakeups = 0;
        var owner = new CompileOwnerContext(action =>
        {
            Interlocked.Increment(ref wakeups);
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            parent.Post(_ => { try { action(); signal.TrySetResult(); } catch (Exception error) { signal.TrySetException(error); } }, null);
            return signal.Task;
        });
        var js = new Runtime(); js.Module.Port.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        js.Module.Port.CancelHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var evaluator = new RustRenderedExtentEvaluator(js); using var stop = new CancellationTokenSource();
        var checking = evaluator.EvaluateAsync(ExtentFixtureJson.Request(), stop.Token);
        try
        {
            await js.Module.Port.Reached.Task.WaitAsync(Bound);
            owner.Run(() =>
            {
                stop.Cancel(); // Real invalidation callbacks run inside this borrowed context.
                Assert.Same(owner, SynchronizationContext.Current); // Transport entry must restore caller scope.
            });
            Assert.Equal(1, js.Module.Port.Cancels);
            var call = Assert.IsType<Invocation>(js.Module.Port.CancelInvocation);
            Assert.Equal(typeof(IJSVoidResult), call.ResultType); // Same SDK void-result sentinel, not JSON object result.
            Assert.Equal(new object?[] { ExtentFixtureJson.Request().RequestId }, call.Arguments);
            Assert.False(call.Token.CanBeCanceled); Assert.Null(call.Context);
            Assert.Equal(0, owner.PendingOwned); Assert.Equal(0, owner.PendingPosts);
            js.Module.Port.Hold.TrySetResult(true);
            await Task.Run(() => js.Module.Port.CancelHold.TrySetResult(true));
            Assert.Equal(RenderedExtentEvaluationStatus.Cancelled, (await checking.WaitAsync(Bound)).Status);
            Assert.Equal(0, wakeups); // Includes SDK-internal await capture; owner remains alive so regression fails safely.
        }
        finally
        {
            js.Module.Port.Hold.TrySetResult(true); js.Module.Port.CancelHold.TrySetResult(true);
            try { await checking.WaitAsync(Bound); }
            finally { owner.RequestClose(); await owner.WhenSettled.WaitAsync(Bound); }
        }
    });
    [Fact] public void Pinned_sdk_void_extension_captures_owner_despite_outer_configure_await_false() => Run(async () =>
    {
        var parent = SynchronizationContext.Current!; int wakeups = 0;
        var owner = new CompileOwnerContext(action =>
        {
            Interlocked.Increment(ref wakeups);
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            parent.Post(_ => { try { action(); signal.TrySetResult(); } catch (Exception error) { signal.TrySetException(error); } }, null);
            return signal.Task;
        });
        var port = new Port { CancelHold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        async Task OuterAwait() => await port.InvokeVoidAsync("cancel", ExtentFixtureJson.Request().RequestId).ConfigureAwait(false);
        // Positive control executes the PINNED SDK extension, not a copied body.
        // Port itself does not capture; this one wakeup belongs to the extension.
        var pending = owner.Run(OuterAwait);
        try
        {
            Assert.False(pending.IsCompleted);
            await Task.Run(() => port.CancelHold.TrySetResult(true));
            await pending.WaitAsync(Bound);
            Assert.Equal(1, wakeups);
        }
        finally
        {
            port.CancelHold.TrySetResult(true);
            try { await pending.WaitAsync(Bound); }
            finally { owner.RequestClose(); await owner.WhenSettled.WaitAsync(Bound); }
        }
    });
    [Theory][InlineData(false)][InlineData(true)]
    public void Disposal_preserves_void_semantics_and_does_not_capture_owner_inside_reference_wrappers(bool moduleReference) => Run(async () =>
    {
        var parent = SynchronizationContext.Current!; int wakeups = 0;
        var owner = new CompileOwnerContext(action =>
        {
            Interlocked.Increment(ref wakeups);
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            parent.Post(_ => { try { action(); signal.TrySetResult(); } catch (Exception error) { signal.TrySetException(error); } }, null);
            return signal.Task;
        });
        var js = new Runtime(); await using var evaluator = new RustRenderedExtentEvaluator(js);
        Assert.Equal(RenderedExtentEvaluationStatus.Completed, (await evaluator.EvaluateAsync(ExtentFixtureJson.Request())).Status);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (moduleReference) js.Module.ReferenceHold = held; else js.Module.Port.ReferenceHold = held;
        try
        {
            owner.Run(() =>
            {
                Assert.True(evaluator.DisposeAsync().IsCompletedSuccessfully); // Must not await the held foreign cleanup.
                Assert.Same(owner, SynchronizationContext.Current);
            });
            var call = Assert.IsType<Invocation>(js.Module.Port.CloseInvocation);
            Assert.Equal(typeof(IJSVoidResult), call.ResultType);
            Assert.NotNull(call.Arguments); Assert.Empty(call.Arguments);
            Assert.Null(call.Context); Assert.False(call.Token.CanBeCanceled);
            Assert.Equal(0, owner.PendingOwned); Assert.Equal(0, owner.PendingPosts);
            Assert.False(js.Module.ReferenceReleased.Task.IsCompleted);
            await Task.Run(() => held.TrySetResult());
            await js.Module.ReferenceReleased.Task.WaitAsync(Bound);
            Assert.Null(js.Module.Port.ReferenceContext); Assert.Null(js.Module.ReferenceContext);
            Assert.Equal(0, wakeups);
            Assert.Equal(1, js.Module.Port.Closes); Assert.Equal(1, js.Module.Port.Disposals); Assert.Equal(1, js.Module.Disposals);
            await evaluator.DisposeAsync(); Assert.Equal(1, js.Module.Disposals); // Reference disposal remains exactly once.
        }
        finally
        {
            held.TrySetResult();
            try { await evaluator.DisposeAsync(); await js.Module.ReferenceReleased.Task.WaitAsync(Bound); }
            finally { owner.RequestClose(); await owner.WhenSettled.WaitAsync(Bound); }
        }
    });
    [Theory][InlineData("id")][InlineData("epoch")][InlineData("version")][InlineData("status")][InlineData("unicode")][InlineData("limit")]
    public async Task Wrong_actual_envelope_correlation_or_bounds_is_not_completed(string field)
    {
        var js = new Runtime(); js.Module.Port.Transform = reply =>
        {
            switch (field)
            {
                case "id": reply.RequestId = new('f', 32); break;
                case "epoch": reply.WorkerEpoch = 0; break;
                case "version": reply.Version = 2; break;
                case "status": reply.Status = "passed"; break;
                case "unicode": reply.ResponseJson = "\ud800"; break;
                case "limit": reply.ResponseJson = new('x', 8193); break;
            }
            return reply;
        };
        await using var evaluator = new RustRenderedExtentEvaluator(js);
        Assert.Equal(RenderedExtentEvaluationStatus.InvalidResponse, (await evaluator.EvaluateAsync(ExtentFixtureJson.Request())).Status);
    }
    [Fact] public async Task Epoch_is_from_actual_reply_not_a_global_getter_and_replay_is_refused()
    {
        var js = new Runtime(); await using var evaluator = new RustRenderedExtentEvaluator(js);
        Assert.Equal(1, (await evaluator.EvaluateAsync(ExtentFixtureJson.Request())).WorkerEpoch);
        js.Module.Port.Transform = reply => { reply.WorkerEpoch = 1; return reply; };
        Assert.Equal(RenderedExtentEvaluationStatus.InvalidResponse, (await evaluator.EvaluateAsync(ExtentFixtureJson.Request())).Status);
        Assert.Equal(2, js.Module.Port.Calls); // Port has NO epoch-getter method; unexpected calls assert.
    }
    [Theory]
    [InlineData("{\"version\":1,\"requestId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"workerEpoch\":1,\"status\":\"completed\"}")]
    [InlineData("{\"version\":1,\"requestId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"workerEpoch\":1,\"status\":\"completed\",\"responseJson\":null,\"extra\":true}")]
    public async Task Missing_or_extra_interop_envelope_fields_are_rejected(string raw)
    {
        var js = new Runtime(); js.Module.Port.RawEnvelope = raw;
        await using var evaluator = new RustRenderedExtentEvaluator(js);
        Assert.Equal(RenderedExtentEvaluationStatus.InvalidResponse, (await evaluator.EvaluateAsync(ExtentFixtureJson.Request())).Status);
    }
}
