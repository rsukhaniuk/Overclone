using System.Collections.Immutable;
using Client.Workspace.Domain;

namespace Client.Compilation;

/// <summary>Explicit caller limits, not final product/provisioning policy. Zero is
/// never unlimited. Empty files are valid within positive finite limits.</summary>
public sealed record CompilationCaptureBudget(int MaxFiles, long MaxFileBytes, long MaxTotalBytes, int MaxMetadataBytes)
{
    public bool IsValid => MaxFiles > 0 && MaxFileBytes > 0 && MaxFileBytes <= Array.MaxLength
        && MaxTotalBytes > 0 && MaxMetadataBytes > 0 && MaxMetadataBytes <= Array.MaxLength;
}

// Untrusted immutable value records; lists in SelectionInput may alias callers.
// Only CompilationSelection.Create validates/owns a usable selection.
public sealed record CompilationDescriptor(string Id, string Version, SHA256 Hash);
public sealed record CompilationOption(string Key, string Value);
public sealed record CompilationSelectionInput(FileId MainFileId, CompilationDescriptor Engine,
    IReadOnlyList<CompilationDescriptor> Bundles, IReadOnlyList<CompilationDescriptor> Tools,
    IReadOnlyList<CompilationOption> Options);
public sealed record CompilationSelectionCreation(CompilationSelection? Selection, CompilationCaptureIssue? Failure);

public enum CompilationCaptureIssue
{
    InvalidBudget, InvalidMetadata, InvalidWorkspace, InvalidFile, InvalidPath,
    DuplicateFile, InvalidMainFile, FileCountLimit, FileByteLimit, TotalByteLimit,
    MetadataLimit, ReadFailure, CorruptBytes
}

/// <summary>Bounded reference only; invalid paths are not echoed into errors.</summary>
public sealed record CompilationFileReference(FileId FileId, string? RelativePath);

public abstract record CompilationCaptureResult
{
    private CompilationCaptureResult() { }
    public sealed record Captured(CompilationInputSnapshot Snapshot) : CompilationCaptureResult;
    public sealed record NoProject : CompilationCaptureResult;
    public sealed record NoSelection : CompilationCaptureResult;
    public sealed record InvalidInput(CompilationCaptureIssue Issue, CompilationFileReference? File = null) : CompilationCaptureResult
    { public string SafeMessage => "Compilation input metadata is invalid; no snapshot was captured."; }
    public sealed record QuotaExceeded(CompilationCaptureIssue Issue, CompilationFileReference? File = null) : CompilationCaptureResult
    { public string SafeMessage => "Compilation input exceeds the explicit capture budget; no snapshot was captured."; }
    public sealed record Unavailable(CompilationCaptureIssue Issue, CompilationFileReference? File = null) : CompilationCaptureResult
    { public string SafeMessage => "Compilation input bytes could not be verified; no snapshot was captured."; }
    public sealed record Superseded : CompilationCaptureResult;
    public sealed record Cancelled : CompilationCaptureResult;
    public sealed record Disposed : CompilationCaptureResult;
}

/// <summary>Private owned bytes. Neither a mutable array nor its Memory backing
/// escapes. ToArray is defensive; ByteSpan is read-only. No UTF-8 assumption for binaries.</summary>
public sealed class CompilationInputFile
{
    private readonly byte[] _bytes;
    internal CompilationInputFile(FileId id, string path, string mediaKind, bool hasBom, SHA256 hash, byte[] owned)
    { FileId = id; RelativePath = path; MediaKind = mediaKind; HasBom = hasBom; Hash = hash; _bytes = owned; }
    public FileId FileId { get; }
    public string RelativePath { get; }
    public string MediaKind { get; }
    public bool HasBom { get; }
    public SHA256 Hash { get; }
    public long ByteLength => _bytes.LongLength;
    public ReadOnlySpan<byte> ByteSpan => _bytes;
    public byte[] ToArray() => _bytes.ToArray();
}

/// <summary>Freshness identity, never part of the deterministic content digest.
/// ServiceId gives the local epoch its scope; Head is the captured loaded durable head.</summary>
public sealed record CompilationSnapshotIdentity(Guid CaptureId, Guid ServiceId, ProjectId ProjectId,
    Guid SessionId, long WorkingGeneration, RevisionId? BaseHeadRevisionId, long LocalWorkspaceEpoch, long SelectionVersion);

/// <summary>Engine-independent owned input, not a compiler job/result/PDF. The
/// digest covers all file IDs/paths/media/BOM/lengths/hashes and explicit selection,
/// not capture/session/head/generation/epoch/version counters, timestamps or budgets.</summary>
public sealed class CompilationInputSnapshot
{
    internal CompilationInputSnapshot(CompilationSnapshotIdentity identity, CompilationSelection selection,
        CompilationCaptureBudget budget, ImmutableArray<CompilationInputFile> files, SHA256 digest, long metadataBytes)
    { Identity = identity; Selection = selection; Budget = budget; Files = files; ContentDigest = digest; MetadataByteLength = metadataBytes; }
    public CompilationSnapshotIdentity Identity { get; }
    public CompilationSelection Selection { get; }
    public FileId MainFileId => Selection.MainFileId;
    public CompilationCaptureBudget Budget { get; }
    public ImmutableArray<CompilationInputFile> Files { get; }
    public SHA256 ContentDigest { get; }
    public long MetadataByteLength { get; }
}
