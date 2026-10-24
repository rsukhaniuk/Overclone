using Client.Compilation;
using Client.Compilation.History;
using Client.Compilation.Native;
using Client.RuntimeAssets;
using Workspace.Tests.CompilationMapPayload;
using Workspace.Tests.CompilationNative;
using Xunit;

namespace Workspace.Tests.SyncTeXIntegration;

public sealed class HistoryAdapterTests
{
    [Fact]
    public async Task Explicit_new_adapter_preserves_old_history_and_unknown_adapters_remain_refused()
    {
        await using var f = await CompilationTestWorkspace.Open(); var snapshot = await f.Snapshot();
        var added = new CompileOutputDescriptor("native-pdftex", "n11-synctex-v1", new(NativeRuntimeProfiles.SyncTeXCompilerWasmHash));
        var allowed = new[] { added };
        var io = new SetupBackend();
        await using var store = new CompileArtifactHistoryStore(io, f.Repo, NativeRuntimePackage.Pdf, NativeRuntimePackage.Adapter, allowed);
        allowed[0] = new("untrusted", "1", null); // Composition inputs are owned.
        var budget = new ArtifactHistoryBudget(new(32, 1048576, 8388608, 65536), 1048576, 65536,
            new(new(2, 256, 1048576, 2097152, 4096, 256, 256), 16384, 16, 32, 30000));
        foreach (var adapter in new[] { NativeRuntimePackage.Adapter, added, allowed[0] })
        {
            var context = CompileRequestContext.Create(snapshot, 1, 4096, NativeRuntimePackage.Pdf, adapter).Context!;
            var output = CompileOutputEnvelope.Create(MapPayloadTests.Input(context, [1, 2, 3]),
                new(1024, 65536, 8, 64, 1024, 4096)).Envelope!.TryAccept(context).Output!;
            int before = io.Calls;
            var saved = await store.PersistAsync(output, null, budget);
            if (adapter == allowed[0])
            { Assert.Equal(RuntimeAssetStatus.InvalidInput, saved.Status); Assert.Equal(before, io.Calls); continue; }
            Assert.Equal(RuntimeAssetStatus.Installed, saved.Status);
            var read = await store.ReadAsync(saved.Key!, f.Repo.Project.Id, null, budget);
            Assert.Equal(RuntimeAssetStatus.Ready, read.Status);
            Assert.Equal(output.ContentIdentity, read.Artifact!.ContentIdentity);
            Assert.Equal(output.ToPayloadArray(), read.Artifact.CopyPdf());
            Assert.Equal(2, io.Files[saved.Key!.RecordId].Count); // Existing metadata+PDF codec; map remains transient.
        }
        Assert.Equal(0, f.Repo.Commits);
    }
}
