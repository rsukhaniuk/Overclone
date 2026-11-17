using System.Collections.Immutable;
using System.Text;
using Client.Analysis;
using Client.Analysis.RenderedExtent;
using Client.Analysis.RenderedReview;
using Client.Compilation.Presentation;
using Client.Pages;
using Client.Pdf;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Fixture = Workspace.Tests.RenderedRoleBinding.RoleBindingTests.Host;
using NativeRoleFact = Workspace.Tests.RenderedRoleBinding.NativeRoleFactAttribute;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.RenderedRoleBindingUi;

// Actual Razor event dispatch and existing authority services over the separately
// recorded native/Rust fixture. No browser/offline or fresh native execution claim.
public sealed class SourceBindingPanelTests
{
    [NativeRoleFact]
    public void Panel_owned_controller_receives_the_binding_parameter_without_an_injected_session() => Run(async () =>
    {
        await using var h = await Host.Open(); using var services = Services(h);
        await using var renderer = new Events(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            string before = h.Base.Work.State(); int id = await renderer.Mount(h, h.Base.Report, panelOwnsController: true);
            await renderer.Change(id, "rendered-review-work-type", "bachelorQualification");
            await renderer.Change(id, "rendered-source-event", "0"); await renderer.Click(id, "rendered-source-bind");
            Assert.Equal("captionText", renderer.Element(id, "rendered-source-binding").Attributes["data-range-kind"]);
            Assert.Empty(h.Controller.Annotations); // This supplied host controller was not used by the panel.
            Assert.Equal(3, h.Base.Queries.Calls); Assert.Equal(before, h.Base.Work.State());
        });
    });

    [NativeRoleFact]
    public void Selection_is_readonly_and_only_explicit_match_adds_the_issued_native_range() => Run(async () =>
    {
        await using var h = await Host.Open(); using var services = Services(h);
        await using var renderer = new Events(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            string before = h.Base.Work.State(); int id = await renderer.Mount(h, h.Base.Report);
            Assert.True(renderer.Disabled(id, "rendered-source-bind")); Assert.Empty(h.Controller.Annotations);
            await renderer.Change(id, "rendered-source-event", "0");
            Assert.False(renderer.Disabled(id, "rendered-source-bind")); Assert.Empty(h.Controller.Annotations);
            Assert.Equal(0, h.Base.Queries.Calls); Assert.Equal(0, h.Base.Inspector.Calls);
            await renderer.Click(id, "rendered-source-bind");
            var proof = Assert.Single(h.Controller.SourceBindings); var annotation = Assert.Single(h.Controller.Annotations);
            Assert.Same(proof.Annotation, annotation); Assert.Equal("figureCaption", annotation.Role);
            Assert.Equal("literalSourceAndNativeMap", annotation.Provenance); Assert.Equal(10, annotation.CharacterStart); Assert.Equal(26, annotation.CharacterEnd);
            var output = renderer.Element(id, "rendered-source-binding");
            Assert.Equal("captionText", output.Attributes["data-range-kind"]); Assert.Equal("false", output.Attributes["data-glyph-ink-measured"]);
            Assert.Contains("only those text characters", renderer.Text(id)); Assert.True(renderer.Disabled(id, "rendered-source-bind"));
            Assert.Equal(before, h.Base.Work.State()); Assert.Equal(0, h.Base.Work.Repo.Commits);
        });
    });

    [NativeRoleFact]
    public void Same_report_retains_draft_but_a_new_canonical_report_requires_reselection() => Run(async () =>
    {
        await using var h = await Host.Open(); using var services = Services(h);
        await using var renderer = new Events(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            int id = await renderer.Mount(h, h.Base.Report); await renderer.Change(id, "rendered-source-event", "0");
            await renderer.Update(id, h, h.Base.Report); Assert.False(renderer.Disabled(id, "rendered-source-bind"));
            var previous = h.Base.Report; var ready = new TaskCompletionSource<AnalysisReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(AnalysisState state) { if (state is AnalysisState.Completed c && !ReferenceEquals(c.Report, previous)) ready.TrySetResult(c.Report); }
            h.Base.Analysis.Changed += Changed;
            AnalysisReport next;
            try { h.Base.Analysis.RequestReanalyze(); next = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { h.Base.Analysis.Changed -= Changed; }
            Assert.NotSame(previous, next); await renderer.Update(id, h, next);
            Assert.True(renderer.Disabled(id, "rendered-source-bind"), renderer.Text(id));
            await renderer.Click(id, "rendered-source-bind"); Assert.Empty(h.Controller.Annotations); Assert.Equal(0, h.Base.Queries.Calls);
            await renderer.Change(id, "rendered-source-event", "0"); await renderer.Click(id, "rendered-source-bind");
            Assert.Same(next, Assert.Single(h.Controller.SourceBindings).Report);
        });
    });

    [NativeRoleFact]
    public void Copied_report_metadata_cannot_publish_an_annotation_through_Razor_events() => Run(async () =>
    {
        await using var h = await Host.Open(); using var services = Services(h);
        await using var renderer = new Events(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            int id = await renderer.Mount(h, h.Base.Report with { });
            await renderer.Change(id, "rendered-source-event", "0"); await renderer.Click(id, "rendered-source-bind");
            Assert.Empty(h.Controller.Annotations); Assert.Empty(h.Controller.SourceBindings); Assert.Equal(0, h.Base.Queries.Calls);
            Assert.Contains("stale-source-or-compile", renderer.Text(id));
        });
    });

    [NativeRoleFact]
    public void Stale_matched_row_can_be_removed_without_reconfirmation_or_new_compile() => Run(async () =>
    {
        await using var h = await Host.Open(); using var services = Services(h);
        await using var renderer = new Events(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            int id = await renderer.Mount(h, h.Base.Report); await renderer.Change(id, "rendered-source-event", "0"); await renderer.Click(id, "rendered-source-bind");
            await h.Base.Compile(); await renderer.Update(id, h, h.Base.Report); Assert.False(h.Controller.AnnotationsCurrent);
            int compiles = h.Base.Workers.Created.Count; long version = h.Controller.AnnotationVersion;
            await renderer.Click(id, "rendered-role-confirm"); Assert.Equal(version, h.Controller.AnnotationVersion); Assert.False(h.Controller.AnnotationsCurrent);
            var remove = renderer.Attribute(id, "aria-label", "Remove Figure caption label on page 1");
            Assert.False(remove.Attributes.ContainsKey("disabled")); await renderer.Click(remove);
            Assert.Empty(h.Controller.Annotations); Assert.Empty(h.Controller.SourceBindings); Assert.True(h.Controller.AnnotationsCurrent);
            Assert.Equal(compiles, h.Base.Workers.Created.Count);
        });
    });

    [NativeRoleFact]
    public void Long_untrusted_Unicode_label_truncates_at_a_scalar_boundary_and_cannot_bind() => Run(async () =>
    {
        await using var h = await Host.Open(); using var services = Services(h);
        await using var renderer = new Events(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            // Display-only malformed report DATA. It is never canonical authority
            // and cannot change the original native source/response fixture.
            var report = h.Base.Report; var thesis = report.Result.Thesis!; var e = thesis.Events[0];
            string title = new string('a', 95) + "🙂Хвіст";
            var clone = report with { Result = report.Result with { Thesis = thesis with { Events = thesis.Events.SetItem(0, e with { Text = title }) } } };
            int id = await renderer.Mount(h, clone); string text = renderer.Text(id);
            Assert.Contains(new string('a', 95) + "🙂…", text); Assert.DoesNotContain('\ufffd', text);
            _ = new UTF8Encoding(false, true).GetBytes(text);
            await renderer.Change(id, "rendered-source-event", "0"); await renderer.Click(id, "rendered-source-bind");
            Assert.Empty(h.Controller.Annotations); Assert.Equal(0, h.Base.Queries.Calls);
        });
    });

    private static ServiceProvider Services(Host h) => new ServiceCollection().AddLogging()
        .AddSingleton(h.Base.Work.Workspace).AddSingleton(h.Base.Session).AddSingleton<IRenderedReviewEvaluator>(h.Evaluator).BuildServiceProvider();
    private sealed class Evaluator : IRenderedReviewEvaluator
    {
        public Task<RenderedReviewEvaluationReply> EvaluateAsync(RenderedReviewRequest request, ReadOnlyMemory<byte> factsUtf8, CancellationToken ct = default)
            => throw new InvalidOperationException("Source-binding UI tests must not run a rendered evaluation implicitly.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Host : IAsyncDisposable
    {
        internal Fixture Base = null!; internal readonly Evaluator Evaluator = new(); internal CompiledReviewController Controller = null!;
        internal static async Task<Host> Open()
        {
            var h = new Host { Base = await Fixture.Open() }; var owner = SynchronizationContext.Current!;
            Task Dispatch(Action action) { var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); owner.Post(_ => { try { action(); done.SetResult(); } catch (Exception e) { done.SetException(e); } }, null); return done.Task; }
            h.Controller = new(h.Base.Work.Workspace, h.Base.Session, h.Evaluator, Dispatch, h.Base.Binding);
            h.Controller.SelectWorkType("bachelorQualification"); return h;
        }
        public async ValueTask DisposeAsync() { Controller.Dispose(); await Base.DisposeAsync(); }
    }

#pragma warning disable BL0006 // Actual bounded Razor event dispatcher, not browser evidence.
    private sealed class Events(IServiceProvider services, ILoggerFactory logging) : Microsoft.AspNetCore.Components.RenderTree.Renderer(services, logging)
    {
        public override Dispatcher Dispatcher { get; } = new OwnerDispatcher(SynchronizationContext.Current!);
        private static ParameterView Parameters(Host h, AnalysisReport report, bool panelOwnsController = false) => ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Session"] = panelOwnsController ? null : h.Controller, ["Binding"] = h.Base.Binding, ["SourceReport"] = report });
        internal async Task<int> Mount(Host h, AnalysisReport report, bool panelOwnsController = false) { int id = AssignRootComponentId(InstantiateComponent(typeof(RenderedReviewPanel))); await RenderRootComponentAsync(id, Parameters(h, report, panelOwnsController)); return id; }
        internal Task Update(int id, Host h, AnalysisReport report) => RenderRootComponentAsync(id, Parameters(h, report));
        internal sealed record Control(Dictionary<string, object?> Attributes, Dictionary<string, ulong> Handlers);
        internal Control Element(int id, string testId) => Attribute(id, "data-testid", testId);
        internal Control Attribute(int id, string name, string expected)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Element) continue;
                var attributes = new Dictionary<string, object?>(); var handlers = new Dictionary<string, ulong>();
                for (int j = i + 1; j < frames.Count && frames.Array[j].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute; j++)
                { var a = frames.Array[j]; attributes[a.AttributeName] = a.AttributeValue; if (a.AttributeEventHandlerId != 0) handlers[a.AttributeName] = a.AttributeEventHandlerId; }
                if (attributes.TryGetValue(name, out var value) && Equals(value, expected)) return new(attributes, handlers);
            }
            throw new InvalidOperationException("Expected rendered source-binding control missing: " + expected);
        }
        internal bool Disabled(int id, string testId) => Element(id, testId).Attributes.ContainsKey("disabled");
        internal Task Change(int id, string testId, object value) => DispatchEventAsync(Element(id, testId).Handlers["onchange"], null, new ChangeEventArgs { Value = value });
        internal Task Click(int id, string testId) => Click(Element(id, testId));
        internal Task Click(Control control) => DispatchEventAsync(control.Handlers["onclick"], null, new MouseEventArgs());
        internal string Text(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id); var text = new StringBuilder();
            for (int i = 0; i < frames.Count; i++)
                if (frames.Array[i].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Text) text.Append(frames.Array[i].TextContent);
                else if (frames.Array[i].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Markup) text.Append(frames.Array[i].MarkupContent);
            return text.ToString();
        }
        protected override Task UpdateDisplayAsync(in Microsoft.AspNetCore.Components.RenderTree.RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
    }
    private sealed class OwnerDispatcher(SynchronizationContext owner) : Dispatcher
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        public override bool CheckAccess() => Environment.CurrentManagedThreadId == _thread;
        public override Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return Task.CompletedTask; });
        public override Task InvokeAsync(Func<Task> action) => InvokeAsync(async () => { await action(); return true; });
        public override Task<T> InvokeAsync<T>(Func<T> action) => InvokeAsync(() => Task.FromResult(action()));
        public override Task<T> InvokeAsync<T>(Func<Task<T>> action)
        {
            if (CheckAccess()) return action(); var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.Post(async _ => { try { done.SetResult(await action()); } catch (Exception e) { done.SetException(e); } }, null); return done.Task;
        }
    }
#pragma warning restore BL0006
}
