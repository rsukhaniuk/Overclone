using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Workspace.Tests.CompilationLifecycle;
using Xunit;

namespace Workspace.Tests.CompilationNative;

// Modeled transport only. Real497 package/capture/canonical hashing; NO native execution/PDF proof.
public sealed class NativeAuthoringV4Tests
{
    private const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalLatex2023Authoring;
    private static readonly Lazy<NativeRuntimePackage> Runtime = new(() =>
    {
        string root = Environment.GetEnvironmentVariable("AUTHORING_RUNTIME_DIR")!;
        var pins = JsonSerializer.Deserialize<NativeResourcePin[]>(File.ReadAllBytes(Path.Combine(root, "pins.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(pins.Select(p => new NativeResourceInput(p.Name,
            File.ReadAllBytes(Path.Combine(root, "files", p.Name)))).ToArray(), pins, new(512, 33554432, 67108864, Profile), Profile).Package);
    });
    private static readonly CompileOutputBudget Budget = new(8388608, 65536, 32, 128, 4096, 8192);
    private static readonly CompilationCaptureBudget CaptureBudget = new(64, 1048576, 8388608, 131072);
    private static void Select(CompilationTestWorkspace f, bool bib = false, CompilationOption[]? options = null)
        => f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, Runtime.Value.Engine, [Runtime.Value.Bundle],
            bib ? [Runtime.Value.Bibtex] : [], options ?? NativeAuthoringV4Contract.SelectionOptions), 65536).Selection!);
    private static async Task<CompileRequestContext> Context(CompilationTestWorkspace f)
        => CompileRequestContext.Create(await f.Snapshot(CaptureBudget), 1, 65536).Context!;
    private static async Task<CompileOutputEnvelopeInput> Execute(CompileRequestContext context, Transport transport, NativeCompilePlan? plan = null, INativeRuntimeProvider? provider = null)
    {
        var factory = new NativeCompilationWorkerFactory(provider ?? new FixedNativeRuntimeProvider(Runtime.Value), transport, plan ?? NativeAuthoringV4Contract.CreatePlan());
        var id = new CompileJobId(Guid.NewGuid());
        var operation = Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(factory.Create(id, 1).ExecuteAsync(id, context, Budget, default));
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(20));
        await factory.Operations.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        return Assert.IsType<CompileOutputEnvelopeInput>(result);
    }
    private sealed class Provider : INativeRuntimeProvider
    {
        public int Calls;
        public Task<NativeRuntimePackage?> GetAsync() { Calls++; throw new InvalidOperationException("Must reject before provider IO"); }
    }
    private sealed class Transport : INativePhaseTransport
    {
        public readonly List<NativePhaseRequest> Calls = [];
        public Func<int, byte[]?> Toc = _ => [1];
        public Func<NativePhaseReply, NativePhaseReply>? Mutate;
        public bool Warning;
        private int _latex;
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest r)
        {
            Calls.Add(r);
            var state = r.Files.Where(f => r.Outputs.Contains(f.Name) && NativeAuthoringV4Contract.IsState(f.Name))
                .ToDictionary(f => f.Name, f => f.Bytes, StringComparer.Ordinal);
            if (r.Tag.Tool == "bibtex") state["main.bbl"] = [2];
            else
            {
                _latex++;
                foreach (string name in r.Outputs.Where(n => n.EndsWith(".aux", StringComparison.Ordinal))) state[name] = [1];
                if (Toc(_latex) is { } toc) state["main.toc"] = toc; else state.Remove("main.toc");
                foreach (string name in new[] { "main.lof", "main.lot", "main.out" }) state[name] = [1];
            }
            var files = state.Select(f => new NativeFileTransfer(f.Key, f.Value)).ToList();
            if (r.Tag.Tool == "latex") files.Add(new("main.pdf", "%PDF-modeled-v4"u8.ToArray()));
            var reply = new NativePhaseReply(r.Tag, "native-return", 0, true, Warning ? "Rerun" : "MODELED only", files.ToArray(),
                r.Files.Select(f => new NativeInputFact(f.Name, f.Bytes.Length, NativeRuntimePackage.Digest(f.Bytes).Hex)).ToArray(),
                WasmSha256: NativeRuntimePackage.WasmHash, LoaderSha256: NativeRuntimePackage.LoaderHash);
            return Task.FromResult(Mutate?.Invoke(reply) ?? reply);
        }
        public void RequestTermination(string job, long epoch) { }
    }
    [Fact]
    public void Defaults_profiles_bytes_deadlines_and_explicit_tuples_are_closed()
    {
        foreach (var profile in Enum.GetValues<NativeRuntimeProfile>().Where(profile => (int)profile <= 2))
        {
            var plan = new NativeCompilePlan(false, 4, TimeSpan.FromSeconds(25), Profile: profile);
            Assert.True(plan.IsValid); Assert.Null(plan.PhaseProtocolVersion); Assert.Equal(512, plan.MaxPhaseFiles);
            Assert.Equal((int)profile + 1, plan.EffectivePhaseVersion); Assert.Equal((int)profile + 1, NativeRuntimeProfiles.ProtocolVersion(profile));
            Assert.True((plan with { PhaseProtocolVersion = plan.EffectivePhaseVersion }).IsValid);
            Assert.False((plan with { PhaseProtocolVersion = 6 }).IsValid);
            Assert.False((plan with { MaxPhaseFiles = 513 }).IsValid);
            Assert.Equal(16777216, plan.MaxInputBytes); Assert.Equal(8388608, plan.MaxFileBytes);
        }
        var v4 = NativeAuthoringV4Contract.CreatePlan(); Assert.True(v4.IsValid); Assert.Equal(4, v4.EffectivePhaseVersion);
        Assert.Equal(768, v4.MaxPhaseFiles); Assert.Equal(67108864, v4.MaxInputBytes); Assert.Equal(33554432, v4.MaxFileBytes);
        Assert.True(v4.UseSelectionTools); Assert.Equal(6, v4.MaxLatexPasses);
        Assert.False((v4 with { Profile = NativeRuntimeProfile.Legacy }).IsValid);
        Assert.False((v4 with { Profile = NativeRuntimeProfile.LocalLatex2023 }).IsValid);
        Assert.False((v4 with { PhaseProtocolVersion = 3 }).IsValid); //768 is not a v3 phase limit
        Assert.False((v4 with { MaxPhaseFiles = 769 }).IsValid);
        Assert.False((v4 with { MaxLatexPasses = 7 }).IsValid);
        Assert.False((v4 with { JobDeadline = TimeSpan.FromSeconds(121) }).IsValid);
    }
    [Theory]
    [InlineData("n6.map", "N6.MAP/child.tex")]
    [InlineData("main.toc", "main.toc/child.tex")]
    [InlineData("é.tex", "e\u0301.tex")]
    public void Native_namespace_is_NFC_ASCII_casefold_and_prefix_safe(string first, string second)
    {
        Assert.Equal("v4-namespace-collision", NativeAuthoringV4Contract.ValidateNamespace([first, second], [], []));
    }
    [Fact]
    public void Paths_and_approved_state_are_bounded_without_renaming()
    {
        Assert.Null(NativeAuthoringV4Contract.ValidatePath(new string('x', 245) + ".tex"));
        Assert.Equal("v4-native-path-limit", NativeAuthoringV4Contract.ValidatePath(new string('x', 246) + ".tex"));
        foreach (string name in new[] { "/main.tex", "../main.tex", "a//b", "a\\b", "a/./b", "a\0b", " main.tex", "NUL.txt", ".git/config", "a\ud800" })
            Assert.NotNull(NativeAuthoringV4Contract.ValidatePath(name));
        Assert.Equal("v4-invalid-state", NativeAuthoringV4Contract.ValidateNamespace(["main.tex"], ["main.aux"], ["main.idx"]));
        Assert.Null(NativeAuthoringV4Contract.ValidateNamespace(["main.tex"], ["main.aux"], ["main.aux"]));
        Assert.Equal("v4-namespace-collision", NativeAuthoringV4Contract.ValidateNamespace(["main.aux"], ["main.aux"], []));
        Assert.Equal(72, NativeAuthoringV4Contract.SelectedOutputs(Enumerable.Range(0, 64).Select(i => i == 0 ? "main.tex" : $"c{i}.tex")).Length);
    }
    [AuthoringV4FixtureFact]
    public void Canonical_option_changes_real_digest_and_old_artifact_binding() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Assert.True(f.Workspace.RenameFile(f.Main, "main.tex"));
        Select(f, options: []); var v3 = await Context(f);
        Select(f); var v4 = await Context(f); Assert.NotEqual(v3.InputContentDigest, v4.InputContentDigest);
        Assert.Equal(v3.Snapshot.Selection.Engine, v4.Snapshot.Selection.Engine); Assert.True(v3.Snapshot.Selection.Bundles.SequenceEqual(v4.Snapshot.Selection.Bundles));
        var t = new Transport(); var result = await Execute(v4, t); Assert.Equal(CompileOutputStatus.Succeeded, result.Status);
        var envelope = CompileOutputEnvelope.Create(result, Budget).Envelope!;
        Assert.NotNull(envelope.TryAccept(v4).Output); Assert.Null(envelope.TryAccept(v3).Output);
        Assert.All(t.Calls, r => { Assert.Equal(4, r.Tag.Version); Assert.Equal(Profile, r.Tag.Profile); Assert.Equal(Runtime.Value.Identity.Hex, r.Tag.RuntimeIdentity); });
    });
    [AuthoringV4FixtureTheory]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("unknown")]
    [InlineData("extra")]
    public void Bad_options_reject_before_provider_io(string kind) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex");
        CompilationOption[] options = kind switch { "missing" => [], "wrong" => [new("overclone-native-phase-contract", "3")],
            "unknown" => [new("unknown", "4")], _ => [NativeAuthoringV4Contract.SelectionOption, new("extra", "4")] };
        Select(f, options: options); var p = new Provider(); var t = new Transport();
        Assert.Equal(CompileOutputStatus.Unsupported, (await Execute(await Context(f), t, provider: p)).Status);
        Assert.Equal(0, p.Calls); Assert.Empty(t.Calls);
        Assert.Null(CompilationSelection.Create(new(f.Main, Runtime.Value.Engine, [Runtime.Value.Bundle], [],
            [NativeAuthoringV4Contract.SelectionOption, NativeAuthoringV4Contract.SelectionOption]), 65536).Selection);
    });
    [AuthoringV4FixtureTheory]
    [InlineData("n6.map/child.tex", "v4-namespace-collision")]
    [InlineData("N6.MAP", "v4-namespace-collision")]
    [InlineData("main.toc/child.tex", "v4-namespace-collision")]
    [InlineData("main.bbl", "generated-input-collision")]
    public void Runtime_source_output_topology_rejects_before_dispatch(string name, string code) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex");
        Assert.True(f.Workspace.CreateFile(name, "unchanged")); Select(f); var t = new Transport();
        var result = await Execute(await Context(f), t); Assert.Equal(CompileOutputStatus.Failed, result.Status);
        Assert.Equal(code, Assert.Single(result.Diagnostics!).Code); Assert.Empty(t.Calls);
    });
    [AuthoringV4FixtureFact]
    public void All64_projects_and_declared_state_fit_each_actual_modeled_frame() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex"); var main = f.Main;
        foreach (var file in f.Workspace.Files.Where(x => x.Id != main).ToArray()) Assert.True(f.Workspace.DeleteFile(file.Id));
        for (int i = 0; i < 62; i++) Assert.True(f.Workspace.CreateFile($"chapter{i:D2}.tex", "tiny chapter"));
        Assert.True(f.Workspace.CreateFile("refs.bib", "tiny reference")); Select(f, bib: true); var c = await Context(f);
        Assert.Equal(64, c.Snapshot.Files.Length); var t = new Transport(); var result = await Execute(c, t);
        Assert.Equal(CompileOutputStatus.Succeeded, result.Status); Assert.Equal(new[] { "latex", "bibtex", "latex", "latex" }, t.Calls.Select(r => r.Tag.Tool));
        Assert.Equal(559, t.Calls[0].Files.Length); Assert.All(t.Calls, r => { Assert.InRange(r.Files.Length, 559, 768); Assert.Equal(71, r.Outputs.Length); });
        Assert.Contains(t.Calls[2].Files, f => f.Name == "main.toc"); Assert.Contains(t.Calls[2].Files, f => f.Name == "chapter61.aux");
        var limited = new Transport(); var refusal = await Execute(c, limited, NativeAuthoringV4Contract.CreatePlan() with { MaxPhaseFiles = 559 });
        Assert.Equal("v4-state-budget", Assert.Single(refusal.Diagnostics!).Code); Assert.Single(limited.Calls);
    });
    [AuthoringV4FixtureTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Toc_change_or_deletion_prevents_premature_aux_only_convergence(bool deletion) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex"); Select(f);
        var t = new Transport { Toc = pass => pass == 1 ? [1] : deletion ? null : [2] };
        Assert.Equal(CompileOutputStatus.Succeeded, (await Execute(await Context(f), t)).Status); Assert.Equal(3, t.Calls.Count);
        Assert.All(t.Calls, r => Assert.Equal("latex", r.Tag.Tool));
        var third = t.Calls[2].Files.SingleOrDefault(f => f.Name == "main.toc");
        if (deletion) Assert.Null(third); else Assert.Equal(new byte[] { 2 }, third!.Bytes);
    });
    [AuthoringV4FixtureTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_state_churn_or_existing_warning_hits_finite_nonconvergence(bool warning) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex"); Select(f);
        var t = new Transport { Toc = pass => [(byte)(warning ? 1 : pass)], Warning = warning };
        var result = await Execute(await Context(f), t); Assert.Equal(CompileOutputStatus.Failed, result.Status);
        Assert.Equal("pass-limit-unknown", Assert.Single(result.Diagnostics!).Code); Assert.Equal(6, t.Calls.Count);
    });
    [AuthoringV4FixtureTheory]
    [InlineData("tag")]
    [InlineData("epoch")]
    [InlineData("input")]
    [InlineData("output")]
    public void Forged_reply_never_promotes_state_or_artifact(string kind) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex"); Select(f);
        var t = new Transport { Mutate = r => kind switch { "tag" => r with { Tag = r.Tag with { Version = 3 } },
            "epoch" => r with { Tag = r.Tag with { Epoch = 999 } }, "input" => r with { Inputs = [] },
            _ => r with { Files = [.. r.Files, new("main.idx", [1])] } } };
        var result = await Execute(await Context(f), t); Assert.Equal("invalid-native-reply", Assert.Single(result.Diagnostics!).Code); Assert.Single(t.Calls);
    });
    [AuthoringV4FixtureFact]
    public void State_aggregate_refuses_atomically_before_next_frame() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex"); Select(f);
        int replies = 0;
        var t = new Transport { Mutate = r => ++replies == 1 ? r : r with { Files = [new("main.aux", new byte[4194304]), new("main.toc", new byte[4194305])] } };
        var result = await Execute(await Context(f), t); Assert.Equal("v4-state-budget", Assert.Single(result.Diagnostics!).Code); Assert.Equal(2, t.Calls.Count);
        Assert.Equal(new byte[] { 1 }, t.Calls[1].Files.Single(x => x.Name == "main.toc").Bytes);
    });
    [AuthoringV4FixtureFact]
    public void Project65_refuses_before_provider_even_with_larger_capture_budget() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex");
        for (int i = 0; i < 61; i++) Assert.True(f.Workspace.CreateFile($"extra{i}.txt", "tiny"));
        Select(f); var snapshot = await f.Snapshot(CaptureBudget with { MaxFiles = 65 }); Assert.Equal(65, snapshot.Files.Length);
        var context = CompileRequestContext.Create(snapshot, 1, 65536).Context!; var p = new Provider(); var t = new Transport();
        var result = await Execute(context, t, provider: p); Assert.Equal("v4-project-count-limit", Assert.Single(result.Diagnostics!).Code);
        Assert.Equal(0, p.Calls); Assert.Empty(t.Calls);
    });
    [AuthoringV4FixtureTheory]
    [InlineData(245, true)]
    [InlineData(246, false)]
    public void Native_lookup_path_budget_is_checked_before_phase_copy(int length, bool supported) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex");
        string path = new string('x', length) + ".tex"; Assert.True(f.Workspace.CreateFile(path, "unchanged bytes")); Select(f);
        var c = await Context(f); var bytes = c.Snapshot.Files.Single(x => x.RelativePath == path).ToArray(); var t = new Transport();
        var result = await Execute(c, t);
        Assert.Equal(bytes, c.Snapshot.Files.Single(x => x.RelativePath == path).ToArray());
        if (supported) Assert.Equal(CompileOutputStatus.Succeeded, result.Status);
        else { Assert.Equal("v4-native-path-limit", Assert.Single(result.Diagnostics!).Code); Assert.Empty(t.Calls); }
    });
    [AuthoringV4FixtureFact]
    public void Unsupported_tool_and_v3_option_cannot_silently_run_v4() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex");
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, Runtime.Value.Engine, [Runtime.Value.Bundle],
            [Runtime.Value.Bibtex with { Id = "makeindex" }], NativeAuthoringV4Contract.SelectionOptions), 65536).Selection!);
        var t = new Transport(); Assert.Equal(CompileOutputStatus.Unsupported, (await Execute(await Context(f), t)).Status); Assert.Empty(t.Calls);
        Select(f); var v3 = NativeAuthoringV4Contract.CreatePlan() with { PhaseProtocolVersion = null, MaxPhaseFiles = 512 };
        Assert.Equal(CompileOutputStatus.Unsupported, (await Execute(await Context(f), t, v3)).Status); Assert.Empty(t.Calls);
    });
    [Fact]
    public void Trusted_port_options_reach_actual_session_capture_and_selection_guard() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex");
        var workers = new LifecycleTestHost.Workers(); var options = NativeAuthoringV4Contract.SelectionOptions.ToList();
        var config = new Configuration(new("v4-test", "modeled", CompilationTestWorkspace.Descriptor(), [], [], workers, null,
            () => Task.FromResult(new CompileReadiness(true, "Ready", "modeled")), SelectionOptions: options));
        var ui = SynchronizationContext.Current!; var session = new CompileWorkspaceSession(f.Workspace, config);
        session.AttachRenderer(action => { var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(_ => { try { action(); done.SetResult(); } catch (Exception e) { done.SetException(e); } }, null); return done.Task; });
        try
        {
            await session.RefreshToolsAsync(); session.SelectMain(f.Main); session.SelectEngine("v4-test"); await session.CompileAsync();
            var old = await workers.Next(); var ticket = session.LastTicket!; Assert.Equal(NativeAuthoringV4Contract.SelectionOptions, old.Context.Snapshot.Selection.Options);
            options.Clear(); session.SelectEngine("v4-test"); old.Reply.TrySetResult(old.Output());
            Assert.NotEqual(CompileLifecycleReason.Succeeded, (await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Reason); Assert.Null(session.DisplayedOutput);
            await session.CompileAsync(); var next = await workers.Next(); Assert.Empty(next.Context.Snapshot.Selection.Options);
            Assert.NotEqual(old.Context.InputContentDigest, next.Context.InputContentDigest); next.Reply.TrySetResult(next.Output());
            await session.LastTicket!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { foreach (var w in workers.Created) w.Reply.TrySetResult(w.Output()); await session.DisposeAsync(); await session.Owner!.WhenSettled.WaitAsync(TimeSpan.FromSeconds(5)); }
    });
    private sealed class Configuration(CompileWorkspacePorts ports) : ICompileWorkspaceConfiguration
    {
        public Task<CompileWorkspaceConfigurationResult> OpenAsync() => Task.FromResult(new CompileWorkspaceConfigurationResult(new(true, "Ready", "modeled"), ports));
    }
}
public sealed class AuthoringV4FixtureFactAttribute : FactAttribute
{
    public AuthoringV4FixtureFactAttribute() { if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("AUTHORING_RUNTIME_DIR") ?? "", "pins.json"))) Skip = "Explicit497 authoring fixture absent; no native substitute."; }
}
public sealed class AuthoringV4FixtureTheoryAttribute : TheoryAttribute
{
    public AuthoringV4FixtureTheoryAttribute() { if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("AUTHORING_RUNTIME_DIR") ?? "", "pins.json"))) Skip = "Explicit497 authoring fixture absent; no native substitute."; }
}
