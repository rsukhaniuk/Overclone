using System.Text;
using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Compilation.Provisioning;
using Workspace.Tests.CompilationLifecycle;
using Xunit;

namespace Workspace.Tests.CompilerThesis;

// Actual C# capture/admission and closed inventory checks. Modeled replies are
// not native execution, corpus compilation or published-browser acceptance.
public sealed class ThesisProfileTests
{
    private const NativeRuntimeProfile P = NativeThesisCompilerContract.Profile;
    private static readonly CompileOutputBudget Output = new(8388608, 65536, 32, 128, 4096, 8192);

    [Fact]
    public void Profile14_is_explicit_while_reserved_and_unknown_profiles_stay_closed()
    {
        Assert.Equal(14, (int)P);
        Assert.True(NativeRuntimeProfiles.Known(P));
        Assert.True(NativeRuntimeProfiles.IsSuite(P));
        Assert.True(NativeRuntimeProfiles.HasBibliographyCarry(P));
        Assert.True(NativeRuntimeProfiles.SupportsBiber(P));
        Assert.Equal("local-compiler-thesis-v1", NativeRuntimeProfiles.IdentityName(P));
        Assert.Equal(6, NativeRuntimeProfiles.ProtocolVersion(P));
        Assert.True(LocalRuntimeSelection.LimitsFor(P).IsValid);
        Assert.Equal(new[] { 23, 24, 21, 22 }, Enum.GetValues<NativeCompilerEngine>().Select(engine => NativeCompilerSuiteContract.Phase(P, engine)));
        foreach (var engine in Enum.GetValues<NativeCompilerEngine>())
        {
            var plan = NativeCompilerSuiteContract.CreatePlan(engine, profile: P);
            Assert.True(plan.IsValid);
            Assert.Equal(NativeCompilerSuiteContract.Files + 256, plan.MaxPhaseFiles);
            Assert.False((plan with { MaxPhaseFiles = plan.MaxPhaseFiles + 1 }).IsValid);
            Assert.Equal(engine != NativeCompilerEngine.PdfLaTeX,
                NativeNavigationContract.IsMapped(P, plan.EffectivePhaseVersion, engine));
            var old = NativeCompilerSuiteContract.CreatePlan(engine, profile: NativeBiberCompilerContract.Profile);
            Assert.Equal(NativeCompilerSuiteContract.Files + 192, old.MaxPhaseFiles);
            Assert.False((old with { MaxPhaseFiles = NativeCompilerSuiteContract.Files + 256 }).IsValid);
        }
        foreach (var unknown in new[] { (NativeRuntimeProfile)11, (NativeRuntimeProfile)16, (NativeRuntimeProfile)int.MaxValue })
        {
            Assert.False(NativeRuntimeProfiles.Known(unknown));
            Assert.Equal(64, NativeRuntimeProfiles.ProjectFileLimit(unknown));
            Assert.Equal(128, NativeRuntimeProfiles.OutputDeclarationLimit(unknown));
        }
    }

