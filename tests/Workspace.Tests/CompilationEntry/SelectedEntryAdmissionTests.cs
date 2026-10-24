using Client.Compilation;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Workspace.Tests.CompilationLifecycle;
using Xunit;

namespace Workspace.Tests.CompilationEntry;

public sealed class SelectedEntryAdmissionTests
{
    private sealed class Provider : INativeRuntimeProvider
    {
        public int Calls;
        public Task<NativeRuntimePackage?> GetAsync() { Calls++; throw new InvalidOperationException("Must reject before provider IO"); }
    }
    private sealed class Transport : INativePhaseTransport
    {
        public int Calls;
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest request) { Calls++; throw new InvalidOperationException("Must not dispatch"); }
        public void RequestTermination(string job, long epoch) { }
    }
    [Theory]
    [InlineData("missing")][InlineData("v4")][InlineData("unknown")][InlineData("extra")]
    public void Invalid_v5_options_reject_before_provider_or_transport_io(string kind) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.RenameFile(f.Main, "dir/paper.tex");
        CompilationOption[] options = kind switch { "missing" => [], "v4" => NativeAuthoringV4Contract.SelectionOptions,
            "unknown" => [new("unknown", "5")], _ => [NativeSelectedEntryContract.SelectionOption, new("extra", "5")] };
        f.Selection.SetSelection(CompilationTestWorkspace.Select(f.Main, options: options));
        var context = CompileRequestContext.Create(await f.Snapshot(), 1, 65536).Context!;
        var provider = new Provider(); var transport = new Transport();
        var factory = new NativeCompilationWorkerFactory(provider, transport, NativeSelectedEntryContract.CreatePlan());
        var id = new CompileJobId(Guid.NewGuid());
        var operation = Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(factory.Create(id, 1).ExecuteAsync(id, context,
            new(8388608, 65536, 32, 128, 4096, 8192), default));
        var result = await operation;
        Assert.NotNull(result); Assert.Equal(CompileOutputStatus.Unsupported, result!.Status);
        Assert.Equal("unsupported-selection", Assert.Single(result.Diagnostics!).Code);
        Assert.Equal(0, provider.Calls); Assert.Equal(0, transport.Calls); Assert.Equal(0, factory.Operations.Active);
    });
}
