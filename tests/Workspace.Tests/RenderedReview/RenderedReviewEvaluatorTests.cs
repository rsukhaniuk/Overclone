using Client.Analysis.RenderedExtent;
using Client.Analysis.RenderedReview;
using Client.Pdf;
using Microsoft.JSInterop;
using System.Text.Json.Nodes;
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
        public Func<string, string>? Respond;
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
            return (T)(object)new RenderedReviewBridgeReply { Version = 1, RequestId = request, WorkerEpoch = ++Epoch, Status = "completed", ResponseJson = Respond?.Invoke((string)args[0]!) ?? "{}" };
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task Opt_in_reference_transport_requires_an_exact_pair_and_values()
    {
        var review = Request() with { PageCount = 2, RequestedPages = [0, 1] };
        var pair = new RenderedReferencePair(new(new('a', 64), new('b', 64)),
            new(new('a', 32), 1, 0, 17, 20, "[1]"), new(new('b', 32), 2, 1, 18, 21, "[2]"));
        var request = new RenderedReferencePairRequest(review, pair, Facts);
        var js = new Runtime();
        await using var evaluator = new RustRenderedReviewEvaluator(js);
        static JsonObject Completed(string metadata)
        {
            var r = JsonNode.Parse(metadata)!.AsObject();
            Assert.Equal("evaluateRenderedReview", (string?)r["operation"]);
            r.Remove("operation");
            r["status"] = "completed";
            var p = r["payload"]!.AsObject();
            Assert.Equal("knu-csc-2025-rendered-reference-pair", (string?)p["profile"]?["id"]);
            Assert.Equal("mainWork", (string?)p["printedReferencePair"]?["scope"]);
            Assert.Empty(p["annotations"]!.AsArray());
            p.Remove("annotations");
            p["guidelineSha256"] = RenderedExtentContract.GuidelineSha256;
            p["comparison"] = "numbersDiffer"; p["reason"] = "confirmed-printed-number-pair";
            p["numbers"] = new JsonObject { ["citation"] = 1, ["entry"] = 2 };
            p["roleProvenance"] = "authorConfirmedPdfSearchSelections"; p["sourceLocation"] = null;
            p["limitations"] = new JsonArray("selected-printed-numbers-only", "citation-key-and-bibliography-completeness-unassessed");
            return r;
        }
        js.Respond = s => Completed(s).ToJsonString();
        var accepted = await evaluator.EvaluateReferencePairAsync(request);
        Assert.Equal(RenderedExtentEvaluationStatus.Completed, accepted.Status);
        Assert.NotNull(accepted.Observation);
        Assert.Same(request, accepted.Observation.Request);
        Assert.Equal(RenderedReferenceComparison.NumbersDiffer, accepted.Observation.Comparison);
        Assert.Equal(1, accepted.Observation.CitationNumber);
        Assert.Equal(2, accepted.Observation.EntryNumber);

        foreach (Action<JsonNode> forge in new Action<JsonNode>[]
        {
            p => p["printedReferencePair"]!["entry"]!["selectionEpoch"] = 99,
            p => p["printedReferencePair"]!["rendererLoaderSha256"] = new string('c', 64),
            p => p["compileEpoch"] = 99,
            p => p["numbers"]!["entry"] = 3,
            p => p["comparison"] = "numbersMatch",
            p => p["sourceLocation"] = "guessed"
        })
        {
            js.Respond = s => { var r = Completed(s); forge(r["payload"]!); return r.ToJsonString(); };
            var refused = await evaluator.EvaluateReferencePairAsync(request);
            Assert.Equal(RenderedExtentEvaluationStatus.InvalidResponse, refused.Status);
            Assert.Null(refused.Observation);
        }
        Assert.Equal(1, js.Imports);
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
