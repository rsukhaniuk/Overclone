using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Analysis.RenderedExtent;
using Client.Analysis.RenderedReview;
using Client.Pdf;
using Xunit;
using Fixture = Workspace.Tests.RenderedRoleBinding.RoleBindingTests.Host;
using NativeRoleFact = Workspace.Tests.RenderedRoleBinding.NativeRoleFactAttribute;
using NativeRoleTheory = Workspace.Tests.RenderedRoleBinding.NativeRoleTheoryAttribute;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.RenderedRoleBindingController;

// Real current-report/capture/ticket/native binding admission over the retained
// actual native fixture. Layout/evaluation ports below are explicitly modeled;
// these are controller lifetime/authority tests, not new measurement acceptance.
public sealed class ControllerBindingTests
{
    [NativeRoleFact]
    public void Explicit_binding_mints_ids_and_retains_only_known_exact_proof_annotations() => Run(async () =>
    {
        await using var h = await Host.Open(); var before = h.Base.Work.State();
        Assert.True(h.Controller.CanBindSourceEvent); Assert.True(await h.Bind());
        var annotation = Assert.Single(h.Controller.Annotations); var proof = Assert.Single(h.Controller.SourceBindings);
        Assert.Same(annotation, proof.Annotation); Assert.Equal("captionText", proof.RangeKind); Assert.False(proof.GlyphInkMeasured);
        Assert.Equal("literalSourceAndNativeMap", annotation.Provenance); Assert.True(annotation.AnnotationId > 0);
        Assert.True(h.Controller.AnnotationsCurrent); Assert.True(h.Controller.CanCheck);
        h.Controller.SelectAnnotations([annotation, Author("body")]);
        Assert.Same(annotation, h.Controller.Annotations[0]); Assert.Same(proof, Assert.Single(h.Controller.SourceBindings));
        Assert.NotEqual(annotation.AnnotationId, h.Controller.Annotations[1].AnnotationId);
        long version = h.Controller.AnnotationVersion;
        h.Controller.SelectAnnotations([annotation with { }]);
        Assert.Equal(version, h.Controller.AnnotationVersion); Assert.Same(annotation, h.Controller.Annotations[0]);
        h.Controller.SelectAnnotations([annotation with { Provenance = "validatedNativeClassification" }]);
        Assert.Equal(version, h.Controller.AnnotationVersion);
        Assert.True(await h.Bind(2)); Assert.Equal(2, h.Controller.SourceBindings.Count);
        Assert.True(h.Controller.SourceBindings[1].Annotation.AnnotationId > annotation.AnnotationId);
        Assert.Equal(before, h.Base.Work.State()); Assert.Equal(0, h.Base.Work.Repo.Commits);
    });

    [NativeRoleFact]
    public void A_foreign_service_issued_annotation_cannot_be_adopted_through_author_label_API() => Run(async () =>
    {
        await using var h = await Host.Open();
        var proof = (await h.Base.Binding.BindAsync(h.Base.Request())).Value!; Assert.NotNull(proof);
        h.Controller.SelectAnnotations([proof.Annotation]);
        Assert.Empty(h.Controller.Annotations); Assert.Empty(h.Controller.SourceBindings); Assert.Equal(0, h.Controller.AnnotationVersion);
        Assert.True(await h.Bind()); Assert.NotSame(proof.Annotation, Assert.Single(h.Controller.Annotations));
    });

    [NativeRoleTheory]
    [InlineData("analysis-profile")][InlineData("analysis-root")][InlineData("analysis-work-type")]
    [InlineData("samehash-new-ticket")][InlineData("mutable-import")][InlineData("controller-work-type")]
    public void Stale_machine_labels_invalidate_report_and_cannot_be_blindly_reconfirmed(string change) => Run(async () =>
    {
        await using var h = await Host.Open(); Assert.True(await h.Bind()); await h.Check();
        Assert.True(h.Controller.ReportCurrent); Assert.NotNull(h.Controller.Report);
        long version = h.Controller.AnnotationVersion; var old = h.Controller.Annotations;
        switch (change)
        {
            case "analysis-profile": h.Base.Analysis.SetProfile(AnalysisProfile.EngineStructural()); break;
            case "analysis-root": h.Base.Analysis.SetSelection(AnalysisSelection.KnuWithThesis(AnalysisWorkType.BachelorQualification, h.Base.Chapter.ToString())); break;
            case "analysis-work-type": h.Base.Analysis.SetSelection(AnalysisSelection.KnuWithThesis(AnalysisWorkType.MasterQualification, h.Base.Main.ToString())); break;
            case "samehash-new-ticket": await h.Base.Compile(); break;
            case "mutable-import": h.Base.Work.Workspace.Files.Single(f => f.Id == h.Base.Main).ImportedBytes![0] ^= 1; break;
            case "controller-work-type": h.Controller.SelectWorkType("masterQualification"); break;
        }
        Assert.False(h.Controller.AnnotationsCurrent); Assert.False(h.Controller.ReportCurrent); Assert.Null(h.Controller.Report); Assert.False(h.Controller.CanCheck);
        h.Controller.SelectAnnotations(old); Assert.Equal(version, h.Controller.AnnotationVersion);
        Assert.Same(old[0], h.Controller.Annotations[0]); Assert.False(h.Controller.AnnotationsCurrent);
        h.Controller.SelectAnnotations([]); Assert.Empty(h.Controller.SourceBindings); Assert.True(h.Controller.AnnotationsCurrent);
    });

