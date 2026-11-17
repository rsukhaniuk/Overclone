using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Analysis.RenderedReview;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Compilation.SyncTeX;
using Client.Pdf;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.RenderedRoleBinding;

// Managed authority/replay checks over separately executed genuine native and
// Rust fixtures. Not a fresh compiler/query, UI, offline or normative rule test.
public sealed class NativeRoleFactAttribute : FactAttribute
{
    public NativeRoleFactAttribute()
    { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RENDERED_ROLE_BINDING_FIXTURE"))) Skip = "Exact separately executed native/Rust role fixture not supplied."; }
}
public sealed class NativeRoleTheoryAttribute : TheoryAttribute
{
    public NativeRoleTheoryAttribute()
    { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RENDERED_ROLE_BINDING_FIXTURE"))) Skip = "Exact separately executed native/Rust role fixture not supplied."; }
}

public sealed class RoleBindingTests
{
    [NativeRoleFact]
    public void Exact_native_caption_ranges_preserve_CRLF_Unicode_preimages_and_never_measure_ink() => Run(async () =>
    {
        await using var h = await Host.Open(); var before = h.Work.State();
        foreach (var (index, role, start, end, line) in new[] { (0, "figureCaption", 10, 26, 6), (2, "tableCaption", 37, 54, 9) })
        {
            var result = await h.Binding.BindAsync(h.Request(index));
            Assert.True(result.Value is not null, result.Code);
            var proof = Assert.IsType<Client.Analysis.RenderedReview.RenderedRoleBinding.Proof>(result.Value);
            Assert.Equal("bound-literal-character-range", result.Code); Assert.Equal(role, proof.Annotation.Role);
            Assert.Equal(start, proof.Annotation.CharacterStart); Assert.Equal(end, proof.Annotation.CharacterEnd);
            Assert.Equal("literalSourceAndNativeMap", proof.Annotation.Provenance); Assert.Equal("confirmedObservation", proof.Annotation.Certainty);
            Assert.Equal("captionText", proof.RangeKind); Assert.False(proof.GlyphInkMeasured);
            Assert.Equal("native-character-box-map-correlation", proof.GeometryMethod); Assert.Equal(line, proof.SourceLine);
            Assert.Same(h.Session.DisplayedOutput, proof.Output); Assert.Same(h.Report, proof.Report);
            Assert.Equal(h.Report.Result.Thesis!.Events[index].Span, proof.Annotation.SourceSpan);
            Assert.True(h.Binding.IsCurrent(proof, "bachelorQualification"));
            Assert.True(await h.Binding.VerifyCurrentAsync(proof, "bachelorQualification"));
            Assert.False(h.Binding.IsCurrent(proof, "masterQualification"));
        }
        Assert.Equal(before, h.Work.State()); Assert.Equal(0, h.Work.Repo.Commits);
        Assert.Equal(2, h.Inspector.Calls); Assert.Equal(6, h.Queries.Calls);
    });

    [NativeRoleTheory]
    [InlineData("wrong-work-type")][InlineData("cloned-report")][InlineData("wrong-event-index")]
    [InlineData("no-role-event")][InlineData("cancelled")][InlineData("worker-reset")]
    public void Invalid_or_noncanonical_requests_never_publish_roles(string kind) => Run(async () =>
    {
        await using var h = await Host.Open(); var request = h.Request(); using var stop = new CancellationTokenSource();
        switch (kind)
        {
            case "wrong-work-type": request = request with { WorkType = "masterQualification" }; break;
            case "cloned-report": request = request with { Report = request.Report with { } }; break;
            case "wrong-event-index": request = request with { EventIndex = -1 }; break;
            case "no-role-event": request = request with { EventIndex = 1 }; break;
            case "cancelled": stop.Cancel(); break;
            case "worker-reset": h.Js.CurrentEpoch++; break;
        }
        Assert.Null((await h.Binding.BindAsync(request, stop.Token)).Value);
        Assert.Equal(0, h.Inspector.Calls); Assert.Equal(0, h.Queries.Calls);
    });

