using System.Collections;
using System.Text;
using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Native;
using Client.Compilation.Lifecycle;
using Xunit;
using Workspace.Tests.CompilationLifecycle;

namespace Workspace.Tests.CompilationNative;

// Fake phase replies below are CONTRACT tests only, never native/PDF evidence.
public sealed class NativePortTests
{
    private static readonly Lazy<NativeRuntimePackage> Runtime = new(LoadRuntime);
    private static NativeRuntimePackage LoadRuntime()
    {
        var root = Environment.GetEnvironmentVariable("N11_RUNTIME_DIR") ?? throw new InvalidOperationException("Explicit test runtime missing.");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "pins.json")));
        var pins = json.RootElement.GetProperty("resources").EnumerateArray().Select(p => new NativeResourcePin(p.GetProperty("name").GetString()!, p.GetProperty("length").GetInt32(), p.GetProperty("sha256").GetString()!)).ToArray();
        var input = pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(root, "files", p.Name)))).ToArray();
        return Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(input, pins, new(512, 16 * 1024 * 1024, 32 * 1024 * 1024)).Package);
    }
    private static async Task<CompilationTestWorkspace> Fixture()
    {
        var f = await CompilationTestWorkspace.Open();
        Assert.True(f.Workspace.RenameFile(f.Main, "main.tex"));
        f.Selection.SetSelection(Assert.IsType<CompilationSelection>(CompilationSelection.Create(new(f.Main, Runtime.Value.Engine, [Runtime.Value.Bundle], [Runtime.Value.Bibtex], []), 65536).Selection));
        return f;
    }
    private static void SelectTools(CompilationTestWorkspace f, params CompilationDescriptor[] tools)
        => f.Selection.SetSelection(Assert.IsType<CompilationSelection>(CompilationSelection.Create(
            new(f.Main, Runtime.Value.Engine, [Runtime.Value.Bundle], tools, []), 65536).Selection));
    private static async Task<CompileRequestContext> Context(CompilationTestWorkspace f, long epoch = 1)
        => Assert.IsType<CompileRequestContext>(CompileRequestContext.Create(await f.Snapshot(), epoch, 65536).Context);
    private static readonly CompileOutputBudget Budget = new(1024 * 1024, 65536, 32, 128, 4096, 8192);
    private static readonly NativeCompilePlan Plan = new(true, 4, TimeSpan.FromSeconds(10));
    private sealed class Provider(Func<Task<NativeRuntimePackage?>> get) : INativeRuntimeProvider
    { public Task<NativeRuntimePackage?> GetAsync() => get(); }
    private sealed class Transport : INativePhaseTransport
    {
        internal readonly List<NativePhaseRequest> Calls = [];
        internal readonly List<(string, long)> Stops = [];
        internal Func<NativePhaseRequest, Task<NativePhaseReply>>? Handler;
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest request) { Calls.Add(request); return Handler?.Invoke(request) ?? Task.FromResult(Reply(request)); }
        public void RequestTermination(string job, long epoch) => Stops.Add((job, epoch));
        internal static NativePhaseReply Reply(NativePhaseRequest r) => new(r.Tag, "native-return", 0, true, "contract-only log\n",
            r.Tag.Tool == "bibtex" ? [new("main.bbl", Encoding.UTF8.GetBytes("contract-generated-bbl"))] : [new("main.pdf", Encoding.ASCII.GetBytes("%PDF-contract-only")), new("main.aux", [1, 2, 3])],
            r.Files.Select(f => new NativeInputFact(f.Name, f.Bytes.Length, NativeRuntimePackage.Digest(f.Bytes).Hex)).ToArray(),
            WasmSha256: NativeRuntimePackage.WasmHash, LoaderSha256: NativeRuntimePackage.LoaderHash);
    }
    private static NativeCompilationWorkerFactory Factory(Transport t, INativeRuntimeProvider? p = null, NativeCompilePlan? plan = null, NativeForeignOperations? operations = null)
        => new(p ?? new FixedNativeRuntimeProvider(Runtime.Value), t, plan ?? Plan, operations);

    private static Task<CompileOutputEnvelopeInput?> Execute(ICompilationWorker worker, CompileJobId id, CompileRequestContext context, CancellationToken token = default)
        => Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(worker.ExecuteAsync(id, context, Budget, token));

    [Fact]
    public void Missing_runtime_uses_existing_lifecycle_unsupported_without_transport() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var factory = new NativeCompilationWorkerFactory();
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, factory, new(2, 100), TimeProvider.System);
        var result = await service.Request(LifecycleTestHost.Options).Completion;
        Assert.Equal(CompileLifecycleReason.Unsupported, result.Reason); Assert.Null(result.Output); Assert.NotNull(result.Attempt);
        await service.WhenIdle; Assert.Equal(0, factory.Operations.Active);
    });
    [Theory]
    [InlineData("xelatex")]
    [InlineData("lualatex")]
    [InlineData("latex")]
    public void Other_engines_do_not_fall_back_to_pdftex(string engine) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture();
        f.Selection.SetSelection(Assert.IsType<CompilationSelection>(CompilationSelection.Create(new(f.Main,
            Runtime.Value.Engine with { Id = engine }, [Runtime.Value.Bundle], [Runtime.Value.Bibtex], []), 65536).Selection));
        var t = new Transport(); var factory = Factory(t); var id = new CompileJobId(Guid.NewGuid());
        var result = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.Equal(CompileOutputStatus.Unsupported, result!.Status); Assert.Empty(t.Calls); await factory.Operations.WhenIdle;
    });
    [Fact]
    public void Lifecycle_timeout_does_not_free_unfinished_provider_lease() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var held = new TaskCompletionSource<NativeRuntimePackage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new NativeForeignOperations(1); var t = new Transport(); var factory = Factory(t, new Provider(() => { reached.TrySetResult(); return held.Task; }), operations: gate);
        var clock = new LifecycleTestHost.Clock(); await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, factory, new(2, 100), clock);
        try
        {
            var ticket = service.Request(LifecycleTestHost.Options); await reached.Task; clock.Advance(TimeSpan.FromSeconds(11));
            Assert.Equal(CompileLifecycleReason.TimedOut, (await ticket.Completion).Reason); await service.WhenIdle;
            Assert.Equal(1, gate.Active); Assert.Empty(t.Calls);
        }
        finally { held.TrySetResult(Runtime.Value); await service.WhenIdle; await gate.WhenIdle; }
    });

    [Fact]
    public void Inventory_count_preflight_does_not_access_hostile_entries()
    {
        var result = NativeRuntimePackage.Create(new TooMany(), [], new(512, 1024, 4096));
        Assert.Equal(NativeRuntimeIssue.ResourceLimit, result.Issue);
    }
    private sealed class TooMany : IReadOnlyList<NativeResourceInput>
    {
        public int Count => 513; public NativeResourceInput this[int i] => throw new InvalidOperationException("entry touched");
        public IEnumerator<NativeResourceInput> GetEnumerator() => throw new InvalidOperationException("enumerated");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    [Fact]
    public void Runtime_returns_defensive_arrays_and_exact_pinned_engine()
    {
        var a = Runtime.Value.Copy("engine.wasm"); a[0] ^= 255;
        Assert.Equal(NativeRuntimePackage.WasmHash, NativeRuntimePackage.Digest(Runtime.Value.Copy("engine.wasm")).Hex);
        Assert.Equal(NativeRuntimePackage.FormatHash, NativeRuntimePackage.Digest(Runtime.Value.Copy("pdflatex.fmt")).Hex);
    }
    [Fact]
    public void Format_resource_maps_once_to_native_vfs_without_changing_inventory_identity() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var t = new Transport(); var factory = Factory(t); var id = new CompileJobId(Guid.NewGuid());
        var output = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.Equal(CompileOutputStatus.Succeeded, output!.Status);
        Assert.Equal("bd23f564e4b86ba7298ad1aa68352ccccd73df9770989fa52b9a32671d458e5f", Runtime.Value.Identity.Hex);
        foreach (var request in t.Calls)
        {
            Assert.DoesNotContain(request.Files, x => x.Name == "pdflatex.fmt");
            var format = Assert.Single(request.Files, x => x.Name == "swiftlatexpdftex.fmt");
            Assert.Equal(715877, format.Bytes.Length); Assert.Equal(NativeRuntimePackage.FormatHash, NativeRuntimePackage.Digest(format.Bytes).Hex);
            Assert.Equal(request.Files.Length, request.Files.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count());
            format.Bytes[0] ^= 255;
        }
        Assert.Equal(NativeRuntimePackage.FormatHash, NativeRuntimePackage.Digest(Runtime.Value.Copy("pdflatex.fmt")).Hex);
        await factory.Operations.WhenIdle;
    });
    [Fact]
    public void Different_runtime_resource_cannot_occupy_mapped_format_destination()
    {
        var original = new[] { "engine.js", "engine.wasm", "pdflatex.fmt" }.Select(n => new NativeResourceInput(n, Runtime.Value.Copy(n))).ToArray();
        var input = original.Append(new NativeResourceInput("swiftlatexpdftex.fmt", Encoding.ASCII.GetBytes("different resource"))).ToArray();
        var pins = input.Select(x => new NativeResourcePin(x.Name, x.Bytes.Length, NativeRuntimePackage.Digest(x.Bytes).Hex)).ToArray();
        var result = NativeRuntimePackage.Create(input, pins, new(512, 16 * 1024 * 1024, 32 * 1024 * 1024));
        Assert.Null(result.Package); Assert.Equal(NativeRuntimeIssue.InvalidInventory, result.Issue);
    }
    [Theory]
    [InlineData("swiftlatexpdftex.fmt")]
    [InlineData("pdflatex.fmt")]
    public void Project_format_conflict_rejects_before_any_phase_dispatch(string name) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); Assert.True(f.Workspace.CreateFileFromBytes(name, Encoding.ASCII.GetBytes("ordinary conflicting bytes")));
        var t = new Transport(); var factory = Factory(t); var id = new CompileJobId(Guid.NewGuid());
        var result = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.Equal(CompileOutputStatus.Failed, result!.Status); Assert.Null(result.Payload); Assert.Empty(t.Calls); await factory.Operations.WhenIdle;
    });
    [Fact]
    public void Unexpected_mapped_format_output_cannot_enter_generated_carry() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var t = new Transport();
        t.Handler = r => Task.FromResult(Transport.Reply(r) with { Files = [.. Transport.Reply(r).Files, new("swiftlatexpdftex.fmt", [1, 2, 3])] });
        var factory = Factory(t); var id = new CompileJobId(Guid.NewGuid()); var result = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.Equal(CompileOutputStatus.Failed, result!.Status); Assert.Null(result.Payload); Assert.Single(t.Calls); await factory.Operations.WhenIdle;
    });

    [Fact]
    public void Declared_phases_carry_only_generated_aux_bbl_and_real_context() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var t = new Transport(); var factory = Factory(t); var c = await Context(f); var id = new CompileJobId(Guid.NewGuid());
        var output = await Execute(factory.Create(id, 1), id, c);
        Assert.NotNull(output); Assert.Equal(CompileOutputStatus.Succeeded, output.Status);
        Assert.Equal(new[] { "latex", "bibtex", "latex", "latex" }, t.Calls.Select(x => x.Tag.Tool));
        Assert.Contains(t.Calls[1].Files, f => f.Name == "main.aux" && f.Bytes.SequenceEqual(new byte[] { 1, 2, 3 }));
        Assert.Contains(t.Calls[2].Files, f => f.Name == "main.bbl" && Encoding.UTF8.GetString(f.Bytes) == "contract-generated-bbl");
        Assert.DoesNotContain(t.Calls[1].Files, f => f.Name == "main.pdf");
        Assert.All(t.Calls, r => { Assert.Equal(c.InputContentDigest.Hex, r.Tag.Digest); Assert.Equal(c.SnapshotIdentity.CaptureId, r.Tag.Capture); });
        var accepted = Assert.IsType<AcceptedCompileOutput>(CompileOutputEnvelope.Create(output, Budget).Envelope?.TryAccept(c).Output);
        Assert.Same(c, accepted.RequestContext); var copy = accepted.ToPayloadArray()!; copy[0] = 0; Assert.Equal((byte)'%', accepted.PayloadBytes[0]);
        await factory.Operations.WhenIdle;
    });
    [Fact]
    public void Legacy_required_plan_still_rejects_missing_bibtex_before_transport() => LifecycleTestHost.Run(async () =>
    {
        Assert.False(Plan.UseSelectionTools);
        await using var f = await Fixture(); SelectTools(f);
        var t = new Transport(); var factory = Factory(t); var id = new CompileJobId(Guid.NewGuid());
        var result = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.Equal(CompileOutputStatus.Unsupported, result!.Status); Assert.Null(result.Payload);
        Assert.Equal("unsupported-selection", Assert.Single(result.Diagnostics!).Code); Assert.Empty(t.Calls);
        await factory.Operations.WhenIdle;
    });
    [Fact]
    public void Legacy_required_plan_preserves_existing_extra_tool_validation() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); SelectTools(f, Runtime.Value.Bibtex, Runtime.Value.Bibtex with { Id = "foreign-tool" });
        var t = new Transport(); var factory = Factory(t); var id = new CompileJobId(Guid.NewGuid());
        var result = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.Equal(CompileOutputStatus.Succeeded, result!.Status);
        Assert.Equal(new[] { "latex", "bibtex", "latex", "latex" }, t.Calls.Select(x => x.Tag.Tool));
        await factory.Operations.WhenIdle;
    });
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Legacy_optional_plan_remains_latex_only_regardless_of_declared_tools(int tools) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture();
        SelectTools(f, tools switch { 0 => [], 1 => [Runtime.Value.Bibtex], _ => [Runtime.Value.Bibtex with { Id = "foreign-tool" }] });
        var t = new Transport(); var factory = Factory(t, plan: Plan with { BibtexRequired = false, MaxLatexPasses = 2 });
        var id = new CompileJobId(Guid.NewGuid()); var result = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.Equal(CompileOutputStatus.Succeeded, result!.Status);
        Assert.Equal(new[] { "latex", "latex" }, t.Calls.Select(x => x.Tag.Tool));
        await factory.Operations.WhenIdle;
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Selection_tools_empty_runs_plain_latex_only_and_converges_in_two_passes(bool legacyRequired) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); SelectTools(f);
        Assert.True(f.Workspace.EditText(f.Main, "\\documentclass{article}\n\\begin{document}Plain article.\\end{document}\n"));
        var t = new Transport(); var factory = Factory(t, plan: Plan with { BibtexRequired = legacyRequired, UseSelectionTools = true, MaxLatexPasses = 2 });
        var id = new CompileJobId(Guid.NewGuid()); var c = await Context(f);
        Assert.Empty(c.Snapshot.Selection.Tools);
        var result = await Execute(factory.Create(id, 1), id, c);
        Assert.Equal(CompileOutputStatus.Succeeded, result!.Status); Assert.NotNull(result.Payload);
        Assert.Equal(new[] { "latex", "latex" }, t.Calls.Select(x => x.Tag.Tool));
        Assert.All(t.Calls, r => Assert.DoesNotContain(r.Files, x => x.Name == "main.bbl"));
        var accepted = Assert.IsType<AcceptedCompileOutput>(CompileOutputEnvelope.Create(result, Budget).Envelope?.TryAccept(c).Output);
        Assert.Same(c, accepted.RequestContext); Assert.Equal("%PDF-contract-only", Encoding.ASCII.GetString(accepted.ToPayloadArray()!));
        await factory.Operations.WhenIdle;
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Selection_tools_exact_bibtex_runs_existing_graph_regardless_of_legacy_flag(bool legacyRequired) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var t = new Transport();
        var factory = Factory(t, plan: Plan with { BibtexRequired = legacyRequired, UseSelectionTools = true, MaxLatexPasses = 3 });
        var id = new CompileJobId(Guid.NewGuid()); var c = await Context(f);
        Assert.Equal(Runtime.Value.Bibtex, Assert.Single(c.Snapshot.Selection.Tools));
        var result = await Execute(factory.Create(id, 1), id, c);
        Assert.Equal(CompileOutputStatus.Succeeded, result!.Status);
        Assert.Equal(new[] { "latex", "bibtex", "latex", "latex" }, t.Calls.Select(x => x.Tag.Tool));
        Assert.Contains(t.Calls[1].Files, x => x.Name == "main.aux");
        Assert.Contains(t.Calls[2].Files, x => x.Name == "main.bbl");
        Assert.IsType<AcceptedCompileOutput>(CompileOutputEnvelope.Create(result, Budget).Envelope?.TryAccept(c).Output);
        await factory.Operations.WhenIdle;
    });
    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("known-plus-foreign")]
    public void Selection_tools_rejects_any_nonexact_descriptor_before_transport(string variation) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture();
        var foreign = variation switch
        {
            "version" => Runtime.Value.Bibtex with { Version = "foreign-version" },
            "hash" => Runtime.Value.Bibtex with { Hash = new Client.Workspace.Domain.SHA256(new string('f', 64)) },
            _ => Runtime.Value.Bibtex with { Id = "foreign-tool" }
        };
        SelectTools(f, variation == "known-plus-foreign" ? [Runtime.Value.Bibtex, foreign] : [foreign]);
        var t = new Transport(); var factory = Factory(t, plan: Plan with { UseSelectionTools = true });
        var id = new CompileJobId(Guid.NewGuid()); var result = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.Equal(CompileOutputStatus.Unsupported, result!.Status); Assert.Null(result.Payload);
        Assert.Equal("unsupported-selection", Assert.Single(result.Diagnostics!).Code); Assert.Empty(t.Calls);
        await factory.Operations.WhenIdle;
    });
    [Theory]
    [InlineData("latex")]
    [InlineData("bibtex")]
    public void Selection_tools_numeric_failure_never_promotes_first_pass_pdf(string tool) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var t = new Transport();
        t.Handler = r => Task.FromResult(Transport.Reply(r) with { NativeReturn = r.Tag.Tool == tool ? 2 : 0 });
        var factory = Factory(t, plan: Plan with { BibtexRequired = false, UseSelectionTools = true });
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, factory, new(2, 100), TimeProvider.System);
        var result = await service.Request(LifecycleTestHost.Options).Completion;
        Assert.Equal(CompileLifecycleReason.CompilerFailed, result.Reason); Assert.Null(result.Output);
        await service.WhenIdle; Assert.Null(service.State.LastSuccessful);
        Assert.Equal(tool == "latex" ? new[] { "latex" } : new[] { "latex", "bibtex" }, t.Calls.Select(x => x.Tag.Tool));
        await factory.Operations.WhenIdle;
    });
    [Fact]
    public void Finite_pass_limit_is_failure_without_partial_pdf() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var t = new Transport(); var factory = Factory(t, plan: Plan with { MaxLatexPasses = 1 }); var id = new CompileJobId(Guid.NewGuid());
        var result = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.NotNull(result); Assert.Equal(CompileOutputStatus.Failed, result.Status); Assert.Null(result.Payload); Assert.Equal("pass-limit-unknown", Assert.Single(result.Diagnostics!).Code);
        await factory.Operations.WhenIdle;
    });
    [Theory]
    [InlineData("bibtex")]
    [InlineData("latex")]
    public void Numeric_failure_is_preserved_and_never_promotes_pdf(string tool) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var t = new Transport(); t.Handler = r => Task.FromResult(Transport.Reply(r) with { NativeReturn = r.Tag.Tool == tool ? 2 : 0 });
        var factory = Factory(t); var seen = new List<NativePhaseObservation>(); factory.PhaseObserved += seen.Add; var id = new CompileJobId(Guid.NewGuid());
        var result = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.NotNull(result); Assert.Equal(CompileOutputStatus.Failed, result.Status); Assert.Null(result.Payload); Assert.Contains(seen, x => x.Reply.NativeReturn == 2);
        await factory.Operations.WhenIdle;
    });
    [Fact]
    public void Wasm_abort_keeps_null_native_return() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var t = new Transport(); t.Handler = r => Task.FromResult(Transport.Reply(r) with { Outcome = "wasm-abort", NativeReturn = null, Files = [] });
        var factory = Factory(t); NativePhaseObservation? seen = null; factory.PhaseObserved += x => seen = x; var id = new CompileJobId(Guid.NewGuid());
        var result = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.Equal(CompileOutputStatus.Failed, result!.Status); Assert.Null(seen!.Reply.NativeReturn); await factory.Operations.WhenIdle;
    });
    [Fact]
    public void Forged_phase_correlation_is_not_accepted() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var t = new Transport(); t.Handler = r => Task.FromResult(Transport.Reply(r) with { Tag = r.Tag with { Capture = Guid.NewGuid() } });
        var factory = Factory(t); var id = new CompileJobId(Guid.NewGuid()); var result = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.Equal(CompileOutputStatus.Failed, result!.Status); Assert.Equal("invalid-native-reply", Assert.Single(result.Diagnostics!).Code); await factory.Operations.WhenIdle;
    });
    [Fact]
    public void Transport_timeout_has_no_invented_numeric_return_or_pdf() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var t = new Transport();
        t.Handler = r => Task.FromResult(Transport.Reply(r) with { Outcome = "transport-timeout", NativeReturn = null, Entered = false, Files = [], Inputs = [] });
        var factory = Factory(t); var id = new CompileJobId(Guid.NewGuid()); var result = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.Equal(CompileOutputStatus.TimedOut, result!.Status); Assert.Null(result.Payload); await factory.Operations.WhenIdle;
    });

    [Fact]
    public void Foreign_OCE_is_not_caller_cancellation() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var t = new Transport { Handler = _ => Task.FromException<NativePhaseReply>(new OperationCanceledException("foreign")) };
        var factory = Factory(t); var id = new CompileJobId(Guid.NewGuid()); var result = await Execute(factory.Create(id, 1), id, await Context(f));
        Assert.Equal(CompileOutputStatus.Failed, result!.Status); Assert.Equal("foreign-cancellation", Assert.Single(result.Diagnostics!).Code); await factory.Operations.WhenIdle;
    });
    [Fact]
    public void Cancelled_provider_keeps_actual_lease_until_foreign_settlement() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var held = new TaskCompletionSource<NativeRuntimePackage?>(TaskCreationOptions.RunContinuationsAsynchronously); int calls = 0;
        var gate = new NativeForeignOperations(1); var t = new Transport(); var factory = Factory(t, new Provider(() => { calls++; return held.Task; }), operations: gate);
        using var cancel = new CancellationTokenSource(); var c = await Context(f); var id = new CompileJobId(Guid.NewGuid());
        try
        {
            var task = Execute(factory.Create(id, 1), id, c, cancel.Token); Assert.Equal(1, gate.Active); cancel.Cancel();
            Assert.Equal(CompileOutputStatus.Cancelled, (await task)!.Status); Assert.Equal(1, gate.Active);
            var next = new CompileJobId(Guid.NewGuid()); var result = await Execute(factory.Create(next, 1), next, c);
            Assert.Equal("foreign-resource-limit", Assert.Single(result!.Diagnostics!).Code); Assert.Equal(1, calls); Assert.Empty(t.Calls);
        }
        finally { held.TrySetResult(Runtime.Value); await gate.WhenIdle; }
    });
    [Fact]
    public void Cancelled_interop_retains_lease_and_termination_is_job_specific() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await Fixture(); var held = new TaskCompletionSource<NativePhaseReply>(TaskCreationOptions.RunContinuationsAsynchronously); var t = new Transport { Handler = _ => held.Task };
        var gate = new NativeForeignOperations(1); var factory = Factory(t, operations: gate); var id = new CompileJobId(Guid.NewGuid()); var c = await Context(f); var worker = factory.Create(id, 1);
        try
        {
            var task = Execute(worker, id, c); Assert.Single(t.Calls); worker.RequestTermination(new(Guid.NewGuid()), 1, CompileLifecycleReason.Cancelled); Assert.Empty(t.Stops);
            worker.RequestTermination(id, 1, CompileLifecycleReason.Cancelled); Assert.Equal(CompileOutputStatus.Cancelled, (await task)!.Status); Assert.Equal(1, gate.Active);
            Assert.Equal((id.Value.ToString("N"), 1L), Assert.Single(t.Stops));
        }
        finally { held.TrySetResult(Transport.Reply(Assert.Single(t.Calls))); await gate.WhenIdle; }
    });
}
