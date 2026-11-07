using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Workspace.Tests.CompilationLifecycle;
using Xunit;

namespace Workspace.Tests.CompilerUnicodeTransport;

public sealed class UnicodeSelectionTests
{
    [Theory]
    [InlineData("exact", true)]
    [InlineData("old-phase", false)]
    [InlineData("future-phase", false)]
    [InlineData("old-engine", false)]
    [InlineData("old-version", false)]
    [InlineData("other-engine", false)]
    [InlineData("extra-option", false)]
    public void Emoji_root_requires_exact_configured_Xe_candidate_and_phase(string mode, bool allowed)
        => LifecycleTestHost.Run(async () =>
        {
            await using var fixture = await CompilationTestWorkspace.Open();
            Assert.True(fixture.Workspace.RenameFile(fixture.Main, "тези/розділ 😀.tex"));
            var workers = new LifecycleTestHost.Workers();
            var engine = new CompilationDescriptor("xelatex", "maps-abi1-nonbmp1", new(NativeCompilerUnicodePins.XeWasmHash));
            CompilationOption[] options = NativeCompilerSuiteContract.SelectionOptions(NativeUnicodeCompilerContract.Profile, NativeCompilerEngine.XeLaTeX);
            if (mode == "old-phase") options = NativeCompilerSuiteContract.SelectionOptions(NativeGraphicsNavigationContract.Profile, NativeCompilerEngine.XeLaTeX);
            if (mode == "future-phase") options = [new("overclone-native-phase-contract", "23")];
            if (mode == "old-engine") engine = engine with { Hash = new(NativeCompilerXeNavigationPins.WasmHash) };
            if (mode == "old-version") engine = engine with { Version = "maps-abi1" };
            if (mode == "other-engine") engine = engine with { Id = "lualatex" };
            if (mode == "extra-option") options = [.. options, new("untrusted-capability", "unicode")];
            var ports = new CompileWorkspacePorts("unicode-xe", "Explicit candidate", engine,
                [new("synthetic-test-bundle", "1", new(new string('1', 64)))], [], workers, null,
                () => Task.FromResult(new CompileReadiness(true, "Ready", "Synthetic configured port")),
                SelectionOptions: options,
                OutputAdapter: NativeUnicodeCompilerContract.Component(NativeCompilerEngine.XeLaTeX).OutputAdapter);
            var session = new CompileWorkspaceSession(fixture.Workspace, new Configuration(ports));
            var owner = SynchronizationContext.Current!;
            session.AttachRenderer(action =>
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                owner.Post(_ => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } }, null);
                return completion.Task;
            });
            try
            {
                await session.RefreshToolsAsync(); session.SelectMain(fixture.Main); session.SelectEngine(ports.CapabilityId);
                await session.CompileAsync();
                if (allowed)
                {
                    var worker = await workers.Next();
                    Assert.Equal("тези/розділ 😀.tex", worker.Context.Snapshot.Files.Single(f => f.FileId == fixture.Main).RelativePath);
                    Assert.Equal(engine, worker.Context.Snapshot.Selection.Engine);
                    worker.Reply.SetResult(worker.Output(CompileOutputStatus.Failed));
                    Assert.NotNull(session.LastTicket);
                    await session.LastTicket!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else { Assert.Empty(workers.Created); Assert.Null(session.LastTicket); Assert.NotEmpty(session.Notice); }
            }
            finally
            {
                foreach (var worker in workers.Created) worker.Reply.TrySetResult(worker.Output(CompileOutputStatus.Failed));
                await session.DisposeAsync();
                if (session.Owner is { } context) await context.WhenSettled.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });

    private sealed class Configuration(CompileWorkspacePorts ports) : ICompileWorkspaceConfiguration
    {
        public Task<CompileWorkspaceConfigurationResult> OpenAsync()
            => Task.FromResult(new CompileWorkspaceConfigurationResult(new(true, "Ready", "Synthetic configured ports"), ports));
    }
}
