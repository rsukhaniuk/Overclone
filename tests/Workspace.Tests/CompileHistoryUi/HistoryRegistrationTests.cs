using System.Net;
using Client.Compilation.History;
using Client.Compilation.Presentation;
using Client.Pages;
using Client.Workspace;
using Client.Workspace.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.CompileHistoryUi;

// Actual registration/parent Razor composition with contract document IO only.
// The finite parent host owns async fixture cleanup, not the borrowed compile owner.
public sealed class HistoryRegistrationTests
{
    [Fact]
    public void Explicit_unavailable_compiler_can_resolve_render_and_save_source_with_history_mounted() => Run(async () =>
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        var js = new NoJs(); var registrations = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(js);
        registrations.AddSingleton<ICompileWorkspaceConfiguration, UnavailableCompileWorkspaceConfiguration>();
        registrations.AddAuthoringWorkspace();
        registrations.AddSingleton<IProjectRepository>(fixture.Repo);
        registrations.AddSingleton<IWorkspaceLocks>(fixture.Lock);
        await using var provider = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope(); var services = scope.ServiceProvider;
        Assert.IsType<UnavailableCompileWorkspaceConfiguration>(services.GetRequiredService<ICompileWorkspaceConfiguration>());
        var history = services.GetRequiredService<CompileHistoryUiSession>(); // BEFORE parent initialization/readiness.
        var compile = services.GetRequiredService<CompileWorkspaceSession>();
        var workspace = services.GetRequiredService<WorkspaceCoordinator>();
        Assert.Null(compile.ConfiguredPorts); Assert.False(history.CanSave); Assert.Equal(0, js.Calls);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        try
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await workspace.OpenAsync(fixture.Repo.Project.Id, CancellationToken.None);
                var controls = await renderer.RenderComponentAsync<CompileControls>(ParameterView.Empty);
                var output = await renderer.RenderComponentAsync<CompilePanel>(ParameterView.Empty);
                string html = WebUtility.HtmlDecode(controls.ToHtmlString() + output.ToHtmlString());
                Assert.Contains("Compiler unavailable", html); Assert.Contains("Saved PDFs", html); Assert.Contains("Save PDF to history", html);
                Assert.Contains("data-testid=\"compile-history-panel\"", html); Assert.False(compile.Readiness.Ready); Assert.False(history.CanSave);
                var main = workspace.Files.First(f => f.MediaKind == "text").Id;
                Assert.True(workspace.EditText(main, "ordinary local source remains editable"));
                var saved = await workspace.SaveAsync(CancellationToken.None);
                Assert.Equal(CommitStatus.Saved, saved.Status); Assert.Equal(saved.Head, workspace.Project!.HeadRevisionId);
                Assert.Equal(0, js.Calls); // No history imports/provisioning on startup/source save.
            });
            await history.DisposeAsync(); await history.WhenSettled.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.False(compile.Disposed); Assert.True(workspace.IsOpen);
        }
        finally
        {
            // Close only from the finite parent fixture AFTER the render/source
            // workflow returned, never await close from inside its own owner.
            await compile.DisposeAsync();
            if (compile.Owner is { } owner) await owner.WhenSettled.WaitAsync(TimeSpan.FromSeconds(4));
        }
    });
    [Theory]
    [InlineData("compile")]
    [InlineData("controls")]
    [InlineData("history")]
    [InlineData("preview")]
    public void View_only_renderer_await_does_not_capture_untracked_compile_owner(string componentName) => Run(async () =>
    {
        var parent = SynchronizationContext.Current!; int wakes = 0;
        var owner = new CompileOwnerContext(action =>
        {
            Interlocked.Increment(ref wakes);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            parent.Post(_ => { try { action(); done.TrySetResult(); } catch (Exception e) { done.TrySetException(e); } }, null);
            return done.Task;
        });
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoJs()).AddAuthoringWorkspace().BuildServiceProvider();
        await using var renderer = new GateRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var type = componentName == "compile" ? typeof(CompilePanel) : componentName == "controls" ? typeof(CompileControls)
            : componentName == "history" ? typeof(CompileHistoryPanel) : typeof(Client.Pdf.Preview.PdfPreviewPane);
        var component = renderer.Create(type);
        if (component is CompileHistoryPanel) type.GetProperty(nameof(CompileHistoryPanel.Session))!.SetValue(component, services.GetRequiredService<CompileHistoryUiSession>());
        renderer.Attach(component);
        var method = type.GetMethod(componentName == "preview" ? "Refresh" : "Render", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Task render = owner.Run(() =>
        {
            Assert.Same(owner, SynchronizationContext.Current);
            return (Task)method.Invoke(component, null)!;
        });
        Assert.False(render.IsCompleted); Assert.Equal(0, owner.PendingOwned); Assert.Equal(0, owner.PendingPosts);
        Console.Error.WriteLine("VIEW-CONTEXT-PROBE " + componentName + ": actual CompileOwnerContext active; renderer await pending; owned0/posts0");
        // Model unmount while rendering is queued. Keep the real UI action inside
        // the dispatcher; disposed view has no StateHasChanged work left to do.
        type.GetField("_disposed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(component, true);
        try
        {
            await Task.Run(renderer.Gate.Release); await render.WaitAsync(TimeSpan.FromSeconds(4));
            Console.Error.WriteLine("VIEW-CONTEXT-PROBE " + componentName + ": captured-owner wakeups=" + wakes);
            Assert.Equal(0, wakes);
        }
        finally { renderer.Gate.Release(); owner.RequestClose(); await owner.WhenSettled.WaitAsync(TimeSpan.FromSeconds(4)); }
    });
#pragma warning disable BL0006 // Minimal test renderer controls the actual InvokeAsync task; not product code.
    private sealed class GateRenderer(IServiceProvider services, ILoggerFactory logging) : Microsoft.AspNetCore.Components.RenderTree.Renderer(services, logging)
    {
        public GateDispatcher Gate { get; } = new();
        public override Dispatcher Dispatcher => Gate;
        public IComponent Create(Type type) => InstantiateComponent(type);
        public void Attach(IComponent component) => AssignRootComponentId(component);
        protected override Task UpdateDisplayAsync(in Microsoft.AspNetCore.Components.RenderTree.RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
    }
#pragma warning restore BL0006
    private sealed class GateDispatcher : Dispatcher
    {
        private Action? _action; private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously); private bool _released;
        public override bool CheckAccess() => true;
        public override Task InvokeAsync(Action action) { if (_released) { action(); return Task.CompletedTask; } _action = action; return _complete.Task; }
        public override Task InvokeAsync(Func<Task> action) => action();
        public override Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public override Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
        public void Release() { if (_released) return; _released = true; _action?.Invoke(); _complete.TrySetResult(); }
    }
    private sealed class NoJs : IJSRuntime
    {
        public int Calls;
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) { Calls++; throw new NotSupportedException("Static startup must not call JS"); }
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken token, object?[]? args) => InvokeAsync<T>(name, args);
    }
}
