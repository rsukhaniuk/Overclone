using Client.Compilation;
using Client.Compilation.History;
using Client.RuntimeAssets;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;

namespace Workspace.Tests.CompilationMapPayload;

public sealed class MapCompatibilityTests
{
    [Fact]
    public void Existing_v1_artifact_goldens_have_no_new_presence_encoding()
    {
        var snapshot = new CompilationSnapshotIdentity(
            Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"),
            ProjectId.Parse(Guid.Parse("00000000-0000-0000-0000-000000000003")), Guid.Parse("00000000-0000-0000-0000-000000000004"),
            0, null, 0, 1);
        byte[] pdf = [1, 2, 3];
        var input = new CompileOutputEnvelopeInput
        {
            RequestId = CompileRequestId.New(), SnapshotIdentity = snapshot, InputContentDigest = new(new string('a', 64)),
            WorkerEpoch = 1, Status = CompileOutputStatus.Succeeded, Payload = pdf, ClaimedLength = pdf.Length, ClaimedHash = Hash(pdf)
        };
        var budget = new CompileOutputBudget(1024, 4096, 16, 64, 256, 512);
        // Existing independently calculated Python v1 goldens, not new map semantics.
        foreach (var (value, golden) in new[]
        {
            (input, "0f248f9ca06d75aee548a66f280b5683f01112b4e9b4d3b964c0d2603d138edf"),
            (input with { OutputFormat = new("pdf", "1.0", new(new string('c', 64))) }, "d35b3cda95de3f4f6753b023039c3ad6a33c685676cca791c7b1fb7c5a5fa1ec"),
            (input with { SourceMapping = new(new(new string('d', 64)), 9) }, "93960632408bb157a78ff0be0088bb7c38d2ca6a59bb4b102bf685ca38fb6125")
        })
        {
            var result = CompileOutputEnvelope.Create(value, budget);
            Assert.Null(result.Failure); Assert.Equal(golden, result.Envelope!.ContentIdentity!.Digest.Hex);
            var empty = CompileOutputEnvelope.Create(value with { SourceMapPayload = [] }, budget);
            Assert.Equal(golden, empty.Envelope!.ContentIdentity!.Digest.Hex);
            Assert.Equal(result.Envelope.MetadataByteLength, empty.Envelope.MetadataByteLength);
        }
    }

    [Fact]
    public async Task Real_history_store_keeps_exact_legacy_two_object_codec_and_identity_only_mapping()
    {
        await using var f = await CompilationTestWorkspace.Open(); var snapshot = await f.Snapshot();
        var format = new CompileOutputDescriptor("pdf", "fixture-v1", null);
        var adapter = new CompileOutputDescriptor("historical-fixture-transport", "test-v1", null);
        var context = CompileRequestContext.Create(snapshot, 1, 4096, format, adapter).Context!;
        byte[] map = [0, 255, 128, 10]; // synthetic boundary-only opaque bytes
        var input = MapPayloadTests.Input(context, map);
        var outputBudget = new CompileOutputBudget(1024, 65536, 8, 64, 1024, 4096);
        var accepted = CompileOutputEnvelope.Create(input, outputBudget).Envelope!.TryAccept(context).Output!;
        var legacy = CompileOutputEnvelope.Create(input with { SourceMapPayload = null }, outputBudget).Envelope!.TryAccept(context).Output!;
        var historyBudget = new ArtifactHistoryBudget(new(32, 1048576, 8388608, 65536), 1048576, 65536,
            new(new(2, 256, 1048576, 2097152, 4096, 256, 256), 16384, 16, 32, 30000));
        var backend = new MemoryBackend();
        await using var store = new CompileArtifactHistoryStore(backend, f.Repo, format, adapter);
        var withBytes = await store.PersistAsync(accepted, null, historyBudget);
        var identityOnly = await store.PersistAsync(legacy, null, historyBudget);
        Assert.Equal(RuntimeAssetStatus.Installed, withBytes.Status); Assert.Equal(RuntimeAssetStatus.Installed, identityOnly.Status);
        var stored = backend.Files[withBytes.Key!.RecordId]; var oldStored = backend.Files[identityOnly.Key!.RecordId];
        Assert.Equal(2, stored.Count); Assert.Equal(oldStored[0], stored[0]); Assert.Equal(oldStored[1], stored[1]);
        // v1 metadata+PDF only: maps remain transient, no codec field/third object.
        var read = await store.ReadAsync(withBytes.Key, f.Repo.Project.Id, null, historyBudget);
        Assert.Equal(RuntimeAssetStatus.Ready, read.Status);
        Assert.Equal(accepted.ContentIdentity, read.Artifact!.ContentIdentity);
        Assert.Equal(accepted.SourceMapping, read.Artifact.ContentIdentity.SourceMapping);
        Assert.Equal(accepted.ToPayloadArray(), read.Artifact.CopyPdf());
        Assert.Null(typeof(HistoricalCompileArtifact).GetProperty("SourceMapPayloadBytes"));
        Assert.Null(typeof(HistoricalCompileArtifact).GetProperty("HasSourceMapPayload"));
        Assert.True(accepted.HasSourceMapPayload); Assert.False(legacy.HasSourceMapPayload);
        Assert.Equal(0, f.Repo.Commits);
    }

    // Minimal in-memory I/O only; the production store/codec/verifier run unchanged.
    private sealed class MemoryBackend : IRuntimeAssetBackend
    {
        internal readonly Dictionary<Guid, Dictionary<int, byte[]>> Files = [];
        private readonly Dictionary<Guid, string> _catalog = [];
        private bool _locked;
        public Task<IRuntimeAssetLease?> TryAcquireAsync()
        {
            if (_locked) return Task.FromResult<IRuntimeAssetLease?>(null);
            _locked = true; return Task.FromResult<IRuntimeAssetLease?>(new Lease(this));
        }
        public Task CreateStageAsync(IRuntimeAssetLease lease, Guid id, int max) { Files.Add(id, []); return Task.CompletedTask; }
        public Task WriteAsync(IRuntimeAssetLease lease, Guid id, int i, byte[] bytes) { Files[id][i] = bytes.ToArray(); return Task.CompletedTask; }
        public Task<StoredAssetBytes?> ReadAsync(Guid id, int i, long expected, long max)
            => Task.FromResult<StoredAssetBytes?>(Files.TryGetValue(id, out var files) && files.TryGetValue(i, out var bytes) ? new(bytes.Length, bytes.ToArray()) : null);
        public Task<string?> CatalogAsync(Guid id, int max) => Task.FromResult(_catalog.GetValueOrDefault(id));
        public Task<bool> StageExistsAsync(Guid id) => Task.FromResult(Files.ContainsKey(id));
        public Task PublishAsync(IRuntimeAssetLease lease, Guid id, string metadata, int max) { _catalog.Add(id, metadata); return Task.CompletedTask; }
        public Task<bool> DiscardAsync(IRuntimeAssetLease lease, Guid id) => Task.FromResult(!_catalog.ContainsKey(id) && Files.Remove(id));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Lease(MemoryBackend owner) : IRuntimeAssetLease
        { public ValueTask DisposeAsync() { owner._locked = false; return ValueTask.CompletedTask; } }
    }
}
