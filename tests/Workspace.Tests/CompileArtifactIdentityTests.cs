using Client.Compilation;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;

namespace Workspace.Tests;

public sealed class CompileArtifactIdentityTests
{
    private const int DescriptorBudget = 4096;
    private static readonly CompileOutputBudget Budget = new(1024, 4096, 16, 64, 256, 512);
    private static readonly byte[] Pdf = Bytes("%PDF-1.4 synthetic, NOT a validity proof\n");
    private static readonly byte[] Other = Bytes("different synthetic output bytes\n");
    private static SHA256 Hash(byte[] b) => SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));

    private static CompileArtifactContentIdentity Identity(CompilationInputSnapshot snapshot, byte[] payload,
        CompileOutputDescriptor? format = null, CompileOutputDescriptor? adapter = null,
        CompileSourceMappingIdentity? mapping = null)
    {
        var input = new CompileOutputEnvelopeInput
        {
            RequestId = CompileRequestId.New(),
            SnapshotIdentity = snapshot.Identity,
            InputContentDigest = snapshot.ContentDigest,
            WorkerEpoch = 1,
            Status = CompileOutputStatus.Succeeded,
            Payload = payload,
            ClaimedLength = payload.Length,
            ClaimedHash = Hash(payload),
            OutputFormat = format,
            OutputAdapter = adapter,
            SourceMapping = mapping
        };
        return CompileOutputEnvelope.Create(input, Budget).Envelope!.ContentIdentity!;
    }

    [Fact]
    public async Task V1_artifact_encoding_has_an_independently_calculated_fixed_golden()
    {
        // Deterministic synthetic input digest + payload (the B1 default fixture uses random
        // file ids, so its snapshot digest is not stable). ComputeIdentity is internal, so the
        // golden exercises the public Create path with a valid fixed SnapshotIdentity.
        var snapshotIdentity = new CompilationSnapshotIdentity(
            Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"),
            ProjectId.Parse(Guid.Parse("00000000-0000-0000-0000-000000000003")), Guid.Parse("00000000-0000-0000-0000-000000000004"),
            0, null, 0, 1); // head-null project and zero local counters are valid
        byte[] payload = [1, 2, 3];
        var noOptional = CompileOutputEnvelope.Create(new CompileOutputEnvelopeInput
        {
            RequestId = CompileRequestId.New(),
            SnapshotIdentity = snapshotIdentity,
            InputContentDigest = new(new string('a', 64)),
            WorkerEpoch = 1,
            Status = CompileOutputStatus.Succeeded,
            Payload = payload,
            ClaimedLength = payload.Length,
            ClaimedHash = Hash(payload)
        }, Budget).Envelope!.ContentIdentity!;
        // Independently encoded via Python struct.pack: domain, 64*'a' digest, int64 length 3,
        // sha256([1,2,3]) hash, three absent-option booleans (overclone-compilation-artifact-v1).
        Assert.Equal("0f248f9ca06d75aee548a66f280b5683f01112b4e9b4d3b964c0d2603d138edf", noOptional.Digest.Hex);
        var withFormat = CompileOutputEnvelope.Create(new CompileOutputEnvelopeInput
        {
            RequestId = CompileRequestId.New(),
            SnapshotIdentity = snapshotIdentity,
            InputContentDigest = new(new string('a', 64)),
            WorkerEpoch = 1,
            Status = CompileOutputStatus.Succeeded,
            Payload = payload,
            ClaimedLength = payload.Length,
            ClaimedHash = Hash(payload),
            OutputFormat = new("pdf", "1.0", new(new string('c', 64)))
        }, Budget).Envelope!.ContentIdentity!;
        Assert.Equal("d35b3cda95de3f4f6753b023039c3ad6a33c685676cca791c7b1fb7c5a5fa1ec", withFormat.Digest.Hex);
        var withMapping = CompileOutputEnvelope.Create(new CompileOutputEnvelopeInput
        {
            RequestId = CompileRequestId.New(),
            SnapshotIdentity = snapshotIdentity,
            InputContentDigest = new(new string('a', 64)),
            WorkerEpoch = 1,
            Status = CompileOutputStatus.Succeeded,
            Payload = payload,
            ClaimedLength = payload.Length,
            ClaimedHash = Hash(payload),
            SourceMapping = new(new(new string('d', 64)), 9)
        }, Budget).Envelope!.ContentIdentity!;
        Assert.Equal("93960632408bb157a78ff0be0088bb7c38d2ca6a59bb4b102bf685ca38fb6125", withMapping.Digest.Hex);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Repeated_same_content_has_equal_identity_with_distinct_request_ids()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var a = Identity(snapshot, Pdf);
        var b = Identity(snapshot, Pdf);
        Assert.Equal(a.Digest, b.Digest);
        Assert.Equal(a, b);
        // Distinct request ids are not modeled inside content identity, but the
        // envelopes that produced them carry distinct echoed request ids:
        var inputA = new CompileOutputEnvelopeInput { RequestId = CompileRequestId.New(), SnapshotIdentity = snapshot.Identity, InputContentDigest = snapshot.ContentDigest, WorkerEpoch = 1, Status = CompileOutputStatus.Succeeded, Payload = Pdf, ClaimedLength = Pdf.Length, ClaimedHash = Hash(Pdf) };
        var inputB = inputA with { RequestId = CompileRequestId.New() };
        Assert.NotEqual(CompileOutputEnvelope.Create(inputA, Budget).Envelope!.EchoedRequestId,
                        CompileOutputEnvelope.Create(inputB, Budget).Envelope!.EchoedRequestId);
    }

    [Fact]
    public async Task Request_and_epoch_changes_do_not_affect_content_identity()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        // Actually vary the request id AND worker epoch over the SAME content.
        var ctxA = CompileRequestContext.Create(snapshot, 11, DescriptorBudget).Context!;
        var ctxB = CompileRequestContext.Create(snapshot, 22, DescriptorBudget).Context!;
        Assert.NotEqual(ctxA.RequestId, ctxB.RequestId);
        Assert.NotEqual(ctxA.WorkerEpoch, ctxB.WorkerEpoch);
        var acceptedA = CompileOutputEnvelope.Create(SuccessFor(ctxA), Budget).Envelope!.TryAccept(ctxA).Output!;
        var acceptedB = CompileOutputEnvelope.Create(SuccessFor(ctxB), Budget).Envelope!.TryAccept(ctxB).Output!;
        // Different request/epoch, same content -> equal content identity.
        Assert.Equal(acceptedA.ContentIdentity, acceptedB.ContentIdentity);
        Assert.Equal(acceptedA.ContentIdentity!.Digest, acceptedB.ContentIdentity!.Digest);
        // But the trusted provenance reflects the distinct epochs.
        Assert.NotEqual(acceptedA.WorkerEpoch, acceptedB.WorkerEpoch);
        Assert.NotEqual(acceptedA.RequestId, acceptedB.RequestId);
    }

    [Theory]
    [InlineData("input-main")]
    [InlineData("input-other")]
    [InlineData("input-binary")]
    [InlineData("selection-engine")]
    [InlineData("output-bytes")]
    public async Task Modeled_content_changes_change_identity(string change)
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var before = Identity(snapshot, Pdf);
        CompilationInputSnapshot changedSnapshot = snapshot;
        byte[] payload = Pdf;
        switch (change)
        {
            case "input-main": Assert.True(f.Workspace.EditText(f.Main, "changed main content")); changedSnapshot = await f.Snapshot(); break;
            case "input-other": Assert.True(f.Workspace.EditText(f.Workspace.Files.Single(w => w.RelativePath == "chapter.tex").Id, "changed include")); changedSnapshot = await f.Snapshot(); break;
            case "input-binary": Assert.True(f.Workspace.ReplaceFileBytes(f.Workspace.Files.Single(w => w.MediaKind == "binary").Id, [255, 9, 9])); changedSnapshot = await f.Snapshot(); break;
            case "selection-engine": f.Selection.SetSelection(Select(f.Main, "different-synthetic-engine")); changedSnapshot = await f.Snapshot(); break;
            case "output-bytes": payload = Other; break;
        }
        var after = Identity(changedSnapshot, payload);
        Assert.NotEqual(before.Digest, after.Digest);
    }

    [Fact]
    public async Task Output_format_adapter_and_mapping_each_change_identity()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var none = Identity(snapshot, Pdf);
        var format = Identity(snapshot, Pdf, format: new("pdf", "1.0", new(new string('a', 64))));
        Assert.NotEqual(none.Digest, format.Digest);
        var adapter = Identity(snapshot, Pdf, adapter: new("native", "0.3", new(new string('a', 64))));
        Assert.NotEqual(none.Digest, adapter.Digest);
        Assert.NotEqual(format.Digest, adapter.Digest);
        var mapping = Identity(snapshot, Pdf, mapping: new(new(new string('d', 64)), 9));
        Assert.NotEqual(none.Digest, mapping.Digest);
        // Each descriptor field independently affects identity.
        var formatVersion = Identity(snapshot, Pdf, format: new("pdf", "1.1", new(new string('a', 64))));
        Assert.NotEqual(format.Digest, formatVersion.Digest);
        var formatHash = Identity(snapshot, Pdf, format: new("pdf", "1.0", new(new string('b', 64))));
        Assert.NotEqual(format.Digest, formatHash.Digest);
    }

    [Fact]
    public async Task Content_identity_components_match_accepted_output()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        var format = new CompileOutputDescriptor("pdf", "1.0", new(new string('a', 64)));
        var mapping = new CompileSourceMappingIdentity(new(new string('d', 64)), 9);
        var ctx = CompileRequestContext.Create(snapshot, 5, DescriptorBudget, outputFormat: format).Context!;
        var input = new CompileOutputEnvelopeInput
        {
            RequestId = ctx.RequestId,
            SnapshotIdentity = ctx.SnapshotIdentity,
            InputContentDigest = ctx.InputContentDigest,
            WorkerEpoch = ctx.WorkerEpoch,
            Status = CompileOutputStatus.Succeeded,
            Payload = Pdf,
            ClaimedLength = Pdf.Length,
            ClaimedHash = Hash(Pdf),
            OutputFormat = format,
            SourceMapping = mapping
        };
        var accepted = CompileOutputEnvelope.Create(input, Budget).Envelope!.TryAccept(ctx).Output!;
        // Provenance is the trusted context/snapshot by reference.
        Assert.Same(ctx, accepted.RequestContext);
        Assert.Same(snapshot, accepted.Snapshot);
        // Identity components are observable through the accepted output, not a tautology.
        Assert.Equal(snapshot.ContentDigest, accepted.ContentIdentity!.InputContentDigest);
        Assert.Equal(Pdf.Length, accepted.ContentIdentity!.OutputLength);
        Assert.Equal(Hash(Pdf), accepted.ContentIdentity!.OutputHash);
        Assert.Equal(format, accepted.ContentIdentity!.OutputFormat);
        Assert.Equal(mapping, accepted.ContentIdentity!.SourceMapping);
        Assert.Equal(accepted.ContentIdentity, accepted.RequestContext.Snapshot is null ? accepted.ContentIdentity : accepted.ContentIdentity);
    }

    [Fact]
    public async Task Content_identity_excludes_durable_revision_link_and_base_head_does_not_enter()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var snapshot = await f.Snapshot();
        // Dirty input can differ from BaseHeadRevisionId; content identity uses the
        // owned content digest, never the revision id. No revision link is modeled.
        var identity = Identity(snapshot, Pdf);
        Assert.Null(identity.GetType().GetProperty("RevisionId"));
        Assert.Equal(snapshot.ContentDigest, identity.InputContentDigest); // content, not revision
        Assert.True(typeof(CompileArtifactContentIdentity).GetProperties().All(p => p.Name != "RevisionId"));
    }

    private static CompileOutputEnvelopeInput SuccessFor(CompileRequestContext ctx) => new()
    {
        RequestId = ctx.RequestId,
        SnapshotIdentity = ctx.SnapshotIdentity,
        InputContentDigest = ctx.InputContentDigest,
        WorkerEpoch = ctx.WorkerEpoch,
        Status = CompileOutputStatus.Succeeded,
        Payload = Pdf,
        ClaimedLength = Pdf.Length,
        ClaimedHash = Hash(Pdf)
    };
}
