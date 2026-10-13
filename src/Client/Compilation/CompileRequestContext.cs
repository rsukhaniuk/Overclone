using Client.Workspace.Domain;

namespace Client.Compilation;

/// <summary>Trusted compile request context. Binds a fresh nonempty request id,
/// the exact owned B1 <see cref="CompilationInputSnapshot"/> and its full
/// identity/content digest, an explicit positive worker epoch, and optional
/// explicit immutable output format/adapter descriptors. The snapshot's own
/// engine/bundle/tool/options are authoritative; this type never copies them or
/// claims a default engine, capability, provisioning status or legal approval.
///
/// Epoch values are future transport-correlation metadata, NOT proof a worker ran
/// or that the workspace is still current. Exactly-once scheduling and actual
/// source/worker freshness checks remain future orchestrator duties; this type
/// only provides the validation seam against which an untrusted envelope is
/// compared in <see cref="CompileOutputEnvelope.TryAccept"/>.</summary>
public sealed class CompileRequestContext
{
    private CompileRequestContext(CompileRequestId requestId, CompilationInputSnapshot snapshot, long workerEpoch,
        CompileOutputDescriptor? outputFormat, CompileOutputDescriptor? outputAdapter, long descriptorMetadataBytes)
    {
        RequestId = requestId; Snapshot = snapshot; WorkerEpoch = workerEpoch;
        OutputFormat = outputFormat; OutputAdapter = outputAdapter; DescriptorMetadataByteLength = descriptorMetadataBytes;
    }
    public CompileRequestId RequestId { get; }
    public CompilationInputSnapshot Snapshot { get; }
    public CompilationSnapshotIdentity SnapshotIdentity => Snapshot.Identity;
    public SHA256 InputContentDigest => Snapshot.ContentDigest;
    public long WorkerEpoch { get; }
    public CompileOutputDescriptor? OutputFormat { get; }
    public CompileOutputDescriptor? OutputAdapter { get; }
    /// <summary>Canonical measure of the modeled format/adapter metadata only,
    /// not a wire-size or full worker-message budget.</summary>
    public long DescriptorMetadataByteLength { get; }

    public static CompileRequestContextCreation Create(CompilationInputSnapshot snapshot, long workerEpoch,
        int maxDescriptorMetadataBytes,
        CompileOutputDescriptor? outputFormat = null, CompileOutputDescriptor? outputAdapter = null)
    {
        if (maxDescriptorMetadataBytes <= 0 || maxDescriptorMetadataBytes > Array.MaxLength)
            return new(null, CompileOutputIssue.InvalidBudget);
        if (snapshot is null) return new(null, CompileOutputIssue.InvalidMetadata);
        if (workerEpoch <= 0) return new(null, CompileOutputIssue.InvalidBudget);
        long metadata;
        try
        {
            using var measure = new CompileArtifactCanonical(CompileArtifactCanonical.MetadataDomain, maxDescriptorMetadataBytes);
            measure.Text(measure.Domain); // domain is part of the descriptor-metadata encoding too
            measure.Descriptor(outputFormat); measure.Descriptor(outputAdapter);
            metadata = measure.Length;
        }
        catch (CompileArtifactException error) { return new(null, error.Issue); }
        return new(new(CompileRequestId.New(), snapshot, workerEpoch, outputFormat, outputAdapter, metadata), null);
    }
}
