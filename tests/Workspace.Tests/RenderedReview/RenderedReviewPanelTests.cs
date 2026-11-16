using Client.Analysis.RenderedExtent;
using Client.Analysis.RenderedReview;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Pages;
using Client.Pdf;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.RenderedReview;

// Real Razor events + compilation authority over modeled workers. No actual
// renderer, Rust rules, standards compliance or browser acceptance is claimed.
public sealed class RenderedReviewPanelTests
{
    [Fact]
    public void Rendering_is_readonly_and_never_starts_a_check_or_treats_empty_labels_as_pass() => Run(async () =>
    {
        await using var f = await Host.Open();
        using var services = Services(f); await using var renderer = new Events(services, services.GetRequiredService<ILoggerFactory>());
        var before = f.Work.State(); int inspections = f.Inspector.LayoutCalls, evaluations = f.Evaluator.Calls;
        int id = await renderer.Dispatcher.InvokeAsync(() => renderer.Mount(f));
        string text = renderer.Text(id);
        Assert.Contains("Coverage is partial", text); Assert.Contains("No page roles confirmed", text);
        Assert.Contains("Role-dependent checks remain unknown", text);
        Assert.Throws<InvalidOperationException>(() => renderer.Element(id, "rendered-review-report"));
        renderer.Element(id, "rendered-review-check"); renderer.Element(id, "rendered-review-cancel");
        Assert.Equal(before, f.Work.State()); Assert.Equal(inspections, f.Inspector.LayoutCalls); Assert.Equal(evaluations, f.Evaluator.Calls);
    });