    [NativeRoleFact]
    public void Removal_preserves_stale_authority_of_remaining_labels_and_clear_is_safe() => Run(async () =>
    {
        await using var h = await Host.Open(); Assert.True(await h.Bind()); Assert.True(await h.Bind(2));
        await h.Base.Compile(); Assert.False(h.Controller.AnnotationsCurrent);
        var first = h.Controller.Annotations[0]; var last = h.Controller.Annotations[1];
        h.Controller.RemoveAnnotation(first.AnnotationId);
        Assert.Same(last, Assert.Single(h.Controller.Annotations)); Assert.False(h.Controller.AnnotationsCurrent);
        h.Controller.RemoveAnnotation(last.AnnotationId);
        Assert.Empty(h.Controller.Annotations); Assert.Empty(h.Controller.SourceBindings); Assert.True(h.Controller.AnnotationsCurrent);
        Assert.True(await h.Bind());
    });

    [NativeRoleTheory]
    [InlineData("inspection")][InlineData("epoch")]
    public void Cancelled_binding_reserves_observable_actual_task_until_settlement(string stage) => Run(async () =>
    {
        await using var h = await Host.Open();
        Task reached;
        if (stage == "inspection") { h.Inspector.InspectionHold = Gate(); reached = h.Inspector.InspectionReached.Task; }
        else { h.Base.Js.EpochGate = Gate(); h.Base.Js.EpochReached = Gate(); reached = h.Base.Js.EpochReached.Task; }
        var pending = h.Bind(); await reached.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(h.Controller.Busy); Assert.True(h.Controller.CanCancel);
        Assert.False(await h.Bind()); await h.Check(); Assert.Equal(0, h.Inspector.LayoutCalls);
        h.Controller.Cancel(); Assert.True(h.Controller.Busy); Assert.False(h.Controller.CanCancel); Assert.False(pending.IsCompleted);
        h.Inspector.InspectionHold?.TrySetResult(true); h.Base.Js.EpochGate?.TrySetResult(true);
        Assert.False(await pending); Assert.False(h.Controller.Busy); Assert.Empty(h.Controller.Annotations);
        Assert.Equal(0, h.Controller.AnnotationVersion);
    });

    [NativeRoleFact]
    public void Clearing_during_binding_does_not_release_the_task_or_publish_its_late_proof() => Run(async () =>
    {
        await using var h = await Host.Open(); h.Inspector.InspectionHold = Gate();
        var pending = h.Bind(); await h.Inspector.InspectionReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Controller.SelectAnnotations([]); Assert.True(h.Controller.Busy); Assert.False(pending.IsCompleted);
        h.Inspector.InspectionHold.TrySetResult(true); Assert.False(await pending);
        Assert.Empty(h.Controller.Annotations); Assert.True(h.Controller.AnnotationsCurrent);
    });

    [NativeRoleFact]
    public void Check_and_binding_share_reservation_and_review_cancel_keeps_valid_source_labels() => Run(async () =>
    {
        await using var h = await Host.Open(); Assert.True(await h.Bind());
        h.Inspector.LayoutHold = Gate(); var check = h.Check(); await h.Inspector.LayoutReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(h.Controller.Busy); Assert.False(await h.Bind(2));
        h.Controller.Cancel(); Assert.False(check.IsCompleted); Assert.True(h.Controller.Busy);
        h.Inspector.LayoutHold.TrySetResult(true); await check;
        Assert.False(h.Controller.Busy); Assert.Null(h.Controller.Report); Assert.True(h.Controller.AnnotationsCurrent);
        Assert.Equal(0, h.Evaluator.Calls);
    });