    [Fact]
    public void Candidate_changes_only_declared_components_and_retains_the_genuine_bibtex_view()
    {
        Assert.Equal(128, NativeThesisCompilerContract.ProjectFiles);
        Assert.Equal(128, NativeThesisCompilerContract.CarriedFiles);
        Assert.Equal(128, NativeAuthoringV4Contract.CarriedFiles);
        Assert.Equal(256, NativeRuntimeProfiles.OutputDeclarationLimit(P));
        foreach (var engine in Enum.GetValues<NativeCompilerEngine>())
        {
            var previous = NativeCompilerComponents.For(NativeBiberCompilerContract.Profile, engine);
            var candidate = NativeCompilerComponents.For(P, engine);
            Assert.Equal(previous.FormatHash, candidate.FormatHash);
            Assert.Equal(previous.EngineVersion, candidate.EngineVersion);
            Assert.Equal(previous.NativeAbiVersion, candidate.NativeAbiVersion);
            Assert.Equal(previous.QueryWasmHash, candidate.QueryWasmHash);
            if (engine == NativeCompilerEngine.XeLaTeX)
            {
                Assert.Equal(NativeCompilerThesisPins.XeWasmHash, candidate.WasmHash);
                Assert.NotEqual(previous.WasmHash, candidate.WasmHash);
                Assert.Equal(previous.OutputAdapter, candidate.OutputAdapter); // same semantic converter
            }
            else if (engine == NativeCompilerEngine.LuaLaTeX)
                Assert.Equal(new Client.Workspace.Domain.SHA256(NativeCompilerThesisPins.LuaWasmHash), candidate.OutputAdapter.Hash);
            else Assert.Equal(previous, candidate);
        }
        Assert.Equal(NativeRuntimeProfiles.SyncTeXCompilerWasmHash, NativeBiberCompilerContract.BibtexComponent.WasmHash);
        Assert.Equal(64, NativeBiberCompilerContract.InputFiles); // unchanged genuine Biber bound
        if (!NativeCompilerThesisPins.Ready)
        {
            Assert.Equal(0, NativeRuntimeProfiles.InstalledCount(P));
            Assert.Equal("", NativeRuntimeProfiles.ClosureHash(P));
            Assert.False(NativeCompilerThesisPins.MatchesComponents(new Dictionary<string, NativeResourcePin>()));
            Assert.Equal(NativeRuntimeIssue.Unavailable, NativeRuntimePackage.Create([], [], LocalRuntimeSelection.LimitsFor(P), P).Issue);
        }
    }

    [Theory]
    [InlineData("notices/thesis-packages/caption/LICENSE", true)]
    [InlineData("notices/biber/README.txt", true)]
    [InlineData("notices/unrelated/LICENSE", false)]
    [InlineData("notices/thesis-packages/../LICENSE", false)]
    [InlineData("notices/thesis-packages/folder name/LICENSE", false)]
    public void New_notices_remain_in_the_closed_namespace(string name, bool allowed)
    {
        Assert.Equal(allowed, NativeThesisCompilerContract.NoticeName(name));
        if (name.StartsWith("notices/thesis-packages/", StringComparison.Ordinal))
            Assert.False(NativeBiberCompilerContract.NoticeName(name));
    }

    [Theory]
    [InlineData(63, 13, true)]
    [InlineData(64, 13, true)]
    [InlineData(65, 13, false)]
    [InlineData(127, 14, true)]
    [InlineData(128, 14, true)]
    [InlineData(129, 14, false)]
    [InlineData(127, 15, true)]
    [InlineData(128, 15, true)]
    [InlineData(129, 15, false)]
    public async Task Worker_project_count_is_bound_to_the_trusted_plan(int count, int profile, bool admitted)
    {
        await using var f = await WithCount(count);
        var trusted = (NativeRuntimeProfile)profile;
        f.Selection.SetSelection(CompilationTestWorkspace.Select(f.Main, options: NativeCompilerSuiteContract.SelectionOptions(trusted, NativeCompilerEngine.PdfLaTeX)));
        var context = CompileRequestContext.Create(await f.Snapshot(new(129, 1048576, 8388608, 131072)), 1, 65536).Context!;
        var factory = new NativeCompilationWorkerFactory(plan: NativeCompilerSuiteContract.CreatePlan(NativeCompilerEngine.PdfLaTeX, profile: trusted));
        var job = new CompileJobId(Guid.NewGuid());
        var worker = factory.Create(job, 1);
        var result = await worker.ExecuteAsync(job, context, Output, default)!;
        Assert.NotNull(result);
        Assert.Equal(admitted ? CompileOutputStatus.Unsupported : CompileOutputStatus.Failed, result.Status);
        Assert.Equal(admitted ? "runtime-unavailable" : "v4-project-count-limit", Assert.Single(result.Diagnostics!).Code);
        Assert.Equal(0, factory.Operations.Active);
    }

