using System.Collections;
using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Workspace.Tests.CompilationLifecycle;
using Xunit;

namespace Workspace.Tests.CompilationNative;

// Real provisioned bytes and capture/C# workflow; the phase transport is MODELED.
// These tests do not claim native execution, valid generated PDF, or app acceptance.
public sealed class NativeLatexDviTests
{
    private const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalLatex2023Dvi;
    private static readonly CompileOutputBudget Budget = new(8388608, 65536, 32, 128, 4096, 8192);
    internal static string Root => Environment.GetEnvironmentVariable("DVI_RUNTIME_DIR")!;
    private static readonly Lazy<(NativeResourcePin[] Pins, NativeResourceInput[] Inputs, NativeRuntimePackage Package)> Fixture = new(() =>
    {
        var pins = LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(Root, "pins.json"))).ToArray();
        var inputs = pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(Root, "files", p.Name)))).ToArray();
        return (pins, inputs, Assert.IsType<NativeRuntimePackage>(Create(inputs, pins).Package));
    });
    internal static NativeRuntimePackage Package => Fixture.Value.Package;
    private static NativeRuntimeCreation Create(IReadOnlyList<NativeResourceInput> inputs, IReadOnlyList<NativeResourcePin> pins) =>
        NativeRuntimePackage.Create(inputs, pins, LocalRuntimeSelection.LimitsFor(Profile), Profile);

    [Fact]
    public void Trusted_tuple_is_closed_and_older_protocols_do_not_change()
    {
        Assert.Equal(new[] { 1, 2, 3, 6, 6 }, Enumerable.Range(0, 5).Select(i => NativeRuntimeProfiles.ProtocolVersion((NativeRuntimeProfile)i)));
        Assert.Equal(new[] { 378, 481, 497, 499, 507 }, Enumerable.Range(0, 5).Select(i => NativeRuntimeProfiles.InstalledCount((NativeRuntimeProfile)i)));
        var plan = NativeLatexDviContract.CreatePlan(); Assert.True(plan.IsValid); Assert.Equal(7, plan.EffectivePhaseVersion); Assert.True(plan.DviOutput);
        Assert.False((plan with { DviOutput = false }).IsValid);
        Assert.False((plan with { PhaseProtocolVersion = 6 }).IsValid);
        Assert.False((plan with { Profile = NativeRuntimeProfile.LocalLatex2023SyncTeX }).IsValid);
        Assert.True(NativeRuntimeProfiles.Known(NativeRuntimeProfile.LocalCompilerSuite));
        Assert.True(NativeRuntimeProfiles.Known(NativeRuntimeProfile.LocalCompilerCompatibility));
        Assert.False(NativeRuntimeProfiles.Known((NativeRuntimeProfile)int.MaxValue));
        Assert.False((plan with { Profile = (NativeRuntimeProfile)5 }).IsValid);
        Assert.False((plan with { Profile = NativeRuntimeProfile.LocalCompilerCompatibility }).IsValid);
        Assert.True((NativeSyncTeXContract.CreatePlan() with { Profile = Profile }).IsValid);
        Assert.False(NativeLatexDviContract.MatchesOptions(NativeSyncTeXContract.SelectionOptions));
        var entry = NativeSelectedEntryContract.Create("тези/my thesis.tex", ["тези/my thesis.tex"], out _, true)!;
        Assert.Contains("my thesis.dvi", entry.Outputs); Assert.False(entry.IsState("my thesis.dvi"));
    }

    [DviFixtureFact]
    public void Complete507_closure_owned_bytes_and_distinct_identity_are_verified()
    {
        var (pins, inputs, package) = Fixture.Value;
        Assert.Equal(507, pins.Length); Assert.Equal(60298852L, pins.Sum(p => (long)p.Length));
        Assert.Equal(Profile, LocalRuntimeSelection.ProfileFor(pins));
        Assert.Equal("69cb517ae4a5cf9e92983c1347e00aaed9f08812fc65e8d91cac71a68117f90c", package.Identity.Hex);
        Assert.Equal("pdftex", package.Engine.Id); Assert.Equal("1.40.21", package.Engine.Version);
        Assert.Equal("latex-dvi", package.DviEngine.Id); Assert.Equal(NativeSyncTeXContract.OutputAdapter, package.OutputAdapter);
        Assert.Equal(NativeLatexDviContract.OutputAdapter, package.DviOutputAdapter);
        var owned = package.Copy("latex-dvi.fmt"); owned[0] ^= 1;
        Assert.Equal(NativeRuntimeProfiles.DviFormatHash, NativeRuntimePackage.Digest(package.Copy("latex-dvi.fmt")).Hex);
        var changed = inputs.ToArray(); changed[0] = changed[0] with { Bytes = changed[0].Bytes.ToArray() }; changed[0].Bytes[0] ^= 1;
        Assert.Null(Create(changed, pins).Package);
        Assert.Null(Create(inputs.Skip(1).ToArray(), pins.Skip(1).ToArray()).Package);
        Assert.Null(Create([.. inputs, inputs[0]], [.. pins, pins[0]]).Package);
        var duplicate = pins.ToArray(); duplicate[1] = duplicate[0]; Assert.Null(Create(inputs, duplicate).Package);
        var oldFmt = inputs.ToArray(); int i = Array.FindIndex(oldFmt, p => p.Name == "latex-dvi.fmt");
        oldFmt[i] = new("latex-dvi.fmt", package.Copy("pdflatex.fmt")); Assert.Null(Create(oldFmt, pins).Package);
        var wrongPins = pins.ToArray(); wrongPins[0] = wrongPins[0] with { Sha256 = new('0', 64) };
        // An invalid complete closure must fail BEFORE indexing any supplied resource.
        Assert.Null(Create(new NoResourceAccess(507), wrongPins).Package);
        Assert.Throws<FormatException>(() => LocalRuntimeSelection.ProfileFor(wrongPins));
    }
    private sealed class NoResourceAccess(int count) : IReadOnlyList<NativeResourceInput>
    {
        public int Count => count;
        public NativeResourceInput this[int index] => throw new Exception("Resource inspected before closure approval");
        public IEnumerator<NativeResourceInput> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static async Task<CompileRequestContext> Context(CompilationTestWorkspace f, bool bib = false, bool dvi = true)
    {
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, dvi ? Package.DviEngine : Package.Engine, [Package.Bundle],
            bib ? [Package.Bibtex] : [], dvi ? NativeLatexDviContract.SelectionOptions : NativeSyncTeXContract.SelectionOptions), 65536).Selection!);
        return CompileRequestContext.Create(await f.Snapshot(new(64, 8388608, 16777216, 131072)), 1, 65536,
            NativeRuntimePackage.Pdf, dvi ? Package.DviOutputAdapter : Package.OutputAdapter).Context!;
    }
    private static NativeCompilationWorkerFactory Factory(Transport transport, NativeCompilePlan? plan = null) =>
        new(new FixedNativeRuntimeProvider(Package), transport, plan ?? NativeLatexDviContract.CreatePlan());
    private static async Task<CompileOutputEnvelopeInput> Execute(CompileRequestContext context, Transport transport, NativeCompilePlan? plan = null)
    {
        var factory = Factory(transport, plan); var id = new CompileJobId(Guid.NewGuid());
        var operation = Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(factory.Create(id, 1).ExecuteAsync(id, context, Budget, default));
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(20));
        await factory.Operations.WhenIdle; return Assert.IsType<CompileOutputEnvelopeInput>(result);
    }
    private sealed class Transport : INativePhaseTransport
    {
        public List<NativePhaseRequest> Calls = [];
        public Func<NativePhaseRequest, NativePhaseReply, NativePhaseReply>? Mutate;
        public bool Churn;
        public TaskCompletionSource<NativePhaseReply>? Conversion;
        public TaskCompletionSource ConversionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops;
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest request)
        {
            Calls.Add(request);
            var reply = Reply(request);
            if (request.Tag.Tool == "dvipdfmx" && Conversion is not null) { ConversionEntered.TrySetResult(); return Conversion.Task; }
            return Task.FromResult(Mutate?.Invoke(request, reply) ?? reply);
        }
        public NativePhaseReply Reply(NativePhaseRequest r)
        {
            var job = r.Outputs.Single(n => n.EndsWith(".pdf", StringComparison.Ordinal))[..^4];
            List<NativeFileTransfer> files = [];
            if (r.Tag.Tool == "dvipdfmx") files.Add(new(job + ".pdf", "%PDF-modeled-converter"u8.ToArray()));
            else
            {
                files.AddRange(r.Files.Where(f => f.Name == job + ".bbl"));
                if (r.Tag.Tool == "bibtex") { files.Clear(); files.Add(new(job + ".bbl", [2])); }
                else
                {
                    files.Add(new(job + ".aux", [1]));
                    files.Add(new(job + ".toc", [(byte)(Churn ? Calls.Count : 1)]));
                    files.Add(r.Tag.Version == 7 ? new(job + ".dvi", [247, 2, (byte)Calls.Count]) : new(job + ".pdf", "%PDF-modeled-compiler"u8.ToArray()));
                }
            }
            bool converter = r.Tag.Tool == "dvipdfmx";
            return new(r.Tag, "native-return", 0, true, converter ? "modeled conversion log" : "modeled compiler log", files.ToArray(),
                r.Files.Select(f => new NativeInputFact(f.Name, f.Bytes.Length, NativeRuntimePackage.Digest(f.Bytes).Hex)).ToArray(),
                WasmSha256: converter ? NativeRuntimeProfiles.DviConverterWasmHash : NativeRuntimeProfiles.SyncTeXCompilerWasmHash,
                LoaderSha256: converter ? NativeRuntimeProfiles.DviConverterLoaderHash : NativeRuntimeProfiles.SyncTeXCompilerLoaderHash);
        }
        public void RequestTermination(string job, long epoch) { Stops++; }
    }

    [DviFixtureTheory]
    [InlineData(false)] [InlineData(true)]
    public void Finite_selected_root_BibTeX_convergence_converts_exact_final_DVI_once(bool bib) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Assert.True(f.Workspace.RenameFile(f.Main, "тези/my thesis.tex"));
        Assert.True(f.Workspace.CreateFile("image.pdf", "captured asset bytes"));
        var context = await Context(f, bib); var t = new Transport(); var result = await Execute(context, t);
        Assert.Equal(CompileOutputStatus.Succeeded, result.Status); Assert.Null(result.SourceMapPayload);
        Assert.Equal(bib ? new[] { "latex", "bibtex", "latex", "latex", "dvipdfmx" } : new[] { "latex", "latex", "dvipdfmx" }, t.Calls.Select(r => r.Tag.Tool));
        var conversion = t.Calls[^1]; Assert.Equal("my thesis.dvi", conversion.MainEntry); Assert.Equal("тези/my thesis.tex", conversion.SelectedRoot);
        Assert.Equal(new[] { "my thesis.pdf" }, conversion.Outputs);
        Assert.All(t.Calls, r => { Assert.Equal(7, r.Tag.Version); Assert.Equal(Profile, r.Tag.Profile); Assert.Equal("latex-dvi", r.Tag.EngineId); Assert.Equal(Package.Identity.Hex, r.Tag.RuntimeIdentity); });
        foreach (var r in t.Calls.Where(r => r.Tag.Tool != "dvipdfmx"))
        {
            Assert.Null(r.RuntimeResourceNames);
            Assert.Equal(NativeRuntimeProfiles.DviFormatHash, NativeRuntimePackage.Digest(r.Files.Single(f => f.Name == NativeRuntimePackage.NativeFormatName).Bytes).Hex);
            Assert.DoesNotContain(r.Files, f => f.Name is "latex-dvi.fmt" or "pdflatex.fmt" or "dvi-converter.js" or "dvi-converter.wasm" or "synctex-query.js" or "synctex-query.wasm" or "my thesis.dvi");
        }
        foreach (var original in context.Snapshot.Files) Assert.Equal(original.ToArray(), conversion.Files.Single(f => f.Name == original.RelativePath).Bytes);
        Assert.Equal((byte)(t.Calls.Count - 1), conversion.Files.Single(f => f.Name == "my thesis.dvi").Bytes[2]);
        Assert.DoesNotContain(conversion.Files, f => f.Name is "my thesis.pdf" or "my thesis.aux" or NativeRuntimePackage.NativeFormatName);
        Assert.NotNull(conversion.RuntimeResourceNames); Assert.Contains("glyphlist.txt", conversion.RuntimeResourceNames!);
        Assert.Contains("pdfglyphlist.txt", conversion.RuntimeResourceNames!); Assert.Contains("kanjix.map", conversion.RuntimeResourceNames!);
        Assert.DoesNotContain("image.pdf", conversion.RuntimeResourceNames!); Assert.DoesNotContain("my thesis.dvi", conversion.RuntimeResourceNames!);
        Assert.All(conversion.RuntimeResourceNames!, name => Assert.Contains(conversion.Files, f => f.Name == name));
        Assert.Equal(NativeRuntimeProfiles.DviConverterLoaderHash, NativeRuntimePackage.Digest(conversion.Loader).Hex);
        Assert.Equal(NativeRuntimeProfiles.DviConverterWasmHash, NativeRuntimePackage.Digest(conversion.Wasm).Hex);
        Assert.Contains("modeled conversion log", result.LogText!);
        var envelope = CompileOutputEnvelope.Create(result, Budget).Envelope!; Assert.NotNull(envelope.TryAccept(context).Output);
        Assert.NotNull(envelope); // final acceptance still belongs to the original B3 capture
        Assert.True(f.Workspace.EditText(f.Main, "changed after capture")); var newer = await Context(f, bib); Assert.Null(envelope.TryAccept(newer).Output);
    });

    [DviFixtureFact]
    public void Profile4_default_projection_retains_pdfLaTeX_format_and_v6() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex");
        var t = new Transport(); var result = await Execute(await Context(f, dvi: false), t, NativeSyncTeXContract.CreatePlan() with { Profile = Profile });
        Assert.Equal(CompileOutputStatus.Succeeded, result.Status); Assert.Equal(2, t.Calls.Count);
        Assert.All(t.Calls, r =>
        {
            Assert.Equal(6, r.Tag.Version); Assert.Null(r.Tag.EngineId); Assert.Null(r.RuntimeResourceNames);
            Assert.Equal(NativeRuntimeProfiles.SyncTeXFormatHash, NativeRuntimePackage.Digest(r.Files.Single(f => f.Name == NativeRuntimePackage.NativeFormatName).Bytes).Hex);
            Assert.DoesNotContain(r.Files, f => f.Name is "latex-dvi.fmt" or "dvi-converter.js" or "dvi-converter.wasm" or "synctex-query.js" or "synctex-query.wasm");
        });
    });

    [DviFixtureTheory]
    [InlineData("missing-dvi")] [InlineData("churn")] [InlineData("failed-latex")]
    public void No_stable_successful_fresh_DVI_means_no_conversion(string kind) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex");
        var t = new Transport { Churn = kind == "churn", Mutate = (_, r) => kind switch
        {
            "missing-dvi" => r with { Files = r.Files.Where(f => !f.Name.EndsWith(".dvi", StringComparison.Ordinal)).Append(new("main.pdf", "%PDF-old-compiler"u8.ToArray())).ToArray() },
            "failed-latex" => r with { NativeReturn = 1 }, _ => r
        } };
        var result = await Execute(await Context(f), t); Assert.Equal(CompileOutputStatus.Failed, result.Status); Assert.Null(result.Payload);
        Assert.DoesNotContain(t.Calls, r => r.Tag.Tool == "dvipdfmx"); Assert.InRange(t.Calls.Count, 1, 6);
    });

    [DviFixtureTheory]
    [InlineData("tag")] [InlineData("engine")] [InlineData("input")] [InlineData("loader")] [InlineData("wasm")]
    [InlineData("map")] [InlineData("status")] [InlineData("missing")] [InlineData("invalid")]
    [InlineData("undeclared")] [InlineData("duplicate")] [InlineData("oversize")]
    public void Converter_reply_cannot_bypass_identity_status_or_PDF_bounds(string kind) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex");
        var t = new Transport { Mutate = (q, r) => q.Tag.Tool != "dvipdfmx" ? r : kind switch
        {
            "tag" => r with { Tag = r.Tag with { Nonce = "reused" } }, "engine" => r with { Tag = r.Tag with { EngineId = "pdftex" } },
            "input" => r with { Inputs = [] }, "loader" => r with { LoaderSha256 = NativeRuntimeProfiles.SyncTeXCompilerLoaderHash },
            "wasm" => r with { WasmSha256 = NativeRuntimeProfiles.SyncTeXCompilerWasmHash }, "map" => r with { SourceMapPayload = [1] },
            "status" => r with { NativeReturn = 1 }, "missing" => r with { Files = [] }, "invalid" => r with { Files = [new("main.pdf", [1, 2, 3])] },
            "undeclared" => r with { Files = [new("other.pdf", r.Files[0].Bytes)] }, "duplicate" => r with { Files = [r.Files[0], r.Files[0]] },
            _ => r with { Files = [new("main.pdf", new byte[8388609])] }
        } };
        var result = await Execute(await Context(f), t); Assert.Equal(CompileOutputStatus.Failed, result.Status); Assert.Null(result.Payload);
        Assert.Equal(1, t.Calls.Count(r => r.Tag.Tool == "dvipdfmx"));
    });

    [DviFixtureFact]
    public void Cancellation_during_conversion_retires_actual_call_and_never_accepts_late_PDF() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex");
        var context = await Context(f); var t = new Transport { Conversion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var factory = Factory(t); var id = new CompileJobId(Guid.NewGuid()); var worker = factory.Create(id, 1);
        using var cancel = new CancellationTokenSource();
        var task = Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(worker.ExecuteAsync(id, context, Budget, cancel.Token));
        await t.ConversionEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            worker.RequestTermination(id, 1, CompileLifecycleReason.Cancelled); cancel.Cancel();
            var result = await task; Assert.Equal(CompileOutputStatus.Cancelled, result!.Status); Assert.Null(result.Payload);
            Assert.Equal(1, t.Stops); Assert.False(factory.Operations.WhenIdle.IsCompleted);
        }
        finally { t.Conversion!.SetResult(t.Reply(t.Calls[^1])); await factory.Operations.WhenIdle; }
    });

    [DviFixtureFact]
    public void Original_B3_freshness_rejects_source_change_while_conversion_is_held() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex");
        await Context(f);
        var t = new Transport { Conversion = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var factory = Factory(t);
        await using var service = new CompilationLifecycleService(f.Workspace, f.Selection, factory, new(4, long.MaxValue), new LifecycleTestHost.Clock());
        var ticket = service.Request(new(new(64, 8388608, 16777216, 131072), Budget, 65536, TimeSpan.FromSeconds(30),
            NativeRuntimePackage.Pdf, Package.DviOutputAdapter));
        await t.ConversionEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            Assert.True(f.Workspace.EditText(f.Main, "changed during conversion"));
            t.Conversion!.SetResult(t.Reply(t.Calls[^1]));
            Assert.Equal(CompileLifecycleReason.Superseded, (await ticket.Completion).Reason);
            Assert.Null(service.State.LastSuccessful); Assert.False(service.State.IsCurrent);
        }
        finally { t.Conversion!.TrySetResult(t.Reply(t.Calls[^1])); await factory.Operations.WhenIdle; await service.WhenIdle; }
    });

    [DviFixtureTheory]
    [InlineData("main.dvi")] [InlineData("MAIN.DVI/asset.tex")] [InlineData("latex-dvi.fmt")]
    public void DVI_generated_and_format_namespace_collisions_refuse_before_dispatch(string path) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex");
        Assert.True(f.Workspace.CreateFile(path, "unchanged")); var t = new Transport();
        Assert.Equal(CompileOutputStatus.Failed, (await Execute(await Context(f), t)).Status); Assert.Empty(t.Calls);
    });

    [DviFixtureFact]
    public void Converter_project_total_includes_final_DVI_and_refuses_before_dispatch() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "main.tex");
        var t = new Transport { Mutate = (q, r) => q.Tag.Tool != "latex" ? r : r with
        { Files = r.Files.Where(f => f.Name != "main.dvi").Append(new("main.dvi", new byte[8388608])).ToArray() } };
        var result = await Execute(await Context(f), t); Assert.Equal("dvi-resource-limit", Assert.Single(result.Diagnostics!).Code);
        Assert.Null(result.Payload); Assert.Equal(2, t.Calls.Count); Assert.DoesNotContain(t.Calls, q => q.Tag.Tool == "dvipdfmx");
    });
}

public sealed class DviFixtureFactAttribute : FactAttribute
{
    public DviFixtureFactAttribute() { if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("DVI_RUNTIME_DIR") ?? "", "pins.json"))) Skip = "Explicit507 DVI fixture absent; no native substitute."; }
}
public sealed class DviFixtureTheoryAttribute : TheoryAttribute
{
    public DviFixtureTheoryAttribute() { if (!File.Exists(Path.Combine(Environment.GetEnvironmentVariable("DVI_RUNTIME_DIR") ?? "", "pins.json"))) Skip = "Explicit507 DVI fixture absent; no native substitute."; }
}