    [NativeRoleTheory]
    [InlineData("layout")][InlineData("evaluation")]
    public void Analysis_invalidation_during_check_cannot_publish_a_late_report(string stage) => Run(async () =>
    {
        await using var h = await Host.Open(); Assert.True(await h.Bind());
        if (stage == "layout") h.Inspector.LayoutHold = Gate(); else h.Evaluator.Hold = Gate();
        var check = h.Check();
        await (stage == "layout" ? h.Inspector.LayoutReached.Task : h.Evaluator.Reached.Task).WaitAsync(TimeSpan.FromSeconds(5));
        h.Base.Analysis.Cancel(); Assert.False(h.Controller.AnnotationsCurrent); Assert.False(check.IsCompleted);
        h.Inspector.LayoutHold?.TrySetResult(true); h.Evaluator.Hold?.TrySetResult(true); await check;
        Assert.Null(h.Controller.Report); Assert.False(h.Controller.ReportCurrent); Assert.False(h.Controller.CanCheck);
    });

    [NativeRoleFact]
    public void A_reset_analyzer_epoch_is_verified_before_check_and_retires_the_binding() => Run(async () =>
    {
        await using var h = await Host.Open(); Assert.True(await h.Bind()); h.Base.Js.CurrentEpoch++;
        await h.Check(); Assert.Equal(0, h.Inspector.LayoutCalls); Assert.Equal(0, h.Evaluator.Calls);
        Assert.False(h.Controller.AnnotationsCurrent); Assert.False(h.Controller.CanCheck); Assert.Null(h.Controller.Report);
    });

    [NativeRoleTheory]
    [InlineData("layout-runtime")][InlineData("layout-character")]
    public void A_later_layout_runtime_or_character_range_mismatch_refuses_before_evaluation(string fault) => Run(async () =>
    {
        await using var h = await Host.Open(); Assert.True(await h.Bind()); h.Inspector.Fault = fault;
        await h.Check(); Assert.Equal(0, h.Evaluator.Calls); Assert.Null(h.Controller.Report);
    });

    [NativeRoleFact]
    public void Legacy_constructor_keeps_author_labels_and_refuses_machine_binding_without_dependency() => Run(async () =>
    {
        await using var h = await Host.Open(binding: false); Assert.False(h.Controller.CanBindSourceEvent); Assert.False(await h.Bind());
        h.Controller.SelectAnnotations([Author("body")]); Assert.Equal("authorLabel", Assert.Single(h.Controller.Annotations).Provenance);
        Assert.Empty(h.Controller.SourceBindings); await h.Check(); Assert.True(h.Controller.ReportCurrent);
    });