    private static async Task<CompilationTestWorkspace> WithCount(int count)
    {
        var fixture = await CompilationTestWorkspace.Open();
        Assert.True(fixture.Workspace.RenameFile(fixture.Main, "main.tex"));
        while (fixture.Workspace.Files.Count(file => !file.Deleted) < count)
            Assert.True(fixture.Workspace.CreateFile("chapter" + fixture.Workspace.Files.Count + ".tex", "Synthetic count fixture\n"));
        return fixture;
    }

    private sealed class Configuration(CompileWorkspacePorts ports) : ICompileWorkspaceConfiguration
    {
        public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(new(true, "Ready", "Modeled"), ports));
    }

    [Theory]
    [InlineData(64, null, true)]
    [InlineData(65, null, false)]
    [InlineData(65, 13, false)]
    [InlineData(65, 16, false)]
    [InlineData(127, 14, true)]
    [InlineData(128, 14, true)]
    [InlineData(129, 14, false)]
    [InlineData(127, 15, true)]
    [InlineData(128, 15, true)]
    [InlineData(129, 15, false)]
    public void Ui_capture_never_infers_larger_capacity_from_phase23_or_caller_descriptors(int count, int? profile, bool admitted) => LifecycleTestHost.Run(async () =>
    {
        await using var fixture = await WithCount(count);
        var workers = new LifecycleTestHost.Workers();
        var ports = new CompileWorkspacePorts("modeled-thesis", "Modeled", CompilationTestWorkspace.Descriptor("claimed-profile14"), [], [], workers, null,
            () => Task.FromResult(new CompileReadiness(true, "Ready", "Modeled")),
            SelectionOptions: NativeCompilerSuiteContract.SelectionOptions(P, NativeCompilerEngine.PdfLaTeX),
            NativeProfile: profile is null ? null : (NativeRuntimeProfile)profile);
        var session = new CompileWorkspaceSession(fixture.Workspace, new Configuration(ports));
        var owner = SynchronizationContext.Current!;
        session.AttachRenderer(action =>
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.Post(_ => { try { action(); done.SetResult(); } catch (Exception error) { done.SetException(error); } }, null);
            return done.Task;
        });
        try
        {
            await session.RefreshToolsAsync(); session.SelectMain(fixture.Main); session.SelectEngine(ports.CapabilityId);
            await session.CompileAsync();
            if (admitted)
            {
                var worker = await workers.Next();
                Assert.Equal(count, worker.Context.Snapshot.Files.Length);
                worker.Succeed();
                Assert.Equal(CompileLifecycleReason.Succeeded, (await session.LastTicket!.Completion).Reason);
            }
            else
            {
                Assert.Equal(CompileLifecycleReason.CaptureQuota, (await session.LastTicket!.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
                Assert.Empty(workers.Created);
            }
        }
        finally
        {
            foreach (var worker in workers.Created) worker.Succeed();
            await session.DisposeAsync(); await session.Owner!.WhenSettled.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    [Fact]
    public async Task Original66file_corpus_names_fit_new_capture_without_renaming_or_omission()
    {
        using var stream = typeof(ThesisProfileTests).Assembly.GetManifestResourceStream(typeof(ThesisProfileTests).Assembly.GetManifestResourceNames().Single(name => name.EndsWith(".public-kpi-gap.json", StringComparison.Ordinal)))!;
        using var inventory = JsonDocument.Parse(stream);
        var names = inventory.RootElement.GetProperty("projectFilePins").EnumerateArray().Select(pin => pin.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(66, names.Length);
        await using var f = await CompilationTestWorkspace.Open();
        foreach (var old in f.Workspace.Files.ToArray()) Assert.True(f.Workspace.DeleteFile(old.Id));
        // Name/admission test only: original project bytes are not synthesized
        // here and this is explicitly not a real thesis compilation claim.
        foreach (var name in names) Assert.True(f.Workspace.CreateFileFromBytes(name, Encoding.UTF8.GetBytes("Synthetic inventory-name fixture\n")));
        var main = f.Workspace.Files.Single(file => file.RelativePath == inventory.RootElement.GetProperty("root").GetString());
        f.Selection.SetSelection(CompilationTestWorkspace.Select(main.Id, options: NativeCompilerSuiteContract.SelectionOptions(P, NativeCompilerEngine.LuaLaTeX)));
        var capture = await f.Capture.CaptureAsync(new(128, 1048576, 8388608, 131072), default);
        var snapshot = Assert.IsType<CompilationCaptureResult.Captured>(capture).Snapshot;
        Assert.Equal(names.Order(StringComparer.Ordinal), snapshot.Files.Select(file => file.RelativePath).Order(StringComparer.Ordinal));
        var oldCapture = await f.Capture.CaptureAsync(new(64, 1048576, 8388608, 131072), default);
        Assert.Equal(CompilationCaptureIssue.FileCountLimit, Assert.IsType<CompilationCaptureResult.QuotaExceeded>(oldCapture).Issue);
        Assert.NotNull(NativeCompilerSuiteContract.CreateEntry(main.RelativePath, names, NativeCompilerEngine.LuaLaTeX, out var error, P));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(NativeCompilerEngine.PdfLaTeX)]
    [InlineData(NativeCompilerEngine.LaTeXDvi)]
    [InlineData(NativeCompilerEngine.XeLaTeX)]
    [InlineData(NativeCompilerEngine.LuaLaTeX)]
    public void All128tex_sources_have_an_admissible_output_declaration_without_changing_carried_limit(NativeCompilerEngine engine)
    {
        var paths = Enumerable.Range(0, 128).Select(index => index == 0 ? "main.tex" : "chapters/part" + index + ".tex").ToArray();
        var entry = NativeCompilerSuiteContract.CreateEntry("main.tex", paths, engine, out var error, P);
        Assert.Null(error); Assert.NotNull(entry);
        entry = NativeBiberCompilerContract.ExtendEntry(entry);
        Assert.InRange(entry.Outputs.Length, 129, NativeThesisCompilerContract.Outputs);
        Assert.Null(NativeBiberCompilerContract.ValidateNamespace(entry, paths, []));
        if (engine == NativeCompilerEngine.LuaLaTeX) Assert.True(NativeThesisCompilerContract.LuaOutputDeclarationsFit(entry.Outputs));
        Assert.Equal(128, NativeAuthoringV4Contract.CarriedFiles);
        Assert.Equal(256, NativeThesisCompilerContract.ProjectFiles + NativeAuthoringV4Contract.CarriedFiles);
    }

    [Theory]
    [InlineData(245, true)]
    [InlineData(246, true)]
    [InlineData(247, false)]
    public void Lua256declarations_include_all10_native_defaults(int requested, bool fits)
    {
        var outputs = Enumerable.Range(0, requested).Select(index => "part" + index + ".aux").ToArray();
        Assert.Equal(fits, NativeThesisCompilerContract.LuaOutputDeclarationsFit(outputs));
    }

    [Fact]
    public void Lua256requested_names_without_native_format_would_require257_and_refuse()
    {
        string[] defaults = ["result.pdf", "result.log", "lualatex.fmt", "result.aux", "result.toc", "result.out", "result.bbl", "result.blg", "result.lof", "result.lot"];
        var full = defaults.Concat(Enumerable.Range(0, 246).Select(index => "part" + index + ".aux")).ToArray();
        Assert.True(NativeThesisCompilerContract.LuaOutputDeclarationsFit(full));
        var missingFormat = full.Where(name => name != "lualatex.fmt").Append("another.aux").ToArray();
        Assert.Equal(256, missingFormat.Length);
        Assert.False(NativeThesisCompilerContract.LuaOutputDeclarationsFit(missingFormat));
        Assert.False(NativeThesisCompilerContract.LuaOutputDeclarationsFit(full.Append("another.aux").ToArray()));
    }
}
