using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Analysis.RenderedExtent;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Presentation;
using Client.Editing.Diagnostics;
using Client.Pdf;
using Client.Workspace;
using Client.Workspace.Domain;
using Xunit;
using PreviewPort = global::Workspace.Tests.PdfPreview.PreviewInspector;
using ExtentPort = global::Workspace.Tests.RenderedExtent.RenderedExtentControllerTests.Evaluator;
using static global::Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.Editing.Diagnostics;

// REAL coordinator/capture/analysis/lifecycle/presenter/log parser/navigation /
// extent controller and existing PDF wire fixture. Explicit SYNTHETIC analyzer,
// compiler/storage/inspector/evaluator ports, never native/browser acceptance.
internal sealed class DiagnosticTestHost : IAsyncDisposable
{
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    internal CompilationTestWorkspace Work = null!;
    internal WorkspaceCoordinator Workspace => Work.Workspace;
    internal SourcePort Analyzer = new();
    internal AnalysisCoordinator Analysis = null!;
    internal SourceNavigator Navigator = null!;
    internal Configuration Config = null!;
    internal CompileWorkspaceSession Compile = null!;
    internal int ForeignCompileNotifications;
    internal PreviewPort Inspector = new();
    internal ExtentPort Evaluator = new();
    internal RenderedExtentController Extent = null!;
    internal DiagnosticListSession List = null!;
    internal Task Dispatch(Action action) => _dispatch(action);
    private Func<Action, Task> _dispatch = null!;
    internal static async Task<DiagnosticTestHost> Open(bool readOnly = false)
    {
        var h = new DiagnosticTestHost { Work = await CompilationTestWorkspace.Open(readOnly: readOnly) };
        if (readOnly)
        {
            var r = h.Work.Repo.Revision!;
            h.Work.Repo.Revision = r with { Files = r.Files.Select(f => f.Id == h.Work.Main ? f with { RelativePath = "main.tex" } : f).ToImmutableArray() };
            h.Work.Repo.Remember(); await h.Workspace.CloseAsync(default); await h.Workspace.OpenAsync(h.Work.Repo.Project.Id, default);
        }
        else h.Workspace.RenameFile(h.Work.Main, "main.tex");
        var ui = SynchronizationContext.Current!;
        h._dispatch = action =>
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(_ => { try { action(); done.TrySetResult(); } catch (Exception e) { done.TrySetException(e); } }, null); return done.Task;
        };
        h.Analysis = new(h.Workspace, h.Analyzer, delayAsync: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        h.Navigator = new(h.Analysis, h.Workspace, h.Analyzer); h.Config = new(h.Inspector);
        h.Compile = new(h.Workspace, h.Config); h.Compile.AttachRenderer(h.Dispatch);
        int ownerThread = Environment.CurrentManagedThreadId;
        // Probe BEFORE the extent subscriber: an escaped foreign notification
        // must not disappear behind that subscriber's owner-affinity exception.
        h.Compile.Changed += () => { if (Environment.CurrentManagedThreadId != ownerThread) Interlocked.Increment(ref h.ForeignCompileNotifications); };
        await h.Compile.RefreshToolsAsync(); h.Compile.SelectMain(h.Work.Main); h.Compile.SelectEngine("diagnostic-contract");
        h.Extent = new(h.Workspace, h.Compile, h.Evaluator, h.Dispatch);
        h.List = new(h.Workspace, h.Analysis, h.Navigator, h.Compile, h.Extent);
        return h;
    }
    internal async Task<AnalysisReport> Analyze()
    {
        var t = new TaskCompletionSource<AnalysisReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = (Analysis.State as AnalysisState.Completed)?.Report;
        void Changed(AnalysisState state) { if (state is AnalysisState.Completed c && !ReferenceEquals(c.Report, previous)) t.TrySetResult(c.Report); }
        Analysis.Changed += Changed;
        try { Analysis.RequestReanalyze(); return await t.Task.WaitAsync(Bound); }
        finally { Analysis.Changed -= Changed; }
    }
    internal async Task CompileReply(CompileOutputStatus status = CompileOutputStatus.Failed, string log = "main.tex:2: Undefined control sequence.\nchapter.tex:1: Undefined control sequence.\n")
    {
        var prior = Compile.Attempt; var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() { if (Compile.Attempt is { } p && !ReferenceEquals(prior, p)) presented.TrySetResult(); }
        Compile.Changed += Changed;
        try
        {
            await Compile.CompileAsync(); var worker = await Config.Workers.Next();
            worker.Reply.TrySetResult(worker.Output(status) with { LogText = log });
            await Compile.LastTicket!.Completion.WaitAsync(Bound); await presented.Task.WaitAsync(Bound);
            await Compile.Preview.WhenSettled.WaitAsync(Bound);
        }
        finally { Compile.Changed -= Changed; }
    }
    internal async Task NarrowSections()
    {
        using var stream = typeof(DiagnosticTestHost).Assembly.GetManifestResourceStream("Workspace.Tests.Fixtures.RenderedSections.bachelor.json")!;
        var fixture = JsonNode.Parse(stream)!;
        var original = JsonSerializer.Deserialize<RenderedExtentRequest>(fixture["request"]!.ToJsonString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Inspector.PageCountOverride = original.PageCount;
        await CompileReply(CompileOutputStatus.Succeeded, "");
        // Production compile owner uses ProvidedPdf for the live accepted
        // artifact. A source intent/current preview ALONE is not extent evidence.
        Assert.Equal(Client.Pdf.Preview.PdfDisplayProvenance.ProvidedPdf, Compile.Preview.State.Provenance);
        Assert.False(List.Snapshot().Origins.Single(s => s.Origin == DiagnosticOrigin.Rendered).HasReport);
        Assert.DoesNotContain(List.Snapshot().Rows, r => r.HasPageAction);
        Extent.SelectWorkType(original.WorkType); Extent.SelectSections(original.Sections);
        Evaluator.Json = request =>
        {
            var reply = fixture["response"]!.DeepClone(); reply["requestId"] = request.RequestId; reply["pdfSha256"] = request.PdfSha256;
            return reply.ToJsonString(); // Existing Rust fixture; only correlation changes.
        };
        await Extent.CheckAsync(); Assert.NotNull(Extent.Report); Assert.True(Extent.ReportCurrent);
        Assert.True(Compile.State!.IsCurrent);
        Assert.Same(Compile.DisplayedOutput, Compile.LastTicket!.Completion.Result.Output);
        Assert.Equal(Extent.Report.Evidence.PdfSha256, Compile.Preview.State.DocumentSha256);
        Assert.Equal(Extent.Report.Observation.Request.PageCount, Compile.Preview.State.PageCount);
    }
    public async ValueTask DisposeAsync()
    {
        Analyzer.Release.TrySetResult(); Inspector.Handler = null; Evaluator.Hold?.TrySetResult(true);
        foreach (var g in Work.Repo.Gates) g.Release.TrySetResult(Work.Repo.Content[g.Hash]);
        List.Dispose(); Extent.Dispose(); await Analysis.DisposeAsync();
        foreach (var worker in Config.Workers.Created) worker.Reply.TrySetResult(worker.Output(CompileOutputStatus.Failed));
        await Compile.DisposeAsync(); if (Compile.Owner is { } owner) await owner.WhenSettled.WaitAsync(Bound);
        await Work.DisposeAsync();
    }
    internal sealed class Configuration(PreviewPort inspector) : ICompileWorkspaceConfiguration
    {
        internal readonly Workers Workers = new();
        internal int Opens;
        public Task<CompileWorkspaceConfigurationResult> OpenAsync()
        {
            Opens++;
            return Task.FromResult(new CompileWorkspaceConfigurationResult(new(true, "Ready", "Synthetic contract only"),
                new("diagnostic-contract", "Synthetic diagnostics", CompilationTestWorkspace.Descriptor(), [], [], Workers, inspector,
                    () => Task.FromResult(new CompileReadiness(true, "Ready", "Synthetic contract only")))));
        }
    }
    internal sealed class SourcePort : ISourceAnalyzer
    {
        internal int Count = 3, Calls, EpochCalls;
        internal long Epoch = 7;
        internal bool HoldEpoch;
        internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<AnalysisSnapshot, AnalysisResultDto>? Transform;
        public Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot snapshot, CancellationToken ct)
        {
            Calls++; var result = Transform?.Invoke(snapshot) ?? Result(snapshot, Count);
            return Task.FromResult<AnalysisJobResult>(new AnalysisJobResult.Completed("synthetic-diagnostic-report", Epoch, result));
        }
        public async Task<bool> IsCurrentWorkerEpochAsync(long epoch, CancellationToken ct)
        { EpochCalls++; if (HoldEpoch) { Reached.TrySetResult(); await Release.Task; } return epoch == Epoch; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        // Same controlled structural DTO shape as existing AnalysisNavigationTests.
        // No rule evaluation/source parser is being implemented by this fixture.
        internal static AnalysisResultDto Result(AnalysisSnapshot s, int count)
        {
            var f = s.Files[0]; var diagnostics = new List<AnalysisEngineDiagnosticDto>();
            for (int i = 0; i < count; i++) diagnostics.Add(new("synthetic-" + i, i == 0 || i >= 3 ? "ENGINE-STRUCT-001" : "SYNTHETIC-UNLOCATED-" + i,
                "engine", i == 1 ? "info" : i == 2 ? "error" : "warning", i == 2 ? FileId.New().ToString() : f.FileId.ToString(),
                i == 1 ? null : new(i == 2 ? 0 : 17, i == 2 ? 1 : 21), "<img src=x onerror=alert(1)> synthetic Україна🙂 " + i, new(2, 0)));
            return new(s.Id.ToString(), new(s.Profile.Id, s.Profile.Version),
                new(s.Files.Select(file => new AnalysisSourceFileFactsDto(file.FileId.ToString(), file.ByteLength, file.HasBom, NewlineKind.Mixed,
                    false, false, 0, RuleOutcome.Indeterminate, [])).ToArray()), diagnostics,
                [new("ENGINE-STRUCT-001", "engine", RuleOutcome.Indeterminate, [], ["synthetic-uncertainty"])], RuleOutcome.Indeterminate,
                new([], ["synthetic-unsupported"], [], false, false), ["pdfFacts", "pdfChecks"], s.Configuration, ReferenceConfiguration: s.ReferenceConfiguration);
        }
    }
}