    private static RenderedRoleAnnotation Author(string role) => new(0, role, 0, null, null, null, null, "authorLabel", "tentative");
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Host : IAsyncDisposable
    {
        internal Fixture Base = null!; internal Inspector Inspector = null!; internal readonly Evaluator Evaluator = new();
        internal CompiledReviewController Controller = null!; private readonly List<Task> _tasks = [];
        internal static async Task<Host> Open(bool binding = true)
        {
            var h = new Host(); h.Base = await Fixture.Open(decorate: inner => h.Inspector = new(inner));
            h.Inspector.Data = h.Base.Data; var owner = SynchronizationContext.Current!;
            Task Dispatch(Action action) { var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); owner.Post(_ => { try { action(); done.SetResult(); } catch (Exception e) { done.SetException(e); } }, null); return done.Task; }
            h.Controller = binding ? new(h.Base.Work.Workspace, h.Base.Session, h.Evaluator, Dispatch, h.Base.Binding)
                : new(h.Base.Work.Workspace, h.Base.Session, h.Evaluator, Dispatch);
            h.Controller.SelectWorkType("bachelorQualification"); return h;
        }
        internal Task<bool> Bind(int index = 0) { var task = Controller.BindSourceEventAsync(Base.Report, index); _tasks.Add(task); return task; }
        internal Task Check() { var task = Controller.CheckAsync(); _tasks.Add(task); return task; }
        public async ValueTask DisposeAsync()
        {
            Inspector.InspectionHold?.TrySetResult(true); Inspector.LayoutHold?.TrySetResult(true); Evaluator.Hold?.TrySetResult(true); Base.Js.EpochGate?.TrySetResult(true);
            await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(10)); Controller.Dispose(); await Base.DisposeAsync();
        }
    }
    private sealed class Inspector(IPdfInspector inner) : IPdfInspector, IPdfLayoutInspector
    {
        internal JsonObject Data = null!; internal int LayoutCalls; private long _epoch; internal string Fault = "";
        internal TaskCompletionSource<bool>? InspectionHold, LayoutHold;
        internal readonly TaskCompletionSource<bool> InspectionReached = Gate(), LayoutReached = Gate();
        public async Task<PdfInspectionOutcome> InspectAsync(PdfInspectRequest request, CancellationToken cancellationToken = default)
        {
            var outcome = await inner.InspectAsync(request, cancellationToken);
            if (InspectionHold is { } hold) { InspectionReached.TrySetResult(true); await hold.Task; }
            return outcome;
        }
        public async Task<PdfLayoutOutcome> InspectLayoutAsync(PdfLayoutRequest request, CancellationToken cancellationToken = default)
        {
            LayoutCalls++; LayoutReached.TrySetResult(true); if (LayoutHold is { } hold) await hold.Task;
            // Explicit modeled layout port, not native ink/style evidence. Copy
            // actual scalar indexes only; unsupported measurements remain null.
            var page = Data["pages"]![request.PageIndex]!; var actual = page["facts"]!;
            var characters = new JsonArray(page["text"]!["characters"]!.AsArray().Select(c => (JsonNode)new JsonObject {
                ["index"] = c!["index"]!.DeepClone(), ["unicode"] = c["unicode"]!.DeepClone(), ["origin"] = c["origin"]?.DeepClone(),
                ["advance"] = null, ["matrix"] = null, ["nominalSize"] = null, ["fontName"] = null, ["fontFlags"] = null,
                ["renderMode"] = null, ["inkBox"] = null, ["inkStatus"] = "unsupported", ["styleStatus"] = "unsupported" }).ToArray());
            var facts = new JsonObject { ["schemaVersion"] = 1, ["pageIndex"] = request.PageIndex, ["pageCount"] = Data["pages"]!.AsArray().Count,
                ["media"] = actual["media"]!.DeepClone(), ["crop"] = actual["crop"]?.DeepClone(), ["effective"] = actual["effective"]!.DeepClone(),
                ["rotation"] = actual["rotation"]!.DeepClone(), ["userUnit"] = new JsonObject { ["status"] = "unsupported", ["value"] = null, ["method"] = "unavailable" },
                ["characters"] = characters, ["complete"] = true, ["limitations"] = new JsonArray("glyph-ink-not-measured") };
            if (Fault == "layout-character") characters[10]!["unicode"] = 65;
            const string capabilities = "{\"schemaVersion\":1,\"coordinateSpace\":\"pdf-raw-unrotated-bottom-left\",\"physicalScale\":\"unsupported\",\"glyphInk\":\"unsupported\",\"textStyle\":\"unsupported\",\"lineGrouping\":\"unsupported\",\"maxCharacters\":100000,\"nominalSizeUnit\":\"raw-user-units\",\"nominalSizeMethod\":\"pdfium-font-size\"}";
            var parsed = PdfLayoutJson.Parse(capabilities, facts.ToJsonString(), request);
            return new(PdfInspectionStatus.Completed, Guid.NewGuid().ToString("N"), request.Document.Sha256, ++_epoch,
                new(Fault == "layout-runtime" ? new('0', 64) : Data["pdfRuntime"]!["loader"]!.GetValue<string>(), Data["pdfRuntime"]!["wasm"]!.GetValue<string>()), parsed.Capabilities, parsed.Facts);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class Evaluator : IRenderedReviewEvaluator
    {
        internal int Calls; internal TaskCompletionSource<bool>? Hold; internal readonly TaskCompletionSource<bool> Reached = Gate();
        public async Task<RenderedReviewEvaluationReply> EvaluateAsync(RenderedReviewRequest request, ReadOnlyMemory<byte> factsUtf8, CancellationToken cancellationToken = default)
        {
            Calls++; Reached.TrySetResult(true); if (Hold is { } hold) await hold.Task;
            using var stream = typeof(ControllerBindingTests).Assembly.GetManifestResourceStream("Workspace.Tests.Fixtures.RenderedReview.producer-v1.json")!;
            var response = JsonNode.Parse(stream)!["response"]!.DeepClone(); var p = response["payload"]!;
            var packed = JsonNode.Parse(RenderedReviewJson.Pack(request))!["payload"]!;
            foreach (var key in new[] { "annotationVersion", "captureId", "compileEpoch", "compileRequestId", "factsSha256", "inputDigest", "pageCount", "pdfSha256", "requestedPages", "workType" }) p[key] = packed[key]!.DeepClone();
            response["requestId"] = request.RequestId;
            // Model correlation/collection only; deliberately issue no normative
            // Pass/Fail from copied producer transport data.
            var paper = p["results"]!.AsArray().Single(r => r!["ruleId"]!.GetValue<string>() == "KNU-PAPER-001")!;
            paper["outcome"] = "unavailable"; paper["supportState"] = "unsupported"; paper["reason"] = "physical-scale-unavailable"; paper["evidence"] = new JsonArray();
            return new(RenderedExtentEvaluationStatus.Completed, request.RequestId, Calls, response.ToJsonString());
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
