using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis.RenderedExtent;
using Client.Analysis.RenderedReview;
using Client.Pdf;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.RenderedReview;

public sealed class RenderedCaptionImageTests
{
    private static readonly JsonElement Capture = LoadCapture();

    [Theory]
    [InlineData(0, RenderedCaptionImageRelation.CaptionAbove, 36)]
    [InlineData(1, RenderedCaptionImageRelation.CaptionBelow, -20.3994140625)]
    public async Task Genuine_combined_facts_and_Rust_reply_preserve_signed_measurement(int imageIndex,
        RenderedCaptionImageRelation relation, double gap)
    {
        var capture = await InspectAsync();
        var request = capture.Create(imageIndex);
        var js = new ReviewInterop { Reply = frame => ReplayRust(frame, imageIndex) };
        await using var evaluator = new RustRenderedReviewEvaluator(js);
        var result = await evaluator.EvaluateCaptionImageAsync(request);

        Assert.Equal(RenderedExtentEvaluationStatus.Completed, result.Status);
        Assert.Equal(request.Review.RequestId, result.RequestId);
        Assert.Equal(1, result.WorkerEpoch);
        var observation = Assert.IsType<RenderedCaptionImageObservation>(result.Observation);
        Assert.Same(request, observation.Request);
        Assert.Equal(relation, observation.Relation);
        Assert.Equal(RenderedCaptionImageReason.MeasuredSeparation, observation.Reason);
        var geometry = Assert.IsType<RenderedCaptionImageGeometry>(observation.Geometry);
        Assert.Equal(gap, geometry.RawSignedGap);
        Assert.Equal(gap * 2 * 25.4 / 72, geometry.SignedGapMm, 10);
        Assert.InRange(geometry.AbsoluteErrorRaw, 0.118, 0.12);
        Assert.InRange(geometry.AbsoluteErrorMm, 0.083, 0.085);
        Assert.Contains("no-normative-outcome", observation.Limitations);

        Assert.Equal(request.Review.FactsSha256, PdfHashes.Of(js.ReceivedFacts!));
        using var facts = JsonDocument.Parse(js.ReceivedFacts!);
        Assert.Equal(2, facts.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(capture.Outcome.DocumentSha256, facts.RootElement.GetProperty("documentSha256").GetString());
        var layout = facts.RootElement.GetProperty("pages")[0].GetProperty("facts").GetRawText();
        var objects = facts.RootElement.GetProperty("objectLayout").GetRawText();
        Assert.Equal(Text(Capture, "layoutJson"), layout);
        Assert.Equal(Text(Capture, "objectJson"), objects);
        Assert.Equal(PdfHashes.Of(Encoding.UTF8.GetBytes(layout)), request.Pair.LayoutFactsSha256);
        Assert.Equal(PdfHashes.Of(Encoding.UTF8.GetBytes(objects)), request.Pair.ObjectFactsSha256);
        Assert.Equal(capture.Outcome.RequestId, request.Pair.InspectionRequestId);
        Assert.Equal(capture.Outcome.WorkerEpoch, request.Pair.InspectionEpoch);
        Assert.Equal(capture.Runtime.WasmSha256, request.Pair.RendererWasmSha256);
        Assert.Equal(capture.Runtime.LoaderSha256, request.Pair.RendererLoaderSha256);
    }

    [Theory]
    [InlineData("wrong-pdf")]
    [InlineData("wrong-page")]
    [InlineData("wrong-runtime")]
    [InlineData("wrong-fact-hash")]
    [InlineData("wrong-inspection-epoch")]
    [InlineData("missing-atomic-text")]
    [InlineData("tentative-caption")]
    [InlineData("ambiguous-caption-id")]
    [InlineData("unknown-image")]
    [InlineData("outside-caption")]
    public async Task Request_requires_current_atomic_inspection_and_explicit_pair(string alteration)
    {
        var capture = await InspectAsync();
        var review = capture.Review;
        var inspection = capture.Outcome;
        var chosen = review.Annotations[0];
        int image = 0;
        switch (alteration)
        {
            case "wrong-pdf": review = review with { PdfSha256 = new('0', 64) }; break;
            case "wrong-page": inspection = inspection with { PageIndex = 1 }; break;
            case "wrong-runtime": inspection = inspection with { Runtime = new(new('0', 64), new('1', 64)) }; break;
            case "wrong-fact-hash": inspection = inspection with { LayoutFactsSha256 = new('0', 64) }; break;
            case "wrong-inspection-epoch": inspection = inspection with { WorkerEpoch = 0 }; break;
            case "missing-atomic-text": inspection = inspection with { LayoutFacts = null }; break;
            case "tentative-caption": review = review with { Annotations = [chosen with { Certainty = "tentative" }] }; break;
            case "ambiguous-caption-id": review = review with { Annotations = [chosen, chosen] }; break;
            case "unknown-image": image = 2; break; // The direct text object's ordinal.
            case "outside-caption": review = review with { Annotations = [chosen with { CharacterEnd = 5 }] }; break;
        }
        Assert.Throws<ArgumentException>(() => RenderedCaptionImageRequest.Create(review, capture.Request, inspection,
            capture.Runtime, chosen.AnnotationId, image));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("annotation")]
    [InlineData("image")]
    [InlineData("inspection")]
    [InlineData("renderer")]
    [InlineData("facts")]
    [InlineData("profile")]
    [InlineData("duplicate")]
    [InlineData("normative")]
    [InlineData("wrong-sign")]
    [InlineData("wrong-gap")]
    [InlineData("changed-ink-box")]
    [InlineData("lowered-error")]
    [InlineData("ambiguous-mm")]
    public async Task Tampered_Rust_replies_never_publish_a_measurement(string alteration)
    {
        var request = (await InspectAsync()).Create(0);
        var js = new ReviewInterop { Reply = frame =>
        {
            var completed = JsonNode.Parse(ReplayRust(frame, 0))!.AsObject();
            var payload = completed["payload"]!.AsObject();
            var pair = payload["captionImagePair"]!.AsObject();
            var geometry = payload["geometry"]!.AsObject();
            switch (alteration)
            {
                case "revision": payload["compileEpoch"] = 41; break;
                case "annotation": pair["captionAnnotationId"] = 22; break;
                case "image": pair["objectIndex"] = 1; break;
                case "inspection": pair["inspectionEpoch"] = 50; break;
                case "renderer": pair["rendererWasmSha256"] = new string('c', 64); break;
                case "facts": pair["objectFactsSha256"] = new string('c', 64); break;
                case "profile": payload["profile"]!["version"] = "1.0.0"; break;
                case "duplicate": return "{\"protocolVersion\":1," + completed.ToJsonString()[1..];
                case "normative": payload["outcome"] = "pass"; break;
                case "wrong-sign": payload["relation"] = "captionBelow"; break;
                case "wrong-gap": geometry["rawSignedGap"] = 35.0; break;
                case "changed-ink-box": geometry["captionInkBox"]!["right"] = 60; break;
                case "lowered-error": geometry["absoluteErrorRaw"] = 0.05; break;
                case "ambiguous-mm": geometry["absoluteErrorMm"] = 36.0; break;
            }
            return completed.ToJsonString();
        } };
        await using var evaluator = new RustRenderedReviewEvaluator(js);
        var result = await evaluator.EvaluateCaptionImageAsync(request);
        Assert.Equal(RenderedExtentEvaluationStatus.InvalidResponse, result.Status);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task Indeterminate_and_strict_worker_failure_have_no_geometry()
    {
        var request = (await InspectAsync()).Create(0);
        var js = new ReviewInterop { Reply = frame =>
        {
            var reply = JsonNode.Parse(ReplayRust(frame, 0))!.AsObject();
            reply["payload"]!["relation"] = "indeterminate";
            reply["payload"]!["reason"] = "horizontal-overlap-unestablished";
            reply["payload"]!["geometry"] = null;
            return reply.ToJsonString();
        } };
        await using var evaluator = new RustRenderedReviewEvaluator(js);
        var uncertain = await evaluator.EvaluateCaptionImageAsync(request);
        Assert.Equal(RenderedExtentEvaluationStatus.Completed, uncertain.Status);
        var observation = Assert.IsType<RenderedCaptionImageObservation>(uncertain.Observation);
        Assert.Equal(RenderedCaptionImageRelation.Indeterminate, observation.Relation);
        Assert.Equal(RenderedCaptionImageReason.HorizontalOverlapUnestablished, observation.Reason);
        Assert.Null(observation.Geometry);

        js.Reply = _ => """{"protocolVersion":1,"status":"failed","error":{"code":"InvalidFacts"}}""";
        var refused = await evaluator.EvaluateCaptionImageAsync(request);
        Assert.Equal(RenderedExtentEvaluationStatus.InvalidInput, refused.Status);
        Assert.Equal("InvalidFacts", refused.Code);
        Assert.Null(refused.Observation);
    }

    [Fact]
    public async Task Profiles_share_actual_admission_and_reject_replayed_worker_epochs()
    {
        var request = (await InspectAsync()).Create(0);
        var legacyFacts = "{}"u8.ToArray();
        var legacy = request.Review with { FactsSha256 = PdfHashes.Of(legacyFacts) };
        var js = new ReviewInterop { Reply = frame => ReplayRust(frame, 0),
            EvaluationHold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var evaluator = new RustRenderedReviewEvaluator(js);
        using var stop = new CancellationTokenSource();
        var active = evaluator.EvaluateCaptionImageAsync(request, stop.Token);
        await js.Reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        Assert.False(active.IsCompleted);
        Assert.Equal(RenderedExtentEvaluationStatus.Busy, (await evaluator.EvaluateAsync(legacy, legacyFacts)).Status);
        js.EvaluationHold.TrySetResult(true);
        Assert.Equal(RenderedExtentEvaluationStatus.Cancelled, (await active.WaitAsync(TimeSpan.FromSeconds(3))).Status);
        Assert.Equal(1, js.Cancels);

        js.Reply = frame =>
        {
            Assert.Equal("1.0.0", frame["payload"]!["profile"]!["version"]!.GetValue<string>());
            Assert.False(frame["payload"]!.AsObject().ContainsKey("captionImagePair"));
            return "{}";
        };
        Assert.Equal(RenderedExtentEvaluationStatus.Completed, (await evaluator.EvaluateAsync(legacy, legacyFacts)).Status);
        js.Reply = frame => ReplayRust(frame, 0);
        js.Epoch = 1; // Next invocation replays the legacy response epoch.
        var stale = await evaluator.EvaluateCaptionImageAsync(request);
        Assert.Equal(RenderedExtentEvaluationStatus.InvalidResponse, stale.Status);
        Assert.Null(stale.Observation);
        Assert.Equal(1, js.Imports);
    }

    private static string ReplayRust(JsonObject frame, int objectIndex)
    {
        var recorded = JsonNode.Parse(Text(Capture, objectIndex == 0 ? "aboveReply" : "belowReply"))!.AsObject();
        var measured = recorded["payload"]!.AsObject();
        var requested = frame["payload"]!.AsObject();
        var measuredPair = measured["captionImagePair"]!.AsObject();
        var requestedPair = requested["captionImagePair"]!.AsObject();
        Assert.Equal("evaluateRenderedReview", frame["operation"]!.GetValue<string>());
        Assert.Equal(measured["profile"]!.ToJsonString(), requested["profile"]!.ToJsonString());
        Assert.Equal(measured["pdfSha256"]!.ToJsonString(), requested["pdfSha256"]!.ToJsonString());
        Assert.Equal(measuredPair["objectIndex"]!.ToJsonString(), requestedPair["objectIndex"]!.ToJsonString());
        Assert.Equal(measuredPair["layoutFactsSha256"]!.ToJsonString(), requestedPair["layoutFactsSha256"]!.ToJsonString());
        Assert.Equal(measuredPair["objectFactsSha256"]!.ToJsonString(), requestedPair["objectFactsSha256"]!.ToJsonString());
        // Replay positional measurement and exact native payloads. Rebind the
        // envelope IDs and transport identities assigned by this managed call.
        recorded["requestId"] = frame["requestId"]!.DeepClone();
        foreach (var (key, item) in requested)
            if (key != "annotations") measured[key] = item?.DeepClone();
        return recorded.ToJsonString();
    }

    private static async Task<Inspection> InspectAsync()
    {
        var document = new PdfDocument(Convert.FromBase64String(Text(Capture, "pdfBase64")));
        var request = new PdfObjectLayoutRequest(document, 0, new(), IncludeTextLayout: true);
        var runtime = new PdfReplay();
        await using var inspector = new JsPdfInspector(runtime, runtime);
        var outcome = await inspector.InspectObjectLayoutAsync(request);
        Assert.Equal(PdfInspectionStatus.Completed, outcome.Status);
        var review = new RenderedReviewRequest(new('a', 32), document.Sha256, new('b', 32), 3, new('c', 64),
            Guid.Parse("b7ccb12d-a0b7-40df-bf33-0da810bed446"), "bachelorQualification", 4, 1, [0],
            [new(1, "figureCaption", 0, 0, 4, null, null, "authorLabel", "confirmedObservation")], "");
        return new(request, outcome, runtime.Identity, review);
    }

    private sealed record Inspection(PdfObjectLayoutRequest Request, PdfObjectLayoutOutcome Outcome,
        PdfRuntimeIdentity Runtime, RenderedReviewRequest Review)
    {
        internal RenderedCaptionImageRequest Create(int index) => RenderedCaptionImageRequest.Create(Review, Request,
            Outcome, Runtime, Review.Annotations[0].AnnotationId, index);
    }

    private static JsonElement LoadCapture()
    {
        using var stream = typeof(RenderedCaptionImageTests).Assembly.GetManifestResourceStream(
            "Workspace.Tests.Fixtures.RenderedReview.producer-caption-image-v1.json")!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.Clone();
    }

    private static string Text(JsonElement element, string member) => element.GetProperty(member).GetString()!;

    private sealed class PdfReplay : IJSRuntime, IJSObjectReference, IPdfRuntimeProvider
    {
        private static readonly byte[] Loader = "// test loader"u8.ToArray();
        private static readonly byte[] Wasm = [0, 97, 115, 109, 1, 0, 0, 0];
        internal PdfRuntimeIdentity Identity { get; } = new(PdfHashes.Of(Loader), PdfHashes.Of(Wasm));

        public ValueTask<PdfRuntimePackage?> GetAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<PdfRuntimePackage?>(new(Loader, Wasm, Identity.LoaderSha256, Identity.WasmSha256));
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, default, args);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken cancellationToken, object?[]? args)
        {
            if (id is "import" or "createPdfPort") return ValueTask.FromResult((T)(object)this);
            if (id == "dispose") return ValueTask.FromResult(default(T)!);
            Assert.Equal("inspect", id);
            using var frame = JsonDocument.Parse((string)args![0]!);
            var input = frame.RootElement;
            Assert.True(input.GetProperty("inspectLayout").GetBoolean());
            Assert.True(input.GetProperty("inspectObjectLayout").GetBoolean());
            Assert.Equal(Text(Capture, "pdfSha256"), Text(input, "documentSha256"));
            return ValueTask.FromResult((T)(object)new PdfWireReply
            {
                Version = 2, Status = "completed", RequestId = Text(input, "requestId"), DocumentSha256 = Text(input, "documentSha256"),
                Epoch = input.GetProperty("epoch").GetInt64(), LoaderSha256 = Text(input, "loaderSha256"), WasmSha256 = Text(input, "wasmSha256"),
                EnteredNative = true, NativeStatus = 0, PageCount = 1,
                LayoutState = "available", ObjectLayoutState = "available",
                LayoutCapabilitiesJson = Text(Capture, "capabilitiesJson"), LayoutFactsJson = Text(Capture, "layoutJson"),
                ObjectLayoutFactsJson = Text(Capture, "objectJson"),
                NativeCalls = ["e1_version", "e1_open", "e1_document_pages", "e1_page", "e1_layout_capabilities", "e1_copy",
                    "e1_layout_facts", "e1_copy", "e1_object_layout_facts", "e1_copy", "e1_reset"]
            });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReviewInterop : IJSRuntime, IJSObjectReference
    {
        internal required Func<JsonObject, string> Reply { get; set; }
        internal byte[]? ReceivedFacts { get; private set; }
        internal TaskCompletionSource<bool>? EvaluationHold { get; init; }
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Imports { get; private set; }
        internal int Cancels { get; private set; }
        internal long Epoch { get; set; }

        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, default, args);
        public async ValueTask<T> InvokeAsync<T>(string id, CancellationToken cancellationToken, object?[]? args)
        {
            if (id == "import") { Imports++; return (T)(object)this; }
            if (id == "createRenderedReviewPort") return (T)(object)this;
            if (id == "cancel") { Cancels++; return default!; }
            if (id == "dispose") return default!;
            Assert.Equal("evaluate", id);
            var request = JsonNode.Parse((string)args![0]!)!.AsObject();
            ReceivedFacts = (byte[])args[1]!;
            Reached.TrySetResult();
            if (EvaluationHold is not null) await EvaluationHold.Task;
            return (T)(object)new RenderedReviewBridgeReply { Version = 1,
                RequestId = request["requestId"]!.GetValue<string>(), WorkerEpoch = ++Epoch,
                Status = "completed", ResponseJson = Reply(request) };
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
