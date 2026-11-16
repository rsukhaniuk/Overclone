using Client.Analysis.RenderedExtent;
using Client.Analysis.RenderedReview;
using Client.Pdf;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.RenderedReview;

// Transport lifetime tests only; genuine native facts/Rust/browser evidence are
// verified independently. Fakes deliberately ignore cancellation until released.
public sealed class RenderedReviewEvaluatorTests
{
    private static readonly byte[] Facts = "{}"u8.ToArray();
    private static RenderedReviewRequest Request() => new(new('a', 32), new('b', 64), new('c', 32), 1, new('d', 64), Guid.NewGuid(),
        "coursework", 0, 1, Array.AsReadOnly(new[] { 0 }), Array.Empty<RenderedRoleAnnotation>(), PdfHashes.Of(Facts));
    private sealed class Runtime : IJSRuntime, IJSObjectReference
    {
        public int Imports, Cancels, Calls;
        public byte[]? Received;
        public long Epoch;
        public TaskCompletionSource<IJSObjectReference>? ImportHold;
        public TaskCompletionSource<bool>? EvaluationHold;
        public TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, default, args);
        public async ValueTask<T> InvokeAsync<T>(string id, CancellationToken cancellationToken, object?[]? args)
        {
            if (id == "import") { Imports++; Assert.Equal("./js/rendered-review-bridge.js", args![0]); return (T)(object)(ImportHold is { } h ? await h.Task : this); }
            if (id == "createRenderedReviewPort") return (T)(object)this;
            if (id == "cancel") { Cancels++; return default!; }
            if (id == "dispose") return default!;
            Assert.Equal("evaluate", id); Calls++; Received = (byte[])args![1]!; Reached.TrySetResult();
            using var doc = System.Text.Json.JsonDocument.Parse((string)args[0]!);
            var request = doc.RootElement.GetProperty("requestId").GetString()!;
            if (EvaluationHold is { } held) await held.Task;
            return (T)(object)new RenderedReviewBridgeReply { Version = 1, RequestId = request, WorkerEpoch = ++Epoch, Status = "completed", ResponseJson = "{}" };
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task HashMismatchAndByteQuotaNeverImport()
    {
        var js = new Runtime(); await using var evaluator = new RustRenderedReviewEvaluator(js);
        Assert.Equal(RenderedExtentEvaluationStatus.InvalidInput, (await evaluator.EvaluateAsync(Request(), "[]"u8.ToArray())).Status);
        Assert.Equal(RenderedExtentEvaluationStatus.InvalidInput, (await evaluator.EvaluateAsync(Request(), new byte[4194305])).Status);
        Assert.Equal(0, js.Imports);
    }
    [Fact]
    public async Task CancellationDuringImportRetainsAdmissionUntilActualSettlement()
    {
        var js = new Runtime { ImportHold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var evaluator = new RustRenderedReviewEvaluator(js); using var stop = new CancellationTokenSource();
        var active = evaluator.EvaluateAsync(Request(), Facts, stop.Token); stop.Cancel();
        Assert.False(active.IsCompleted); Assert.Equal(RenderedExtentEvaluationStatus.Busy, (await evaluator.EvaluateAsync(Request(), Facts)).Status);
        js.ImportHold.TrySetResult(js); Assert.Equal(RenderedExtentEvaluationStatus.Cancelled, (await active.WaitAsync(TimeSpan.FromSeconds(3))).Status);
        Assert.Equal(0, js.Calls);
    }
    [Fact]
    public async Task OwnedFactsAndActualEvaluationRemainReservedAfterLogicalCancellation()
    {
        var js = new Runtime { EvaluationHold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var evaluator = new RustRenderedReviewEvaluator(js); using var stop = new CancellationTokenSource();
        var callerBytes = Facts.ToArray(); var active = evaluator.EvaluateAsync(Request(), callerBytes, stop.Token);
        await js.Reached.Task.WaitAsync(TimeSpan.FromSeconds(3)); callerBytes[0] = (byte)'[';
        Assert.Equal((byte)'{', js.Received![0]); stop.Cancel(); Assert.False(active.IsCompleted);
        Assert.Equal(RenderedExtentEvaluationStatus.Busy, (await evaluator.EvaluateAsync(Request(), Facts)).Status);
        js.EvaluationHold.TrySetResult(true); Assert.Equal(RenderedExtentEvaluationStatus.Cancelled, (await active.WaitAsync(TimeSpan.FromSeconds(3))).Status);
        Assert.True(js.Cancels > 0);
    }
}