    [Fact]
    public void Explicit_labels_reject_overlap_and_invalid_ranges_then_require_reconfirmation_after_compile() => Run(async () =>
    {
        await using var f = await Host.Open(); using var services = Services(f);
        await using var renderer = new Events(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            int id = await renderer.Mount(f);
            await renderer.Change(id, "rendered-role-last", "3", "oninput");
            Assert.True(renderer.Element(id, "rendered-review-check").Attributes.ContainsKey("disabled"));
            await renderer.Click(id, "rendered-role-add");
            Assert.Equal(new[] { 0, 1, 2 }, f.Controller.Annotations.Select(label => label.PageIndex));
            Assert.All(f.Controller.Annotations, label => { Assert.Equal("authorLabel", label.Provenance); Assert.Null(label.SourceFileId); Assert.Null(label.SourceSpan); Assert.True(label.AnnotationId > 0); });
            long version = f.Controller.AnnotationVersion;
            await renderer.Change(id, "rendered-role", "title"); await renderer.Change(id, "rendered-role-last", "", "oninput");
            await renderer.Click(id, "rendered-role-add");
            Assert.Equal(version, f.Controller.AnnotationVersion); Assert.Equal(3, f.Controller.Annotations.Count);
            renderer.Element(id, "rendered-review-input-error");
            await renderer.Click(id, "rendered-role-clear"); await renderer.Change(id, "rendered-role-scope", "characters");
            await renderer.Change(id, "rendered-role-start", "2", "oninput"); await renderer.Change(id, "rendered-role-end", "1", "oninput");
            await renderer.Click(id, "rendered-role-add"); Assert.Empty(f.Controller.Annotations);
            await renderer.Change(id, "rendered-role-start", "0", "oninput"); await renderer.Change(id, "rendered-role-end", "2", "oninput");
            await renderer.Click(id, "rendered-role-add");
            var label = Assert.Single(f.Controller.Annotations); Assert.Equal(0, label.CharacterStart); Assert.Equal(2, label.CharacterEnd);
            version = f.Controller.AnnotationVersion;
            Assert.True(f.Work.Workspace.EditText(f.Work.Main, "New source generation\n"));
            Assert.False(f.Controller.AnnotationsCurrent);
            await renderer.Click(id, "rendered-role-confirm"); Assert.Equal(version, f.Controller.AnnotationVersion);
            await f.Compile(); Assert.False(f.Controller.AnnotationsCurrent);
            await renderer.Click(id, "rendered-role-confirm"); Assert.True(f.Controller.AnnotationsCurrent); Assert.True(f.Controller.AnnotationVersion > version);
            Assert.Equal(0, f.Inspector.LayoutCalls); Assert.Equal(0, f.Evaluator.Calls);
        });
    });

    [Fact]
    public void Label_and_page_scope_bounds_refuse_before_inspection_and_label_rows_are_paginated() => Run(async () =>
    {
        await using var f = await Host.Open(); using var services = Services(f);
        await using var renderer = new Events(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            int id = await renderer.Mount(f);
            await renderer.Change(id, "rendered-role-last", "512", "oninput"); await renderer.Click(id, "rendered-role-add");
            Assert.Equal(512, f.Controller.Annotations.Count); Assert.Equal(16, renderer.Count(id, "rendered-role-row"));
            long version = f.Controller.AnnotationVersion;
            await renderer.Change(id, "rendered-role-first", "513", "oninput"); await renderer.Click(id, "rendered-role-add");
            Assert.Equal(version, f.Controller.AnnotationVersion);
            await renderer.Click(id, "rendered-role-discard"); await renderer.Click(id, "rendered-role-clear");
            await renderer.Change(id, "rendered-review-all-pages", false);
            await renderer.Change(id, "rendered-review-first", "0", "oninput"); await renderer.Click(id, "rendered-review-check");
            renderer.Element(id, "rendered-review-input-error");
            Assert.Equal(0, f.Inspector.LayoutCalls); Assert.Equal(0, f.Evaluator.Calls);
            Assert.True(f.Work.Workspace.IsOpen); Assert.False(f.Session.Disposed);
        });
    });

    private static ParameterView Parameters(Host host) => ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = host.Controller });
    private static ServiceProvider Services(Host host) => new ServiceCollection().AddLogging().AddSingleton(host.Work.Workspace)
        .AddSingleton(host.Session).AddSingleton<IRenderedReviewEvaluator>(host.Evaluator).BuildServiceProvider();
    private sealed class Configuration(Workers workers, Inspector inspector) : ICompileWorkspaceConfiguration
    {
        public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(new(true, "Ready", "Modeled"),
            new("review-test", "Modeled", CompilationTestWorkspace.Descriptor(), [], [], workers, inspector,
                () => Task.FromResult(new CompileReadiness(true, "Ready", "Modeled")), SelectionOptions: NativeSelectedEntryContract.SelectionOptions)));
    }
    private sealed class Inspector : IPdfInspector, IPdfLayoutInspector
    {
        public int LayoutCalls;
        public Task<PdfInspectionOutcome> InspectAsync(PdfInspectRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new PdfInspectionOutcome(PdfInspectionStatus.Unavailable, Guid.NewGuid().ToString("N"), request.Document.Sha256, 1));
        public Task<PdfLayoutOutcome> InspectLayoutAsync(PdfLayoutRequest request, CancellationToken cancellationToken = default)
        { LayoutCalls++; return Task.FromResult(new PdfLayoutOutcome(PdfInspectionStatus.Unavailable, Guid.NewGuid().ToString("N"), request.Document.Sha256, 1)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Evaluator : IRenderedReviewEvaluator
    {
        public int Calls;
        public Task<RenderedReviewEvaluationReply> EvaluateAsync(RenderedReviewRequest request, ReadOnlyMemory<byte> factsUtf8, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new RenderedReviewEvaluationReply(RenderedExtentEvaluationStatus.Unavailable, request.RequestId, 0)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Host : IAsyncDisposable
    {
        public CompilationTestWorkspace Work = null!;
        public readonly Workers Workers = new(); public readonly Inspector Inspector = new(); public readonly Evaluator Evaluator = new();
        public CompileWorkspaceSession Session = null!; public CompiledReviewController Controller = null!;
        public static async Task<Host> Open()
        {
            var h = new Host { Work = await CompilationTestWorkspace.Open() };
            Assert.True(h.Work.Workspace.RenameFile(h.Work.Main, "main.tex")); var owner = SynchronizationContext.Current!;
            Task Dispatch(Action action)
            {
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                owner.Post(_ => { try { action(); signal.SetResult(); } catch (Exception error) { signal.SetException(error); } }, null); return signal.Task;
            }
            h.Session = new(h.Work.Workspace, new Configuration(h.Workers, h.Inspector)); h.Session.AttachRenderer(Dispatch);
            await h.Session.RefreshToolsAsync(); h.Session.SelectMain(h.Work.Main); h.Session.SelectEngine("review-test");
            h.Controller = new(h.Work.Workspace, h.Session, h.Evaluator, Dispatch); await h.Compile(); return h;
        }
        public async Task Compile()
        {
            var previous = Session.Attempt; var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed() { if (Session.Attempt is { } attempt && !ReferenceEquals(previous, attempt)) shown.TrySetResult(); }
            Session.Changed += Changed;
            try { await Session.CompileAsync(); (await Workers.Next()).Succeed(); Assert.Equal(CompileLifecycleReason.Succeeded, (await Session.LastTicket!.Completion).Reason); await shown.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Session.Changed -= Changed; }
        }
        public async ValueTask DisposeAsync()
        {
            Controller.Dispose(); foreach (var worker in Workers.Created) worker.Succeed();
            await Session.DisposeAsync(); await Session.Owner!.WhenSettled.WaitAsync(TimeSpan.FromSeconds(5)); await Work.DisposeAsync();
        }
    }

#pragma warning disable BL0006 // Actual Razor event dispatch over a bounded in-process renderer, not browser proof.
    private sealed class Events(IServiceProvider services, ILoggerFactory logging) : Microsoft.AspNetCore.Components.RenderTree.Renderer(services, logging)
    {
        public override Dispatcher Dispatcher { get; } = new OwnerDispatcher(SynchronizationContext.Current!);
        public async Task<int> Mount(Host host) { var id = AssignRootComponentId(InstantiateComponent(typeof(RenderedReviewPanel))); await RenderRootComponentAsync(id, Parameters(host)); return id; }
        internal sealed record Control(Dictionary<string, object?> Attributes, Dictionary<string, ulong> Events);
        public Control Element(int id, string testId)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Element) continue;
                var attributes = new Dictionary<string, object?>(); var events = new Dictionary<string, ulong>();
                for (int j = i + 1; j < frames.Count && frames.Array[j].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute; j++)
                { var attribute = frames.Array[j]; attributes[attribute.AttributeName] = attribute.AttributeValue; if (attribute.AttributeEventHandlerId != 0) events[attribute.AttributeName] = attribute.AttributeEventHandlerId; }
                if (attributes.TryGetValue("data-testid", out var value) && Equals(value, testId)) return new(attributes, events);
            }
            throw new InvalidOperationException("Expected rendered-review control was not rendered.");
        }
        public int Count(int id, string testId)
        { var frames = GetCurrentRenderTreeFrames(id); int count = 0; for (int i = 0; i < frames.Count; i++) if (frames.Array[i].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute && frames.Array[i].AttributeName == "data-testid" && Equals(frames.Array[i].AttributeValue, testId)) count++; return count; }
        public string Text(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id); var text = new System.Text.StringBuilder();
            for (int i = 0; i < frames.Count; i++)
                if (frames.Array[i].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Text) text.Append(frames.Array[i].TextContent);
                else if (frames.Array[i].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Markup) text.Append(frames.Array[i].MarkupContent);
            return text.ToString();
        }
        public Task Change(int id, string testId, object value, string eventName = "onchange") => DispatchEventAsync(Element(id, testId).Events[eventName], null, new ChangeEventArgs { Value = value });
        public Task Click(int id, string testId) => DispatchEventAsync(Element(id, testId).Events["onclick"], null, new MouseEventArgs());
        protected override Task UpdateDisplayAsync(in Microsoft.AspNetCore.Components.RenderTree.RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
    }
    private sealed class OwnerDispatcher(SynchronizationContext owner) : Dispatcher
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        public override bool CheckAccess() => Environment.CurrentManagedThreadId == _thread;
        public override Task InvokeAsync(Action workItem) => Run(() => { workItem(); return Task.FromResult(true); });
        public override Task InvokeAsync(Func<Task> workItem) => Run(async () => { await workItem(); return true; });
        public override Task<T> InvokeAsync<T>(Func<T> workItem) => Run(() => Task.FromResult(workItem()));
        public override Task<T> InvokeAsync<T>(Func<Task<T>> workItem) => Run(workItem);
        private Task<T> Run<T>(Func<Task<T>> workItem)
        {
            if (CheckAccess()) return workItem();
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.Post(async _ => { try { result.TrySetResult(await workItem()); } catch (Exception error) { result.TrySetException(error); } }, null);
            return result.Task;
        }
    }
#pragma warning restore BL0006
}
