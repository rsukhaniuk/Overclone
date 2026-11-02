using System.Collections.Immutable;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Presentation;
using Client.Compilation.SyncTeX;
using Client.Workspace;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.SyncTeXNavigation;

// Real coordinator, capture, envelope admission, lifecycle and session. Only
// repository/worker/query transports are modeled. No native/browser claim.
internal sealed class NavigationFixture : IAsyncDisposable
{
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    internal static readonly byte[] Map = [1, 2, 3, 4]; // Opaque seam bytes, NOT a synthetic SyncTeX parser.
    internal CompilationTestWorkspace Storage = null!;
    internal CompileWorkspaceSession Session = null!;
    internal SyncTeXNavigationPolicy Policy = null!;
    internal readonly Configuration Config = new();
    internal readonly Queries Query = new();
    internal WorkspaceCoordinator Workspace => Storage.Workspace;
    internal FileId Main => Storage.Main;

    internal static async Task<NavigationFixture> Open(bool readOnly = false, string engine = "synthetic-engine")
    {
        var f = new NavigationFixture { Storage = await CompilationTestWorkspace.Open(readOnly: readOnly) };
        f.Config.Engine = engine;
        if (readOnly)
        {
            var revision = f.Storage.Repo.Revision!;
            f.Storage.Repo.Revision = revision with { Files = revision.Files.Select(file =>
                file.RelativePath == "entry" ? file with { RelativePath = "main.tex" } : file).ToImmutableArray() };
            f.Storage.Repo.Remember();
            await f.Workspace.CloseAsync(CancellationToken.None);
            await f.Workspace.OpenAsync(f.Storage.Repo.Project.Id, CancellationToken.None);
        }
        else Assert.True(f.Workspace.RenameFile(f.Main, "main.tex"));
        var ui = SynchronizationContext.Current!;
        f.Session = new(f.Workspace, f.Config);
        f.Session.AttachRenderer(action =>
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(_ => { try { action(); signal.SetResult(); } catch (Exception e) { signal.SetException(e); } }, null);
            return signal.Task;
        });
        await f.Session.RefreshToolsAsync();
        f.Session.SelectMain(f.Main); f.Session.SelectEngine("contract");
        f.Policy = new(f.Workspace, f.Session, f.Query);
        return f;
    }

    internal async Task<AcceptedCompileOutput> Compile(bool mapBytes = true, bool mapIdentity = true, byte[]? nativeGeometry = null,
        string? geometryEngine = null)
    {
        var previous = Session.DisplayedOutput;
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() { if (Session.DisplayedOutput is { } output && !ReferenceEquals(output, previous)) shown.TrySetResult(); }
        Session.Changed += Changed;
        try
        {
            await Session.CompileAsync();
            var worker = await Config.Workers.Next();
            var output = worker.Output();
            var geometry = nativeGeometry is null ? null : SourceMapGeometryCodec.AssembleData(geometryEngine ?? Config.Engine, 1,
                nativeGeometry, Map, output.Payload, (geometryEngine ?? Config.Engine) is "latex-dvi" or "xelatex" ? [9] : null,
                new(1024 * 1024, 65536, 32, 128, 4096, 8192)).Artifact;
            if (nativeGeometry is not null) Assert.NotNull(geometry);
            worker.Reply.TrySetResult(output with
            {
                SourceMapping = mapIdentity ? new(CompilationTestWorkspace.Hash(Map), Map.Length) : null,
                SourceMapPayload = mapBytes ? Map : null,
                SourceMapGeometryPayload = geometry?.ToArray(), SourceMapGeometry = geometry?.Identity
            });
            Assert.Equal(CompileLifecycleReason.Succeeded, (await Session.LastTicket!.Completion.WaitAsync(Bound)).Reason);
            await shown.Task.WaitAsync(Bound);
            Assert.True(Session.State!.IsCurrent);
            return Session.DisplayedOutput!;
        }
        finally { Session.Changed -= Changed; }
    }

    internal static SyncTeXMatch Match(string path = "main.tex", int line = 1, int page = 1) =>
        new(path, 7, line, 91, page, new(14.25, 23.5), new(13, 24, 100, 10, 2));
    internal static SyncTeXQueryOutcome Matches(params SyncTeXMatch[] matches) => new(SyncTeXQueryStatus.Completed, matches);
    internal Task<SyncTeXNavigationResult?> Inverse(CancellationToken token = default) => Policy.InverseAsync(1, 14, 20, token);

    internal async Task Change(string change)
    {
        var file = Workspace.Files.Single(x => x.Id == Main);
        switch (change)
        {
            case "edit": Assert.True(Workspace.EditText(file.Id, "changed")); break;
            case "ABA":
                string original = file.Content;
                Assert.True(Workspace.EditText(file.Id, "changed")); Assert.True(Workspace.EditText(file.Id, original)); break;
            case "other-file": Assert.True(Workspace.EditText(Workspace.Files.Single(x => x.RelativePath == "chapter.tex").Id, "changed")); break;
            case "save": Assert.Equal(CommitStatus.Saved, (await Workspace.SaveAsync(CancellationToken.None)).Status); break;
            case "selection": Session.SelectEngine("xelatex"); break;
            case "selection-ABA": Session.SelectEngine("xelatex"); Session.SelectEngine("contract"); break;
            case "cancel-compile": Session.Cancel(); break;
            case "rename": Assert.True(Workspace.RenameFile(file.Id, "renamed.tex")); break;
            case "delete": Assert.True(Workspace.DeleteFile(file.Id)); break;
            case "takeover": Assert.True(await Workspace.TakeOverAsync(CancellationToken.None)); break;
            case "close": await Workspace.CloseAsync(CancellationToken.None); break;
            case "reopen": await Workspace.CloseAsync(CancellationToken.None); await Workspace.OpenAsync(Storage.Repo.Project.Id, CancellationToken.None); break;
            case "project":
                var project = new Project(ProjectId.New(), "other-synthetic", null);
                Storage.Repo.Projects[project.Id] = (project, null);
                await Workspace.CloseAsync(CancellationToken.None); await Workspace.OpenAsync(project.Id, CancellationToken.None); break;
            case "dispose-policy": Policy.Dispose(); break;
            case "dispose-session": await Session.DisposeAsync(); break;
            case "samehash-compile": await Compile(); break;
            default: throw new ArgumentException("Unknown fixture change.", nameof(change));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Policy.Dispose();
        foreach (var worker in Config.Workers.Created) worker.Reply.TrySetResult(worker.Output(CompileOutputStatus.Failed));
        await Session.DisposeAsync();
        if (Session.Owner is { } owner) await owner.WhenSettled.WaitAsync(Bound);
        await Storage.DisposeAsync();
    }

    internal sealed class Configuration : ICompileWorkspaceConfiguration
    {
        internal string Engine = "synthetic-engine";
        internal bool Ready = true;
        internal readonly Workers Workers = new();
        public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(
            new(Ready, "Ready", "Contract only"), new("contract", "Contract only", CompilationTestWorkspace.Descriptor(Engine), [], [],
                Workers, null, () => Task.FromResult(new CompileReadiness(Ready, "Ready", "Contract only")))));
    }

    internal sealed class Queries : ISyncTeXQuery
    {
        internal readonly List<SyncTeXQueryRequest> Requests = [];
        internal Func<SyncTeXQueryRequest, Task<SyncTeXQueryOutcome>> Reply = _ => Task.FromResult(Matches(Match()));
        internal int Disposals;
        public Task<SyncTeXQueryOutcome> QueryAsync(SyncTeXQueryRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return Reply(request); } // Deliberately noncooperative cancellation.
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
