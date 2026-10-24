using System.Collections.Immutable;
using Client.Analysis.RenderedExtent;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Pdf;
using Client.Pages;
using Client.Workspace.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.RenderedExtent;

// REAL workspace/capture/accepted output/lifecycle/session and PdfWireParser;
// explicit SYNTHETIC storage/compiler/inspector/evaluator ports. No native/Rust/
// browser acceptance is claimed. Background preview calls are isolated by their
// existing 25s policy; the extent request retains the default 30s inspector cap.
public sealed class RenderedExtentControllerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    internal sealed class Inspector : IPdfInspector
    {
        internal int Calls, Disposals, PreviewCalls, Pages = 1;
        internal long Epoch;
        internal PdfInspectRequest? Last;
        internal TaskCompletionSource<bool>? Hold;
        internal TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<PdfInspectionOutcome, PdfInspectionOutcome>? Transform;
        public async Task<PdfInspectionOutcome> InspectAsync(PdfInspectRequest request, CancellationToken ct = default)
        {
            if (request.Budget.DeadlineMs == 25000)
            { PreviewCalls++; return new(PdfInspectionStatus.Unavailable, Guid.NewGuid().ToString("N"), request.Document.Sha256, 1); }
            Calls++; Last = request; var result = Valid(request, ++Epoch, Pages);
            Reached.TrySetResult();
            if (Hold is { } held) await held.Task; // Intentionally noncooperative cancellation.
            return Transform?.Invoke(result) ?? result;
        }
        internal static PdfInspectionOutcome Valid(PdfInspectRequest request, long epoch, int pages = 1, int pageIndex = 0)
        {
            var id = Guid.NewGuid().ToString("N"); var runtime = new PdfRuntimeIdentity(new('c', 64), new('d', 64));
            var interpreted = new PdfInspectRequest(request.Document, pageIndex, request.Budget, raster: false, rasterWidth: 1, rasterHeight: 1);
            return PdfWireParser.Parse(new PdfWireReply
            {
                Version = 2, RequestId = id, Epoch = epoch, DocumentSha256 = request.Document.Sha256,
                LoaderSha256 = runtime.LoaderSha256, WasmSha256 = runtime.WasmSha256,
                Status = "completed", NativeStatus = 0, EnteredNative = true, PageCount = pages, ActionState = "not-requested",
                NativeCalls = ["e1_version", "e1_open", "e1_page", "e1_facts", "e1_text", "e1_reset"],
                FactsJson = "{\"media\":[0,0,100,100],\"crop\":null,\"cropDefaulted\":true,\"effective\":[0,0,100,100],\"rotation\":0,\"displayPoints\":[100,100],\"nativeWidthHeight\":[100,100],\"sourceMapping\":\"unsupported\"}",
                TextJson = "{\"text\":\"\",\"utf16\":[],\"characterCount\":0,\"characters\":[],\"sourceMapping\":\"unsupported\"}", SearchJson = "[]"
            }, interpreted, id, epoch, runtime);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    internal sealed class Evaluator : IRenderedExtentEvaluator
    {
        internal int Calls, Disposals;
        internal long Epoch;
        internal RenderedExtentRequest? Last;
        internal TaskCompletionSource<bool>? Hold;
        internal TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<RenderedExtentRequest, string> Json = r => ExtentFixtureJson.Body(r);
        internal Func<RenderedExtentEvaluationReply, RenderedExtentEvaluationReply>? Transform;
        public async Task<RenderedExtentEvaluationReply> EvaluateAsync(RenderedExtentRequest request, CancellationToken ct = default)
        {
            Calls++; Last = request; long epoch = ++Epoch; Reached.TrySetResult();
            if (Hold is { } held) await held.Task; // Intentionally noncooperative.
            var result = new RenderedExtentEvaluationReply(RenderedExtentEvaluationStatus.Completed, request.RequestId, epoch, Json(request));
            return Transform?.Invoke(result) ?? result;
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    internal sealed class Configuration(Inspector? inspector, bool localAuthoringV4 = false, CompileOutputDescriptor? adapter = null) : ICompileWorkspaceConfiguration
    {
        internal readonly Workers Workers = new();
        internal string CapabilityId => localAuthoringV4 ? "local-authoring-v4" : "contract";
        public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(
            new(true, "Ready", "Synthetic compiler"), new(CapabilityId, "Contract only", CompilationTestWorkspace.Descriptor(), [],
                localAuthoringV4 ? [CompilationTestWorkspace.Descriptor("bibtex")] : [],
                Workers, inspector, () => Task.FromResult(new CompileReadiness(true, "Ready", "Contract only")),
                SelectableBibliography: localAuthoringV4,
                SelectionOptions: localAuthoringV4 ? NativeAuthoringV4Contract.SelectionOptions : null, OutputAdapter: adapter)));
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        internal CompilationTestWorkspace Work = null!;
        internal CompileWorkspaceSession Session = null!;
        internal readonly Inspector Inspector = new();
        internal readonly Evaluator Evaluator = new();
        internal Configuration Config = null!;
        internal RenderedExtentController Controller = null!;
        internal Task? Checking;
        internal static async Task<Fixture> Open(bool inspector = true, Clock? clock = null, bool readOnly = false, bool localAuthoringV4 = false,
            CompileOutputDescriptor? adapter = null)
        {
            var f = new Fixture { Work = await CompilationTestWorkspace.Open(readOnly: readOnly) };
            if (readOnly)
            {
                var revision = f.Work.Repo.Revision!;
                f.Work.Repo.Revision = revision with { Files = revision.Files.Select(file => file.RelativePath == "entry"
                    ? file with { RelativePath = "main.tex" } : file).ToImmutableArray() };
                f.Work.Repo.Remember();
                await f.Work.Workspace.CloseAsync(default); await f.Work.Workspace.OpenAsync(f.Work.Repo.Project.Id, default);
            }
            else Assert.True(f.Work.Workspace.RenameFile(f.Work.Main, "main.tex"));
            f.Config = new(inspector ? f.Inspector : null, localAuthoringV4, adapter);
            var ui = SynchronizationContext.Current!;
            Task Dispatch(Action action)
            {
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ui.Post(_ => { try { action(); signal.SetResult(); } catch (Exception e) { signal.SetException(e); } }, null);
                return signal.Task;
            }
            f.Session = new(f.Work.Workspace, f.Config); f.Session.AttachRenderer(Dispatch);
            await f.Session.RefreshToolsAsync(); f.Session.SelectMain(f.Work.Main); f.Session.SelectEngine(f.Config.CapabilityId);
            f.Controller = new(f.Work.Workspace, f.Session, f.Evaluator, Dispatch, clock);
            return f;
        }
        internal async Task Compile(CompileOutputStatus status = CompileOutputStatus.Succeeded)
        {
            var prior = Session.Attempt; var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed() { if (Session.Attempt is { } attempt && !ReferenceEquals(prior, attempt)) presented.TrySetResult(); }
            Session.Changed += Changed;
            try
            {
                await Session.CompileAsync(); var worker = await Config.Workers.Next();
                worker.Reply.TrySetResult(worker.Output(status));
                Assert.Equal(status == CompileOutputStatus.Succeeded ? CompileLifecycleReason.Succeeded : CompileLifecycleReason.CompilerFailed,
                    (await Session.LastTicket!.Completion.WaitAsync(Bound)).Reason);
                await presented.Task.WaitAsync(Bound);
            }
            finally { Session.Changed -= Changed; }
        }
        internal Task Check() => Checking = Controller.CheckAsync();
        public async ValueTask DisposeAsync()
        {
            Controller.Dispose(); Inspector.Hold?.TrySetResult(true); Evaluator.Hold?.TrySetResult(true);
            if (Checking is { } check) await check.WaitAsync(Bound);
            foreach (var worker in Config.Workers.Created) worker.Reply.TrySetResult(worker.Output(CompileOutputStatus.Failed));
            await Session.DisposeAsync(); if (Session.Owner is { } owner) await owner.WhenSettled.WaitAsync(Bound);
            await Work.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("legacy")][InlineData("synctex")][InlineData("dvi")][InlineData("unknown")]
    public void Only_explicit_supported_current_adapters_enable_rendered_checks(string kind) => Run(async () =>
    {
        var adapter = kind switch
        {
            "legacy" => NativeRuntimePackage.Adapter, "synctex" => NativeSyncTeXContract.OutputAdapter,
            "dvi" => NativeLatexDviContract.OutputAdapter,
            _ => new CompileOutputDescriptor("foreign", "1", null)
        };
        await using var f = await Fixture.Open(adapter: adapter);
        await f.Compile();
        Assert.Equal(kind != "unknown", f.Controller.CanCheck);
        await f.Check();
        Assert.Equal(kind != "unknown", f.Controller.ReportCurrent);
        Assert.Equal(kind == "unknown" ? 0 : 1, f.Inspector.Calls);
        Assert.Equal(kind == "unknown" ? 0 : 1, f.Evaluator.Calls);
        Assert.Equal(0, f.Work.Repo.Commits);
    });

    [Fact] public void First_async_local_v4_shaped_compile_notifies_idle_child_view_that_check_became_available() => Run(async () =>
    {
        // Real session/capture/lifecycle + actual production capability ID,
        // optional-BibTeX shape and v4 selection option. Worker/storage and
        // engine descriptor remain synthetic; not actual native provisioning.
        await using var f = await Fixture.Open(localAuthoringV4: true);
        Assert.Equal(CommitStatus.Saved, (await f.Work.Workspace.SaveAsync(default)).Status);
        Assert.False(f.Controller.CanCheck); Assert.Null(f.Controller.Report);
        bool renderedEnabled = false;
        int notifications = 0;
        var renders = new List<Task>();
        var ui = SynchronizationContext.Current!;
        var enabledView = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Presented()
        {
            if (f.Session.DisplayedOutput is not null && f.Session.Attempt?.Attempt is { Status: CompileOutputStatus.Succeeded })
                presented.TrySetResult();
        }
        void RenderFromNotification()
        {
            notifications++;
            // Like the parameterless child: its view updates ONLY on the
            // controller's Changed notification, not by polling CanCheck.
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            renders.Add(rendered.Task);
            try
            {
                ui.Post(_ =>
                {
                    try
                    {
                        renderedEnabled = f.Controller.CanCheck;
                        if (renderedEnabled) enabledView.TrySetResult();
                        rendered.TrySetResult();
                    }
                    catch (Exception error) { rendered.TrySetException(error); }
                }, null);
            }
            catch (Exception error) { rendered.TrySetException(error); }
        }
        f.Controller.Changed += RenderFromNotification;
        f.Session.Changed += Presented;
        try
        {
            await f.Session.CompileAsync(); var worker = await f.Config.Workers.Next();
            Assert.Equal(NativeAuthoringV4Contract.SelectionOptions, worker.Context.Snapshot.Selection.Options);
            Assert.Empty(worker.Context.Snapshot.Selection.Tools); // Plain local authoring, no implicit BibTeX.
            await Task.Yield(); // Complete an actually pending compile response, not an inline fake-ready property.
            worker.Reply.TrySetResult(worker.Output());
            Assert.Equal(CompileLifecycleReason.Succeeded, (await f.Session.LastTicket!.Completion.WaitAsync(Bound)).Reason);
            await presented.Task.WaitAsync(Bound); // Ticket settlement is not yet a presented UI state.
            // Fresh admission can be true even when the old idle view remained
            // disabled: this distinguishes notification failure from weakening
            // an identity/format/adapter/head/selection predicate.
            Assert.True(f.Controller.CanCheck);
            await enabledView.Task.WaitAsync(Bound);
            Assert.True(renderedEnabled); Assert.True(notifications > 0);
            Assert.Equal(RenderedExtentCheckState.Idle, f.Controller.State); Assert.Null(f.Controller.Report);
            Assert.Equal(0, f.Inspector.Calls); Assert.Equal(0, f.Evaluator.Calls); // Notification did NOT start a check.
        }
        finally
        {
            f.Controller.Changed -= RenderFromNotification; f.Session.Changed -= Presented;
            await Task.WhenAll(renders).WaitAsync(Bound); // Drain every queued view callback before fixture/owner teardown.
        }
    });

    [Fact] public void Actual_child_view_render_await_never_posts_back_to_borrowed_compile_owner() => Run(async () =>
    {
        await using var f = await Fixture.Open();
        var parent = SynchronizationContext.Current!;
        int ownerWakeups = 0;
        var owner = new CompileOwnerContext(action =>
        {
            Interlocked.Increment(ref ownerWakeups);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            parent.Post(_ => { try { action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } }, null);
            return done.Task;
        });
        // Real compiled component, renderer and lifecycle methods. No markup
        // string mirror, fake disposed predicate, or global owner modification.
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IJSRuntime>(new NoJsForView())
            .AddSingleton(f.Work.Workspace).AddSingleton(f.Session).BuildServiceProvider();
        await using var renderer = new HeldViewRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var component = (RenderedExtentPanel)renderer.Create(typeof(RenderedExtentPanel));
        renderer.Attach(component);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(RenderedExtentPanel).GetMethod("OnInitialized", flags)!.Invoke(component, null);
        Task? render = null;
        try
        {
            render = owner.Run(() =>
            {
                Assert.Same(owner, SynchronizationContext.Current);
                return (Task)typeof(RenderedExtentPanel).GetMethod("Render", flags)!.Invoke(component, null)!;
            });
            Assert.False(render.IsCompleted);
            Assert.Equal(0, owner.PendingOwned); Assert.Equal(0, owner.PendingPosts);
            component.Dispose(); // Actual view unmount: unsubscribe/cancel its owned controller.
            Assert.False(f.Session.Disposed); Assert.True(f.Work.Workspace.IsOpen);
            await Task.Run(renderer.Gate.Release); // Finish foreign renderer wait outside the compile owner.
            await render.WaitAsync(Bound);
            Assert.Equal(0, ownerWakeups); // Regressed capture fails here, with owner still alive (not process crash).
            Assert.Equal(0, owner.PendingPosts);
        }
        finally
        {
            component.Dispose(); renderer.Gate.Release();
            try { if (render is not null) await render.WaitAsync(Bound); }
            finally { owner.RequestClose(); await owner.WhenSettled.WaitAsync(Bound); }
        }
    });

#pragma warning disable BL0006 // Minimal renderer controls the ACTUAL component's InvokeAsync task for a lifetime contract.
    private sealed class HeldViewRenderer(IServiceProvider services, ILoggerFactory logging)
        : Microsoft.AspNetCore.Components.RenderTree.Renderer(services, logging)
    {
        internal HeldViewDispatcher Gate { get; } = new();
        public override Dispatcher Dispatcher => Gate;
        internal IComponent Create(Type type) => InstantiateComponent(type);
        internal void Attach(IComponent component) => AssignRootComponentId(component);
        protected override Task UpdateDisplayAsync(in Microsoft.AspNetCore.Components.RenderTree.RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
    }
#pragma warning restore BL0006
    private sealed class HeldViewDispatcher : Dispatcher
    {
        private Action? _action;
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _released;
        public override bool CheckAccess() => true;
        public override Task InvokeAsync(Action action)
        {
            if (_released) { action(); return Task.CompletedTask; }
            _action = action; return _finished.Task;
        }
        public override Task InvokeAsync(Func<Task> action) => action();
        public override Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public override Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
        internal void Release() { if (_released) return; _released = true; _action?.Invoke(); _finished.TrySetResult(); }
    }
    private sealed class NoJsForView : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException("View-only contract must not invoke JS.");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => InvokeAsync<T>(identifier, args);
    }

    [Theory][InlineData(1)][InlineData(512)]
    public void Current_owned_PDF_total_is_inspected_locally_without_mutating_workspace_or_preview(int pages) => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile(); f.Inspector.Pages = pages;
        f.Controller.SelectWorkType("bachelorQualification");
        f.Evaluator.Json = pages == 1 ? ExtentFixtureJson.Known : r => ExtentFixtureJson.Body(r, "indeterminate", "nonAppendixBoundaryUnknown", 40, 60);
        var before = f.Work.State(); int commits = f.Work.Repo.Commits, workers = f.Config.Workers.Created.Count;
        var preview = f.Session.Preview.State; var selectionVersion = f.Session.SelectionVersion;
        await f.Check();
        var report = Assert.IsType<RenderedExtentReport>(f.Controller.Report);
        Assert.True(f.Controller.ReportCurrent); Assert.Equal(pages, report.Observation.Request.PageCount);
        Assert.Equal(f.Session.DisplayedOutput!.OutputHash!.Value.Hex, report.Evidence.PdfSha256);
        Assert.Equal(f.Session.DisplayedOutput.SnapshotIdentity.CaptureId, report.Evidence.CaptureId);
        Assert.Equal(f.Session.DisplayedOutput.RequestId.ToString(), report.Evidence.CompileRequestId);
        Assert.Equal(new string('c', 64), report.Evidence.Runtime.LoaderSha256); Assert.Equal(1, report.Evidence.EvaluationEpoch);
        Assert.Equal(1, f.Inspector.Calls); Assert.Equal(1, f.Evaluator.Calls);
        var request = f.Inspector.Last!;
        Assert.Equal(0, request.PageIndex); Assert.Equal(512, request.Budget.Pages); Assert.Equal(30000, request.Budget.DeadlineMs);
        Assert.False(request.Raster); Assert.Null(request.Search); Assert.False(request.InspectActions);
        Assert.Equal(1, request.RasterWidth); Assert.Equal(1, request.RasterHeight);
        Assert.Equal(f.Session.DisplayedOutput.ToPayloadArray(), request.Document.CopyBytes());
        Assert.Equal(before, f.Work.State()); Assert.Equal(commits, f.Work.Repo.Commits); Assert.Equal(workers, f.Config.Workers.Created.Count);
        Assert.Equal(selectionVersion, f.Session.SelectionVersion); Assert.Equal(preview.PageIndex, f.Session.Preview.State.PageIndex);
        Assert.Equal(preview.Generation, f.Session.Preview.State.Generation); Assert.Equal(0, f.Inspector.Disposals);
    });
    [Fact] public void Unspecified_is_measured_indeterminate_and_work_type_change_never_auto_runs() => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile();
        Assert.Equal("unspecified", f.Controller.WorkType); await f.Check();
        Assert.Equal("workTypeRequired", f.Controller.Report!.Observation.Reason);
        f.Controller.SelectWorkType("masterQualification");
        Assert.Null(f.Controller.Report); Assert.Equal(1, f.Inspector.Calls); Assert.Equal(1, f.Evaluator.Calls);
        f.Controller.SelectWorkType("coursework"); Assert.Equal(1, f.Evaluator.Calls);
    });
    [Fact] public void No_current_success_or_missing_renderer_never_evaluates() => Run(async () =>
    {
        await using var f = await Fixture.Open(inspector: false);
        await f.Check(); Assert.False(f.Controller.CanCheck); Assert.Equal(0, f.Evaluator.Calls);
        await f.Compile(); await f.Check();
        Assert.Equal(RenderedExtentCheckState.RendererUnavailable, f.Controller.State); Assert.Null(f.Controller.Report);
        Assert.Equal(0, f.Evaluator.Calls);
        await f.Compile(CompileOutputStatus.Failed); await f.Check(); Assert.Equal(0, f.Evaluator.Calls);
    });
    [Theory]
    [InlineData("hash")][InlineData("id")][InlineData("epoch")][InlineData("runtime")][InlineData("runtime-sha")]
    [InlineData("native-status")][InlineData("native-error")][InlineData("not-entered")][InlineData("no-page")][InlineData("wrong-page")][InlineData("too-many-pages")]
    public void Wrong_native_outcome_never_produces_partial_count_or_evaluation(string field) => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile();
        f.Inspector.Transform = result => field switch
        {
            "hash" => result with { DocumentSha256 = new('0', 64) }, "id" => result with { RequestId = "foreign" },
            "epoch" => result with { WorkerEpoch = 0 }, "runtime" => result with { Runtime = null },
            "runtime-sha" => result with { Runtime = new("bad", new('d', 64)) }, "native-status" => result with { NativeStatus = 1 },
            "native-error" => result with { NativeError = 1 }, "not-entered" => result with { EnteredNative = false },
            "no-page" => result with { Page = null },
            "wrong-page" => Inspector.Valid(f.Inspector.Last!, result.WorkerEpoch, 2, 1),
            _ => Inspector.Valid(f.Inspector.Last!, result.WorkerEpoch, 513)
        };
        await f.Check(); Assert.Equal(RenderedExtentCheckState.InvalidResponse, f.Controller.State);
        Assert.Null(f.Controller.Report); Assert.Equal(0, f.Evaluator.Calls);
    });
    [Theory][InlineData(PdfInspectionStatus.Busy, RenderedExtentCheckState.InspectorBusy)]
    [InlineData(PdfInspectionStatus.Cancelled, RenderedExtentCheckState.Cancelled)]
    [InlineData(PdfInspectionStatus.TimedOut, RenderedExtentCheckState.TimedOut)]
    [InlineData(PdfInspectionStatus.OutputLimit, RenderedExtentCheckState.Limits)]
    [InlineData(PdfInspectionStatus.Unavailable, RenderedExtentCheckState.RendererUnavailable)]
    public void Inspection_refusals_are_distinct_without_partial_facts(PdfInspectionStatus status, RenderedExtentCheckState expected) => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile();
        f.Inspector.Transform = result => result with { Status = status, Page = null };
        await f.Check(); Assert.Equal(expected, f.Controller.State); Assert.Null(f.Controller.Report); Assert.Equal(0, f.Evaluator.Calls);
    });
    [Theory][InlineData("edit")][InlineData("ABA")][InlineData("save")][InlineData("selection")][InlineData("selection-ABA")]
    [InlineData("cancel-compile")][InlineData("new-failed")][InlineData("replace-same-hash")][InlineData("close")]
    [InlineData("reopen")][InlineData("takeover")][InlineData("project")][InlineData("dispose-session")]
    public void Full_compile_authority_changes_retire_report_even_when_PDF_hash_does_not_change(string change) => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile(); await f.Check();
        var old = f.Session.DisplayedOutput!; var file = f.Work.Workspace.ActiveFile!;
        var source = file.Content;
        switch (change)
        {
            case "edit": f.Work.Workspace.EditText(file.Id, "edited"); break;
            case "ABA": f.Work.Workspace.EditText(file.Id, "other"); f.Work.Workspace.EditText(file.Id, source); break;
            case "save": await f.Work.Workspace.SaveAsync(default); break; // Head changes without generation change.
            case "selection": f.Session.SelectEngine("xelatex"); break;
            case "selection-ABA": f.Session.SelectEngine("xelatex"); f.Session.SelectEngine("contract"); break;
            case "cancel-compile": f.Session.Cancel(); break;
            case "new-failed": await f.Compile(CompileOutputStatus.Failed); break;
            case "replace-same-hash": await f.Compile(); Assert.Equal(old.OutputHash, f.Session.DisplayedOutput!.OutputHash); Assert.NotSame(old, f.Session.DisplayedOutput); break;
            case "close": await f.Work.Workspace.CloseAsync(default); break;
            case "reopen": await f.Work.Workspace.CloseAsync(default); await f.Work.Workspace.OpenAsync(f.Work.Repo.Project.Id, default); break;
            case "takeover": await f.Work.Workspace.TakeOverAsync(default); break;
            case "project":
                var project = new Project(ProjectId.New(), "extent-other-synthetic", null); f.Work.Repo.Projects[project.Id] = (project, null);
                await f.Work.Workspace.OpenAsync(project.Id, default); break;
            case "dispose-session": await f.Session.DisposeAsync(); break;
        }
        Assert.False(f.Controller.ReportCurrent); Assert.Null(f.Controller.Report);
        if (change is "close" or "reopen" or "takeover" or "project") Assert.Equal("unspecified", f.Controller.WorkType);
    });
    [Theory][InlineData(false)][InlineData(true)]
    public void Cancelled_noncooperative_inspector_or_evaluator_keeps_whole_slot_until_actual_settlement(bool evaluation) => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (evaluation) f.Evaluator.Hold = gate; else f.Inspector.Hold = gate;
        var first = f.Check(); await (evaluation ? f.Evaluator.Reached.Task : f.Inspector.Reached.Task).WaitAsync(Bound);
        f.Controller.Cancel(); Assert.True(f.Controller.Busy); Assert.False(f.Controller.CanCancel);
        await f.Controller.CheckAsync(); Assert.Equal(1, f.Inspector.Calls); Assert.Equal(evaluation ? 1 : 0, f.Evaluator.Calls);
        Assert.False(first.IsCompleted); Assert.Null(f.Controller.Report);
        gate.TrySetResult(true); await first.WaitAsync(Bound); Assert.False(f.Controller.Busy); Assert.Null(f.Controller.Report);
        f.Inspector.Hold = null; f.Evaluator.Hold = null;
        await f.Check(); Assert.True(f.Controller.ReportCurrent);
    });
    [Theory][InlineData(false)][InlineData(true)]
    public void Worktype_staleness_during_foreign_work_never_publishes_or_restarts(bool evaluation) => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (evaluation) f.Evaluator.Hold = gate; else f.Inspector.Hold = gate;
        var checking = f.Check(); await (evaluation ? f.Evaluator.Reached.Task : f.Inspector.Reached.Task).WaitAsync(Bound);
        f.Controller.SelectWorkType("coursework"); Assert.True(f.Controller.Busy); Assert.Null(f.Controller.Report);
        await f.Controller.CheckAsync(); Assert.Equal(1, f.Inspector.Calls);
        gate.TrySetResult(true); await checking.WaitAsync(Bound);
        Assert.Null(f.Controller.Report); Assert.Equal(RenderedExtentCheckState.Stale, f.Controller.State);
        Assert.Equal(evaluation ? 1 : 0, f.Evaluator.Calls);
    });
    [Theory][InlineData(false)][InlineData(true)]
    public void Source_change_during_foreign_work_keeps_slot_and_cannot_publish(bool evaluation) => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (evaluation) f.Evaluator.Hold = gate; else f.Inspector.Hold = gate;
        var checking = f.Check(); await (evaluation ? f.Evaluator.Reached.Task : f.Inspector.Reached.Task).WaitAsync(Bound);
        f.Work.Workspace.EditText(f.Work.Main, "changed during measurement Україна 😀");
        Assert.True(f.Controller.Busy); await f.Controller.CheckAsync(); Assert.Equal(1, f.Inspector.Calls);
        gate.TrySetResult(true); await checking.WaitAsync(Bound);
        Assert.Null(f.Controller.Report); Assert.False(f.Controller.ReportCurrent);
        Assert.Equal(evaluation ? 1 : 0, f.Evaluator.Calls);
    });
    [Fact] public void Evaluation_deadline_also_retains_actual_task_and_never_publishes_late_reply() => Run(async () =>
    {
        var clock = new Clock(); await using var f = await Fixture.Open(clock: clock); await f.Compile();
        f.Evaluator.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var checking = f.Check(); await f.Evaluator.Reached.Task.WaitAsync(Bound);
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Controller.Changed += () => { if (f.Controller.State == RenderedExtentCheckState.TimedOut) expired.TrySetResult(); };
        clock.Advance(TimeSpan.FromSeconds(6)); await expired.Task.WaitAsync(Bound);
        Assert.True(f.Controller.Busy); Assert.False(f.Controller.CanCancel);
        await f.Controller.CheckAsync(); Assert.Equal(1, f.Evaluator.Calls);
        f.Evaluator.Hold.TrySetResult(true); await checking.WaitAsync(Bound); Assert.Null(f.Controller.Report);
    });
    [Fact] public void Timeout_retains_inspection_slot_and_dispose_does_not_dispose_borrowed_services() => Run(async () =>
    {
        var clock = new Clock(); await using var f = await Fixture.Open(clock: clock); await f.Compile();
        f.Inspector.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var checking = f.Check(); await f.Inspector.Reached.Task.WaitAsync(Bound);
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Controller.Changed += () => { if (f.Controller.State == RenderedExtentCheckState.TimedOut) expired.TrySetResult(); };
        clock.Advance(TimeSpan.FromSeconds(31)); await expired.Task.WaitAsync(Bound);
        Assert.True(f.Controller.Busy); await f.Controller.CheckAsync(); Assert.Equal(1, f.Inspector.Calls);
        f.Controller.Dispose(); Assert.Equal(0, f.Inspector.Disposals); Assert.False(f.Session.Disposed); Assert.True(f.Work.Workspace.IsOpen);
        f.Inspector.Hold.TrySetResult(true); await checking.WaitAsync(Bound); Assert.Null(f.Controller.Report); Assert.Equal(0, f.Evaluator.Calls);
    });
    [Fact] public void Normal_preview_notifications_preserve_same_authority_and_readonly_observation_is_allowed() => Run(async () =>
    {
        await using var f = await Fixture.Open(readOnly: true); await f.Compile(); await f.Check();
        var report = f.Controller.Report; Assert.NotNull(report); Assert.True(f.Work.Workspace.IsReadOnly);
        f.Session.Preview.Retry(); Assert.Same(report, f.Controller.Report); Assert.True(f.Controller.ReportCurrent);
        Assert.Equal(0, f.Work.Repo.Commits);
    });
    [Fact] public void Wrong_evaluation_id_epoch_or_body_cannot_publish() => Run(async () =>
    {
        await using var f = await Fixture.Open(); await f.Compile();
        f.Evaluator.Transform = reply => reply with { RequestId = new('f', 32) };
        await f.Check(); Assert.Null(f.Controller.Report);
        f.Evaluator.Transform = reply => reply with { WorkerEpoch = 0 };
        await f.Check(); Assert.Null(f.Controller.Report);
        f.Evaluator.Transform = reply => reply with { ResponseJson = "{\"schemaVersion\":1,\"kind\":\"error\",\"code\":\"invalidInput\",\"extra\":1}" };
        await f.Check(); Assert.Null(f.Controller.Report); Assert.Equal(RenderedExtentCheckState.InvalidResponse, f.Controller.State);
    });
}