    [NativeRoleTheory]
    [InlineData("source-edit")][InlineData("source-ABA")][InlineData("samehash-compile")]
    [InlineData("analysis-cancel")][InlineData("analysis-root")][InlineData("analysis-work-type")]
    [InlineData("mutable-import")]
    public void Issued_proof_retires_on_exact_owner_or_selection_change(string kind) => Run(async () =>
    {
        await using var h = await Host.Open(); var bound = await h.Binding.BindAsync(h.Request());
        Assert.True(bound.Value is not null, bound.Code); var proof = bound.Value!;
        switch (kind)
        {
            case "source-edit": Assert.True(h.Work.Workspace.EditText(h.Main, "changed")); break;
            case "source-ABA":
                var text = h.Work.Workspace.Files.Single(f => f.Id == h.Main).Content;
                Assert.True(h.Work.Workspace.EditText(h.Main, "changed")); Assert.True(h.Work.Workspace.EditText(h.Main, text)); break;
            case "samehash-compile": await h.Compile(); break;
            case "analysis-cancel": h.Analysis.Cancel(); break;
            case "analysis-root": h.Analysis.SetSelection(AnalysisSelection.KnuWithThesis(AnalysisWorkType.BachelorQualification, h.Chapter.ToString())); break;
            case "analysis-work-type": h.Analysis.SetSelection(AnalysisSelection.KnuWithThesis(AnalysisWorkType.MasterQualification, h.Main.ToString())); break;
            case "mutable-import": h.Work.Workspace.Files.Single(f => f.Id == h.Main).ImportedBytes![0] ^= 1; break;
        }
        Assert.False(h.Binding.IsCurrent(proof, "bachelorQualification"));
        Assert.False(await h.Binding.VerifyCurrentAsync(proof, "bachelorQualification"));
    });

    [NativeRoleTheory]
    [InlineData("duplicate-source-text")][InlineData("unknown-macro")]
    [InlineData("included-generic-heading")][InlineData("unknown-package")]
    public void Actual_Rust_uncertainty_and_duplicate_events_refuse_before_native_query(string variant) => Run(async () =>
    {
        // Real Rust responses over exact changed bytes; compile/inspect ports
        // remain deliberately modeled. No native compile of a variant is claimed.
        await using var h = await Host.Open(variant);
        Assert.Null((await h.Binding.BindAsync(h.Request())).Value);
        Assert.Equal(0, h.Queries.Calls); Assert.Equal(0, h.Inspector.Calls);
    });

    [NativeRoleFact]
    public void Cancelling_the_original_binding_token_retires_an_issued_proof() => Run(async () =>
    {
        await using var h = await Host.Open(); using var stop = new CancellationTokenSource();
        var result = await h.Binding.BindAsync(h.Request(), stop.Token); Assert.NotNull(result.Value);
        stop.Cancel(); Assert.False(h.Binding.IsCurrent(result.Value!, "bachelorQualification"));
    });

    [NativeRoleFact]
    public void Copying_a_proof_does_not_mint_new_authority() => Run(async () =>
    {
        await using var h = await Host.Open(); var proof = (await h.Binding.BindAsync(h.Request())).Value!; Assert.NotNull(proof);
        var clone = (Client.Analysis.RenderedReview.RenderedRoleBinding.Proof)typeof(object)
            .GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(proof, null)!;
        Assert.False(h.Binding.IsCurrent(clone, "bachelorQualification"));
        Assert.False(await h.Binding.VerifyCurrentAsync(clone, "bachelorQualification"));
        Assert.True(h.Binding.IsCurrent(proof, "bachelorQualification"));
    });

    [NativeRoleTheory]
    [InlineData("forward-line")][InlineData("forward-file")][InlineData("forward-box")]
    [InlineData("inverse-line")][InlineData("query-truncated")][InlineData("pdf-hash")]
    [InlineData("duplicated-pdf-text")][InlineData("glyph-index")][InlineData("missing-box")]
    [InlineData("inspection-id")]
    [InlineData("stale-during-inspection")][InlineData("cancel-during-inspection")]
    public void Ambiguous_or_mismatched_native_evidence_cannot_supply_source_span_precision(string fault) => Run(async () =>
    {
        await using var h = await Host.Open(); using var stop = new CancellationTokenSource();
        h.Queries.Fault = fault; h.Inspector.Fault = fault;
        if (fault == "stale-during-inspection") h.Inspector.During = () => h.Work.Workspace.EditText(h.Main, "changed");
        if (fault == "cancel-during-inspection") h.Inspector.During = stop.Cancel;
        Assert.Null((await h.Binding.BindAsync(h.Request(), stop.Token)).Value);
    });

