using System.Collections.Immutable;
using Client.RuntimeAssets;
using Client.Pdf;
using Client.Workspace.Domain;

namespace Client.Compilation.History;

public sealed record ArtifactHistoryBudget(CompilationCaptureBudget Input, int MaxPdfBytes, int MaxMetadataBytes,
    RuntimeAssetBudget Storage)
{
    public PdfCompiledInputCapacity? CompiledInputCapacity { get; init; }
    public bool IsValid => Input is { IsValid: true } && Storage is { IsValid: true } && MaxPdfBytes > 0
        && MaxPdfBytes <= (CompiledInputCapacity?.MaxInputBytes ?? PdfDocument.DefaultInputBytes)
        && MaxMetadataBytes is > 0 and <= 262144 && MaxPdfBytes <= Storage.Inventory.MaxAssetBytes && MaxMetadataBytes <= Storage.Inventory.MaxAssetBytes
        && (long)MaxPdfBytes + MaxMetadataBytes <= Storage.Inventory.MaxTotalBytes && Storage.Inventory.MaxAssets >= 2;
}
// Record/write ID is independent from deterministic B2a content identity. The
// storage inventory digest is only opaque integrity of the two stored objects.
public sealed record HistoricalArtifactKey(Guid RecordId, ProjectId ProjectId, SHA256 ContentDigest, SHA256 StorageInventoryDigest);
public enum RevisionFileAssociationStatus { Unassociated, VerifiedFileManifest, Mismatch, MissingOrCorrupt, Unavailable, WrongProject }
public sealed record RevisionFileAssociation(RevisionFileAssociationStatus Status, RevisionId? RevisionId)
{
    public string Scope => "RevisionFileManifestOnly";
    public string CompileSettingsAssociation => "Unavailable: M2 revisions do not persist compile selection";
}

// Never a live accepted output/context. Historical provenance counters are facts,
// NOT freshness authority. There is deliberately no IsCurrent/Context conversion.
public sealed class HistoricalCompileArtifact
{
    private readonly byte[] _pdf;
    internal HistoricalCompileArtifact(HistoricalArtifactKey key, CompileArtifactContentIdentity identity,
        CompilationSelection selection, ImmutableArray<ProjectFile> files, CompileRequestId request,
        CompilationSnapshotIdentity snapshot, long workerEpoch, RevisionId? originalRequestedRevision, byte[] ownedPdf)
    { Key = key; ContentIdentity = identity; Selection = selection; Files = files; OriginalRequestId = request; OriginalSnapshot = snapshot; OriginalWorkerEpoch = workerEpoch; OriginalRequestedRevision = originalRequestedRevision; _pdf = ownedPdf; }
    public string Classification => "HistoricalOnly";
    public HistoricalArtifactKey Key { get; }
    public CompileArtifactContentIdentity ContentIdentity { get; }
    public CompilationSelection Selection { get; }
    public ImmutableArray<ProjectFile> Files { get; }
    public CompileRequestId OriginalRequestId { get; }
    public CompilationSnapshotIdentity OriginalSnapshot { get; }
    public long OriginalWorkerEpoch { get; }
    public RevisionId? OriginalRequestedRevision { get; }
    public byte[] CopyPdf() => (byte[])_pdf.Clone();
    public long PdfLength => _pdf.LongLength;
}
public sealed record ArtifactHistoryResult(RuntimeAssetStatus Status, HistoricalArtifactKey? Key = null,
    HistoricalCompileArtifact? Artifact = null, RevisionFileAssociation? Association = null,
    bool PublicationUncertain = false, RuntimeAssetStatus? Cause = null);
