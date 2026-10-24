using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Workspace.Tests.CompilationLifecycle;
using Workspace.Tests.CompilationNative;
using Xunit;

namespace Workspace.Tests.CompilationEntry;

// Actual capture, package and C# orchestration. Replies are MODELED, not native/PDF evidence.
public sealed class SelectedEntryWorkerTests
{
    private static readonly Lazy<NativeRuntimePackage> Runtime = new(() =>
    {
        var root = Environment.GetEnvironmentVariable("AUTHORING_RUNTIME_DIR")!;
        var pins = JsonSerializer.Deserialize<NativeResourcePin[]>(File.ReadAllBytes(Path.Combine(root, "pins.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(pins.Select(p => new NativeResourceInput(p.Name,
            File.ReadAllBytes(Path.Combine(root, "files", p.Name)))).ToArray(), pins,
            new(512, 33554432, 67108864, NativeRuntimeProfile.LocalLatex2023Authoring), NativeRuntimeProfile.LocalLatex2023Authoring).Package);
    });
    private static readonly CompileOutputBudget Budget = new(8388608, 65536, 32, 128, 4096, 8192);
    private static async Task<CompileRequestContext> Context(CompilationTestWorkspace f, bool bib = false, CompilationOption[]? options = null)
    {
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, Runtime.Value.Engine, [Runtime.Value.Bundle],
            bib ? [Runtime.Value.Bibtex] : [], options ?? NativeSelectedEntryContract.SelectionOptions), 65536).Selection!);
        return CompileRequestContext.Create(await f.Snapshot(), 1, 65536).Context!;
    }
    private sealed class Transport : INativePhaseTransport
    {
        public readonly List<NativePhaseRequest> Calls = [];
        public readonly List<(string, long)> Stops = [];
        public Func<NativePhaseRequest, NativePhaseReply, NativePhaseReply>? Mutate;
        public bool Churn, DeleteToc;
        public TaskCompletionSource<NativePhaseReply>? Held;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest r)
        {
            Calls.Add(r); Entered.TrySetResult();
            if (Held is not null) return Held.Task;
            var job = r.Outputs.Single(n => n.EndsWith(".pdf", StringComparison.Ordinal))[..^4];
            var state = r.Files.Where(f => r.Outputs.Contains(f.Name) && (f.Name.EndsWith(".aux", StringComparison.Ordinal)
                || new[] { ".bbl", ".toc", ".lof", ".lot", ".out" }.Any(ext => f.Name == job + ext)))
                .ToDictionary(f => f.Name, f => f.Bytes, StringComparer.Ordinal);
            if (r.Tag.Tool == "bibtex") state[job + ".bbl"] = [2];
            else
            {
                foreach (var name in r.Outputs.Where(n => n.EndsWith(".aux", StringComparison.Ordinal))) state[name] = [1];
                if (DeleteToc && Calls.Count > 1) state.Remove(job + ".toc");
                else state[job + ".toc"] = [(byte)(Churn ? Calls.Count : 1)];
            }
            var files = state.Select(f => new NativeFileTransfer(f.Key, f.Value)).ToList();
            if (r.Tag.Tool == "latex") files.Add(new(job + ".pdf", "%PDF-modeled-entry"u8.ToArray()));
            var reply = Reply(r, files.ToArray());
            return Task.FromResult(Mutate?.Invoke(r, reply) ?? reply);
        }
        public void RequestTermination(string job, long epoch) => Stops.Add((job, epoch));
        public static NativePhaseReply Reply(NativePhaseRequest r, NativeFileTransfer[] files) => new(r.Tag, "native-return", 0, true, "Modeled only", files,
            r.Files.Select(f => new NativeInputFact(f.Name, f.Bytes.Length, NativeRuntimePackage.Digest(f.Bytes).Hex)).ToArray(),
            WasmSha256: NativeRuntimePackage.WasmHash, LoaderSha256: NativeRuntimePackage.LoaderHash);
    }
    private static async Task<CompileOutputEnvelopeInput> Execute(CompileRequestContext context, Transport transport, NativeCompilePlan? plan = null)
    {
        var factory = new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(Runtime.Value), transport, plan ?? NativeSelectedEntryContract.CreatePlan());
        var id = new CompileJobId(Guid.NewGuid());
        var operation = Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(factory.Create(id, 1).ExecuteAsync(id, context, Budget, default));
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(10));
        await factory.Operations.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        return Assert.IsType<CompileOutputEnvelopeInput>(result);
    }

    [AuthoringV4FixtureTheory]
    [InlineData("renamed.tex", false)][InlineData("dir/paper.tex", false)][InlineData("dir/paper.tex", true)]
    [InlineData("тези/праця.tex", false)][InlineData("space dir/paper.tex", true)][InlineData("dir/Paper.TEX", true)]
    [InlineData("my thesis.tex", false)][InlineData("my thesis.tex", true)]
    [InlineData("dir/my thesis.tex", false)][InlineData("dir/my thesis.tex", true)]
    public void Selected_entry_tool_state_output_and_all_captured_bytes_survive_every_phase(string root, bool bib) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Assert.True(f.Workspace.RenameFile(f.Main, root));
        Assert.True(f.Workspace.CreateFile("parts/body.tex", "unchanged includes Україна🙂\r\n"));
        Assert.True(f.Workspace.CreateFile("refs.bib", "unchanged bibliography"));
        var context = await Context(f, bib); var entry = NativeSelectedEntryContract.Create(root, context.Snapshot.Files.Select(x => x.RelativePath), out _)!;
        var transport = new Transport(); var result = await Execute(context, transport);
        Assert.Equal(CompileOutputStatus.Succeeded, result.Status);
        Assert.Equal(bib ? new[] { "latex", "bibtex", "latex", "latex" } : new[] { "latex", "latex" }, transport.Calls.Select(r => r.Tag.Tool));
        Assert.All(transport.Calls, r =>
        {
            Assert.Equal(5, r.Tag.Version); Assert.Equal(2, (int)r.Tag.Profile); Assert.Equal(Runtime.Value.Identity.Hex, r.Tag.RuntimeIdentity);
            Assert.Equal(root, r.SelectedRoot); Assert.Equal(r.Tag.Tool == "latex" ? entry.LatexEntry : entry.BibtexEntry, r.MainEntry);
            Assert.Equal(entry.Outputs.ToArray(), r.Outputs);
            foreach (var original in context.Snapshot.Files) Assert.Equal(original.ToArray(), r.Files.Single(x => x.Name == original.RelativePath).Bytes);
            Assert.DoesNotContain(r.Files, x => x.Name == "main.tex");
        });
        if (bib)
        {
            Assert.Contains(transport.Calls[1].Files, x => x.Name == entry.Aux);
            Assert.Contains(transport.Calls[2].Files, x => x.Name == entry.Bbl);
            Assert.DoesNotContain(transport.Calls[2].Files, x => x.Name == root[..^4] + ".bbl" && x.Name != entry.Bbl);
        }
        Assert.NotNull(CompileOutputEnvelope.Create(result, Budget).Envelope!.TryAccept(context).Output);
        var legacy = await Execute(context, new Transport(), NativeAuthoringV4Contract.CreatePlan());
        Assert.Equal(CompileOutputStatus.Unsupported, legacy.Status);
    });

    [AuthoringV4FixtureTheory]
    [InlineData("paper.bbl")][InlineData("PAPER.PDF")][InlineData("paper.toc/child.tex")][InlineData("paper.TEX")]
    [InlineData("N6.MAP")][InlineData("n6.map/child.tex")]
    public void Generated_and_runtime_input_collisions_refuse_before_dispatch(string name) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "dir/paper.tex");
        Assert.True(f.Workspace.CreateFile(name, "untouched")); var t = new Transport();
        Assert.Equal(CompileOutputStatus.Failed, (await Execute(await Context(f), t)).Status); Assert.Empty(t.Calls);
    });

    [AuthoringV4FixtureTheory]
    [InlineData("dir/paper%.tex", "selected-entry-syntax")]
    [InlineData("dir/ paper.tex", "selected-entry-syntax")]
    [InlineData("dir/paper.txt", "selected-entry-extension")]
    public void Unsupported_selected_spelling_is_not_a_failed_namespace_admission(string root, string code) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Assert.True(f.Workspace.RenameFile(f.Main, root));
        var t = new Transport(); var result = await Execute(await Context(f), t);
        Assert.Equal(CompileOutputStatus.Unsupported, result.Status); Assert.Equal(code, Assert.Single(result.Diagnostics!).Code);
        Assert.Null(result.Payload); Assert.Empty(t.Calls);
    });

    [AuthoringV4FixtureTheory]
    [InlineData(false)][InlineData(true)]
    public void Complete_dynamic_state_change_or_deletion_delays_convergence(bool delete) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "dir/paper.tex");
        var t = new Transport { Churn = !delete, DeleteToc = delete };
        var result = await Execute(await Context(f), t);
        Assert.Equal(delete ? CompileOutputStatus.Succeeded : CompileOutputStatus.Failed, result.Status);
        Assert.Equal(delete ? 3 : 6, t.Calls.Count);
        if (delete) Assert.DoesNotContain(t.Calls[2].Files, x => x.Name == "paper.toc");
        else Assert.Equal("pass-limit-unknown", Assert.Single(result.Diagnostics!).Code);
    });

    [AuthoringV4FixtureTheory]
    [InlineData("version")][InlineData("entry-output")][InlineData("old-state")][InlineData("missing-bbl")]
    public void Wrong_reply_version_output_or_tool_state_cannot_promote(string kind) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "dir/paper.tex");
        var t = new Transport { Mutate = (request, reply) => kind switch
        {
            "version" => reply with { Tag = reply.Tag with { Version = 4 } },
            "entry-output" => reply with { Files = [new("dir/paper.pdf", "%PDF-model"u8.ToArray())] },
            "old-state" => reply with { Files = [new("main.aux", [1])] },
            _ => request.Tag.Tool == "bibtex" ? reply with { Files = [] } : reply
        } };
        var result = await Execute(await Context(f, bib: true), t);
        Assert.Equal(CompileOutputStatus.Failed, result.Status); Assert.Null(result.Payload);
    });

    [AuthoringV4FixtureFact]
    public void Cancellation_keeps_actual_transport_admission_and_blocks_late_next_pass() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "dir/paper.tex");
        var t = new Transport { Held = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var operations = new NativeForeignOperations(1);
        var factory = new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(Runtime.Value), t, NativeSelectedEntryContract.CreatePlan(), operations);
        var id = new CompileJobId(Guid.NewGuid()); var worker = factory.Create(id, 1); var context = await Context(f, bib: true);
        var run = Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(worker.ExecuteAsync(id, context, Budget, default));
        await t.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); worker.RequestTermination(id, 1, CompileLifecycleReason.Cancelled);
        var result = Assert.IsType<CompileOutputEnvelopeInput>(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(CompileOutputStatus.Cancelled, result.Status); Assert.Single(t.Stops); Assert.Single(t.Calls);
        Assert.Equal(1, operations.Active); Assert.False(operations.WhenIdle.IsCompleted);
        await Assert.ThrowsAsync<NativeResourceLimitException>(() => operations.RunAsync(() => Task.FromResult(1), default));
        t.Held.SetResult(Transport.Reply(t.Calls[0], [new("paper.aux", [1])]));
        await operations.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Single(t.Calls); Assert.Equal(0, operations.Active);
    });

    [AuthoringV4FixtureTheory]
    [InlineData("missing")][InlineData("old")][InlineData("extra")]
    public void V5_options_are_required_and_bound_into_real_digest(string kind) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "renamed.tex");
        var good = await Context(f); CompilationOption[] options = kind switch { "missing" => [], "old" => NativeAuthoringV4Contract.SelectionOptions,
            _ => [NativeSelectedEntryContract.SelectionOption, new("extra", "5")] };
        var wrong = await Context(f, options: options); Assert.NotEqual(good.InputContentDigest, wrong.InputContentDigest);
        var t = new Transport(); Assert.Equal(CompileOutputStatus.Unsupported, (await Execute(wrong, t)).Status); Assert.Empty(t.Calls);
    });
}