    [NativeRoleFact]
    public void Different_exact_compile_capture_bytes_refuse_even_when_report_and_workspace_are_current() => Run(async () =>
    {
        await using var h = await Host.Open();
        // A deliberately invalid owned fixture capture, never claimed native output.
        // The current canonical report cannot bless a mismatching capture preimage.
        var output = h.Session.DisplayedOutput!; var input = output.Snapshot.Files.Single(f => f.FileId == h.Main);
        var field = typeof(CompilationInputFile).GetField("_bytes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var bytes = (byte[])field.GetValue(input)!; bytes[0] ^= 1;
        Assert.Null((await h.Binding.BindAsync(h.Request())).Value); Assert.Equal(0, h.Queries.Calls);
    });

    private sealed class Configuration(Host h) : ICompileWorkspaceConfiguration
    {
        public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(new(true, "Ready", "Native fixture replay"),
                new("roles", "Native fixture replay", CompilationTestWorkspace.Descriptor("pdflatex"), [], [], h.Workers, h.ConfiguredInspector,
                () => Task.FromResult(new CompileReadiness(true, "Ready", "Replay")), SelectionOptions: NativeSelectedEntryContract.SelectionOptions)));
    }
    internal sealed class Host : IAsyncDisposable
    {
        internal CompilationTestWorkspace Work = null!;
        internal readonly Workers Workers = new(); internal Inspector Inspector = null!; internal Queries Queries = null!;
        internal IPdfInspector ConfiguredInspector = null!;
        internal CompileWorkspaceSession Session = null!; internal SyncTeXNavigationPolicy Navigation = null!;
        internal AnalysisCoordinator Analysis = null!; internal ThesisSource.ThesisTestHost.RecordingAnalyzer Analyzer = null!;
        internal KnuProtocolTests.FixtureJs Js = null!; internal AnalysisReport Report = null!;
        internal Client.Analysis.RenderedReview.RenderedRoleBinding Binding = null!;
        internal JsonObject Data = null!; internal byte[] Pdf = [], Map = []; internal FileId Main, Chapter;
        internal RenderedRoleBindingRequest Request(int index = 0) => new(Report, index, index + 1, "bachelorQualification");
        internal static async Task<Host> Open(string? variant = null, Func<IPdfInspector, IPdfInspector>? decorate = null)
        {
            string folder = Environment.GetEnvironmentVariable("RENDERED_ROLE_BINDING_FIXTURE")!;
            string sourceFolder = variant is null ? folder : Path.Combine(folder, "variants", variant);
            var h = new Host { Work = await CompilationTestWorkspace.Open() };
            h.Data = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "native-fixture.json")))!.AsObject();
            h.Pdf = File.ReadAllBytes(Path.Combine(folder, "native01/output.pdf")); h.Map = File.ReadAllBytes(Path.Combine(folder, "native01/raw.synctex"));
            Assert.Equal(h.Data["pdf"]!["sha256"]!.GetValue<string>(), PdfHashes.Of(h.Pdf));
            Assert.Equal(h.Data["map"]!["sha256"]!.GetValue<string>(), PdfHashes.Of(h.Map));
            foreach (var file in h.Work.Workspace.Files.ToArray()) Assert.True(h.Work.Workspace.DeleteFile(file.Id));
            foreach (var name in new[] { "тези/my thesis.tex", "chapter.tex" })
                Assert.True(h.Work.Workspace.CreateFileFromBytes(name, File.ReadAllBytes(Path.Combine(sourceFolder, "project", name))));
            h.Main = h.Work.Workspace.Files.Single(f => !f.Deleted && f.RelativePath == "тези/my thesis.tex").Id;
            h.Chapter = h.Work.Workspace.Files.Single(f => !f.Deleted && f.RelativePath == "chapter.tex").Id;
            h.Inspector = new(h); h.ConfiguredInspector = decorate?.Invoke(h.Inspector) ?? h.Inspector;
            h.Queries = new(h); h.Session = new(h.Work.Workspace, new Configuration(h)); var owner = SynchronizationContext.Current!;
            h.Session.AttachRenderer(action => { var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); owner.Post(_ => { try { action(); done.SetResult(); } catch (Exception e) { done.SetException(e); } }, null); return done.Task; });
            await h.Session.RefreshToolsAsync(); h.Session.SelectMain(h.Main); h.Session.SelectEngine("roles");
            h.Navigation = new(h.Work.Workspace, h.Session, h.Queries);
            var responseBytes = File.ReadAllBytes(Path.Combine(sourceFolder, "response.json"));
            if (variant is null) Assert.Equal(h.Data["rustResponse"]!.GetValue<string>(), PdfHashes.Of(responseBytes));
            var response = JsonNode.Parse(responseBytes)!.AsObject();
            h.Js = new KnuProtocolTests.FixtureJs((meta, rid) =>
            {
                var snapshot = h.Analyzer.Captured.Single(s => s.Id.ToString() == JsonNode.Parse(meta)!["payload"]!["snapshotId"]!.GetValue<string>());
                return ThesisSource.ThesisFixture.Rebind(response.DeepClone().AsObject(), snapshot, rid,
                    new() { ["f0"] = h.Main.ToString(), ["f1"] = h.Chapter.ToString() });
            });
            h.Analyzer = new(new RustSourceAnalyzer(h.Js)); h.Analysis = new(h.Work.Workspace, h.Analyzer, delayAsync: (_, ct) => Task.Delay(Timeout.Infinite, ct));
            var completed = new TaskCompletionSource<AnalysisReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Analysis.Changed += state => { if (state is AnalysisState.Completed c) completed.TrySetResult(c.Report); };
            h.Analysis.SetSelection(AnalysisSelection.KnuWithThesis(AnalysisWorkType.BachelorQualification, h.Main.ToString()));
            h.Report = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await h.Compile(); h.Inspector.Calls = 0;
            h.Binding = new(h.Work.Workspace, h.Session, new(h.Analysis, h.Work.Workspace, h.Analyzer), h.Navigation); return h;
        }
        internal async Task Compile()
        {
            var previous = Session.DisplayedOutput; var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed() { if (Session.DisplayedOutput is { } output && !ReferenceEquals(previous, output)) shown.TrySetResult(); }
            Session.Changed += Changed;
            try
            {
                await Session.CompileAsync(); var worker = await Workers.Next();
                worker.Reply.TrySetResult(worker.Output(bytes: Pdf) with { SourceMapping = new(CompilationTestWorkspace.Hash(Map), Map.Length), SourceMapPayload = Map });
                Assert.Equal(CompileLifecycleReason.Succeeded, (await Session.LastTicket!.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
                await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { Session.Changed -= Changed; }
        }
        public async ValueTask DisposeAsync()
        {
            Js.EpochGate?.TrySetResult(true); Navigation.Dispose(); await Analysis.DisposeAsync(); await Analyzer.DisposeAsync();
            foreach (var worker in Workers.Created) worker.Succeed(); await Session.DisposeAsync();
            if (Session.Owner is { } owner) await owner.WhenSettled.WaitAsync(TimeSpan.FromSeconds(5)); await Work.DisposeAsync();
        }
    }
    internal sealed class Inspector(Host h) : IPdfInspector
    {
        internal int Calls; internal string Fault = ""; internal Action? During;
        public Task<PdfInspectionOutcome> InspectAsync(PdfInspectRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Assert.Equal(PdfHashes.Of(h.Pdf), request.Document.Sha256); Assert.False(request.Raster);
            var page = h.Data["pages"]![request.PageIndex]!.DeepClone(); var text = page!["text"]!.AsObject();
            if (Fault == "duplicated-pdf-text")
            {
                var chars = text["characters"]!.AsArray(); var original = chars.Select(c => c!.DeepClone()).ToArray();
                foreach (var c in original) { c["index"] = chars.Count; chars.Add(c); }
                string all = text["text"]!.GetValue<string>(); text["text"] = all + all;
                var utf = text["utf16"]!.AsArray(); foreach (var u in utf.Select(u => u!.DeepClone()).ToArray()) utf.Add(u);
                text["characterCount"] = chars.Count;
            }
            if (Fault == "glyph-index") text["characters"]![10]!["unicode"] = 0xd800;
            if (Fault == "missing-box") { text["characters"]![10]!["box"] = null; text["characters"]![10]!["boxStatus"] = "indeterminate"; }
            var runtime = new PdfRuntimeIdentity(h.Data["pdfRuntime"]!["loader"]!.GetValue<string>(), h.Data["pdfRuntime"]!["wasm"]!.GetValue<string>());
            string id = Fault == "inspection-id" ? "not-a-32-hex-request-id" : "4fb46b1c9aa54ffb91b60138bc6e709b";
            var wire = new PdfWireReply { Version = 2, RequestId = id, DocumentSha256 = Fault == "pdf-hash" ? new('0', 64) : request.Document.Sha256,
                Epoch = 1, LoaderSha256 = runtime.LoaderSha256, WasmSha256 = runtime.WasmSha256, Status = "completed", NativeStatus = 0, EnteredNative = true,
                ActionState = "not-requested",
                PageCount = h.Data["pages"]!.AsArray().Count, FactsJson = page["facts"]!.ToJsonString(), TextJson = text.ToJsonString(), SearchJson = "[]",
                NativeCalls = ["e1_version", "e1_open", "e1_page", "e1_facts", "e1_text", "e1_reset"] };
            var outcome = PdfWireParser.Parse(wire, request, id, 1, runtime); During?.Invoke(); return Task.FromResult(outcome);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal sealed class Queries(Host h) : ISyncTeXQuery
    {
        internal int Calls; internal string Fault = "";
        public Task<SyncTeXQueryOutcome> QueryAsync(SyncTeXQueryRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Assert.Equal(h.Map, request.Map); Assert.Equal(PdfHashes.Of(h.Map), request.MapSha256);
            JsonNode reply;
            if (request.Direction == SyncTeXDirection.Forward)
            {
                var row = h.Data["rows"]!.AsArray().Single(r => r!["file"]!.GetValue<string>() == request.File && r["line"]!.GetValue<int>() == request.Line)!;
                reply = row["forward"]!;
            }
            else
            {
                var row = h.Data["rows"]!.AsArray().SelectMany(r => r!["inverses"]!.AsArray()).Single(r =>
                    r!["page"]!.GetValue<int>() == request.Page && r["h"]!.GetValue<double>() == request.H && r["v"]!.GetValue<double>() == request.V)!;
                reply = row["reply"]!;
            }
            var matches = reply["matches"]!.AsArray().Select(n =>
            {
                var p = n!["point"]!; var b = n["box"]!;
                var m = new SyncTeXMatch(n["file"]!.GetValue<string>(), n["tag"]!.GetValue<int>(), n["line"]!.GetValue<int>(),
                    n["column"]?.GetValue<int>(), n["page"]!.GetValue<int>(), new(p["h"]!.GetValue<double>(), p["v"]!.GetValue<double>()),
                    new(b["h"]!.GetValue<double>(), b["v"]!.GetValue<double>(), b["width"]!.GetValue<double>(), b["height"]!.GetValue<double>(), b["depth"]!.GetValue<double>()));
                if (Fault == "forward-line" && request.Direction == SyncTeXDirection.Forward) m = m with { Line = m.Line + 1 };
                if (Fault == "forward-file" && request.Direction == SyncTeXDirection.Forward) m = m with { File = "chapter.tex" };
                if (Fault == "forward-box" && request.Direction == SyncTeXDirection.Forward) m = m with { Box = m.Box with { H = 900 } };
                if (Fault == "inverse-line" && request.Direction == SyncTeXDirection.Inverse) m = m with { Line = m.Line + 1 };
                return m;
            }).ToArray();
            return Task.FromResult(new SyncTeXQueryOutcome(SyncTeXQueryStatus.Completed, matches, Fault == "query-truncated"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
