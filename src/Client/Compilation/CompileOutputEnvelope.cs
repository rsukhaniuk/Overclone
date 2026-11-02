using System.Collections.Immutable;
using System.Security.Cryptography;
using Client.Workspace.Domain;
using Client.Compilation.SyncTeX;
using ContentHash = Client.Workspace.Domain.SHA256;

namespace Client.Compilation;

/// <summary>Untrusted compile output envelope after intrinsic validation only.
/// It privately owns accepted payload bytes (Succeeded only) but does NOT expose
/// them: an untrusted envelope is parsed/owned/corruption-checked, not yet bound
/// to a trusted request. Call <see cref="TryAccept"/> with the EXPECTED trusted
/// <see cref="CompileRequestContext"/> to validate echoed identity and obtain an
/// <see cref="AcceptedCompileOutput"/> that may expose the artifact.
///
/// Identity validation compares echoed values to the expected trusted context and
/// rejects mismatches; it never constructs authority from the echoed values. This
/// comparison is a validation seam only: it does NOT establish exactly-once
/// scheduling, current worker state, current workspace freshness, a durable
/// revision link or PDF validity. A late response for an old request id cannot
/// satisfy a newer request because the echoed request id will not match.</summary>
public sealed class CompileOutputEnvelope
{
    private readonly byte[]? _payload; // Owned. Null for non-success. Never exposed as an array.
    private readonly byte[]? _sourceMapPayload; // Optional opaque owned bytes; never public on an envelope.
    private readonly SourceMapGeometryArtifact? _geometry; // privately copied/validated wrapper, never live authority

    private CompileOutputEnvelope(CompileRequestId echoedRequestId, CompilationSnapshotIdentity echoedSnapshotIdentity,
        ContentHash echoedInputDigest, long echoedWorkerEpoch, CompileOutputStatus status,
        CompileOutputDescriptor? outputFormat, CompileOutputDescriptor? outputAdapter,
        CompileSourceMappingIdentity? sourceMapping, ImmutableArray<CompileDiagnosticEntry> diagnostics,
        string logText, byte[]? payload, byte[]? sourceMapPayload, SourceMapGeometryArtifact? geometry, long outputLength, ContentHash? outputHash,
        CompileArtifactContentIdentity? contentIdentity, long metadataByteLength)
    {
        EchoedRequestId = echoedRequestId; EchoedSnapshotIdentity = echoedSnapshotIdentity; EchoedInputContentDigest = echoedInputDigest;
        EchoedWorkerEpoch = echoedWorkerEpoch; Status = status; OutputFormat = outputFormat; OutputAdapter = outputAdapter;
        SourceMapping = sourceMapping; Diagnostics = diagnostics; LogText = logText; _payload = payload;
        _sourceMapPayload = sourceMapPayload; _geometry = geometry;
        OutputLength = outputLength; OutputHash = outputHash; ContentIdentity = contentIdentity; MetadataByteLength = metadataByteLength;
    }

    public CompileRequestId EchoedRequestId { get; }
    public CompilationSnapshotIdentity EchoedSnapshotIdentity { get; }
    public ContentHash EchoedInputContentDigest { get; }
    public long EchoedWorkerEpoch { get; }
    public CompileOutputStatus Status { get; }
    public CompileOutputDescriptor? OutputFormat { get; }
    public CompileOutputDescriptor? OutputAdapter { get; }
    public CompileSourceMappingIdentity? SourceMapping { get; }
    public SourceMapGeometryIdentity? SourceMapGeometry => _geometry?.Identity;
    public ImmutableArray<CompileDiagnosticEntry> Diagnostics { get; }
    public string LogText { get; }
    public long OutputLength { get; }
    public ContentHash? OutputHash { get; }
    /// <summary>Content identity for Succeeded only; null for non-success
    /// (no artifact exists to identify).</summary>
    public CompileArtifactContentIdentity? ContentIdentity { get; }
    public long MetadataByteLength { get; }

    public static CompileOutputEnvelopeCreation Create(CompileOutputEnvelopeInput input, CompileOutputBudget budget)
    {
        if (budget is null || !budget.IsValid) return new(null, CompileOutputIssue.InvalidBudget);
        if (input is null) return new(null, CompileOutputIssue.InvalidMetadata);
        if (!Enum.IsDefined(input.Status)) return new(null, CompileOutputIssue.InvalidStatus);

        // Phase A: cheap structural validation. No exception escapes, no diagnostics
        // entry enumeration, no payload copy/hash and no metadata scan. These checks
        // reject malformed identity and over-budget structures before any expensive
        // work; a typed failure is returned for every invariant violated.
        var status = input.Status;
        var payload = input.Payload;
        var sourceMapPayload = input.SourceMapPayload;
        var mapping = input.SourceMapping;
        var geometryPayload = input.SourceMapGeometryPayload;
        var geometryClaim = input.SourceMapGeometry;
        bool geometryPresent = geometryPayload is not null || geometryClaim is not null;
        if (input.RequestId.Value == Guid.Empty) return new(null, CompileOutputIssue.RequestMismatch);
        if (input.WorkerEpoch <= 0) return new(null, CompileOutputIssue.EpochMismatch);
        if (!CompilationCanonical.ValidHash(input.InputContentDigest)) return new(null, CompileOutputIssue.DigestMismatch);
        if (!ValidateSnapshotIdentity(input.SnapshotIdentity)) return new(null, CompileOutputIssue.SnapshotMismatch);
        if ((input.Diagnostics?.Count ?? 0) > budget.MaxDiagnostics)
            return new(null, CompileOutputIssue.OverDiagnosticCount);

        if (status == CompileOutputStatus.Succeeded)
        {
            if (payload is null) return new(null, CompileOutputIssue.MissingPayload);
            if (payload.LongLength > budget.MaxPayloadBytes) return new(null, CompileOutputIssue.OverPayloadBudget);
            if (input.ClaimedLength < 0 || input.ClaimedLength > budget.MaxPayloadBytes)
                return new(null, CompileOutputIssue.OverPayloadBudget);
            if (payload.LongLength != input.ClaimedLength) return new(null, CompileOutputIssue.LengthMismatch);
            if (input.ClaimedHash is null) return new(null, CompileOutputIssue.InvalidHash);
            if (!CompilationCanonical.ValidHash(input.ClaimedHash.GetValueOrDefault()))
                return new(null, CompileOutputIssue.InvalidHash);
        }
        else
        {
            if (payload is not null && payload.LongLength > 0)
                return new(null, CompileOutputIssue.PayloadOnNonSuccess);
        }

        // Independent source-map preflight BEFORE any payload copy/hash or metadata
        // scan. Empty success input means absence, not evidence of a usable map.
        // Identity-only legacy mappings remain unrestricted by the map-byte cap.
        if (status != CompileOutputStatus.Succeeded && sourceMapPayload is not null)
            return new(null, CompileOutputIssue.PayloadOnNonSuccess);
        if (sourceMapPayload is { Length: > 0 })
        {
            if (sourceMapPayload.LongLength > budget.MaxSourceMapPayloadBytes)
                return new(null, CompileOutputIssue.OverSourceMapPayloadBudget);
            if (mapping is null) return new(null, CompileOutputIssue.MappingMismatch);
            if (mapping.ByteLength != sourceMapPayload.LongLength)
                return new(null, CompileOutputIssue.SourceMapLengthMismatch);
            if (!CompilationCanonical.ValidHash(mapping.Hash)) return new(null, CompileOutputIssue.InvalidHash);
        }

        // Geometry presence is not an identity-only or empty-payload shortcut.
        // Independent caps win before metadata enumeration, copying or hashing.
        if (status != CompileOutputStatus.Succeeded && geometryPresent)
            return new(null, CompileOutputIssue.PayloadOnNonSuccess);
        if (geometryPayload is not null && geometryPayload.Length > budget.MaxSourceMapGeometryBytes)
            return new(null, CompileOutputIssue.OverSourceMapGeometryBudget);
        if (geometryPresent)
        {
            if (geometryPayload is not { Length: > 0 } || payload is not { Length: > 0 }
                || sourceMapPayload is not { Length: > 0 } || mapping is null)
                return new(null, CompileOutputIssue.InvalidSourceMapGeometry);
            if (!SourceMapGeometryCodec.IsValidIdentity(geometryClaim) || geometryClaim!.ByteLength != geometryPayload.LongLength)
                return new(null, CompileOutputIssue.SourceMapGeometryMismatch);
        }

        // Phase B: expensive validation/ownership. Descriptors, mapping, diagnostics
        // and log are validated+owned under the aggregate metadata budget; per-field
        // byte budgets are enforced before the byte scan. Payload is copied before
        // hashing (after the count/size preflight above).
        var format = input.OutputFormat;
        var adapter = input.OutputAdapter;
        long outputLength = 0;
        ContentHash? outputHash = null;
        byte[]? owned = null;
        byte[]? ownedSourceMap = null;
        SourceMapGeometryArtifact? ownedGeometry = null;
        CompileArtifactContentIdentity? identity = null;
        ImmutableArray<CompileDiagnosticEntry> diagnostics;
        string logText;
        long metadataLength;
        try
        {
            using var measure = new CompileArtifactCanonical(geometryPresent ? CompileArtifactCanonical.MetadataDomainV2 : CompileArtifactCanonical.MetadataDomain, budget.MaxMetadataBytes);
            measure.Text(measure.Domain); // domain is part of the metadata encoding
            measure.Descriptor(format); measure.Descriptor(adapter); measure.Mapping(mapping);
            diagnostics = measure.Diagnostics(input.Diagnostics, budget.MaxDiagnostics, budget.MaxDiagnosticCodeBytes, budget.MaxDiagnosticMessageBytes);
            measure.LogText(input.LogText ?? "", budget.MaxLogBytes);
            if (geometryPresent) measure.Geometry(geometryClaim!); // exact V2 suffix, no V1 presence flag
            metadataLength = measure.Length;
            logText = input.LogText ?? "";

            if (status == CompileOutputStatus.Succeeded)
            {
                owned = payload!.ToArray(); // copy BEFORE hash, AFTER preflight
                outputLength = owned.LongLength;
                outputHash = ContentHash.FromBytes(System.Security.Cryptography.SHA256.HashData(owned));
                if (outputHash != input.ClaimedHash) return new(null, CompileOutputIssue.HashMismatch);
                if (sourceMapPayload is { Length: > 0 })
                {
                    ownedSourceMap = sourceMapPayload.ToArray(); // hash only the private copy
                    var mapHash = ContentHash.FromBytes(System.Security.Cryptography.SHA256.HashData(ownedSourceMap));
                    if (mapHash != mapping!.Hash) return new(null, CompileOutputIssue.SourceMapHashMismatch);
                }
                if (geometryPresent)
                {
                    var parsed = SourceMapGeometryCodec.ParseWrapper(geometryPayload);
                    if (parsed.Artifact is not { } artifact) return new(null, CompileOutputIssue.InvalidSourceMapGeometry);
                    if (artifact.Identity != geometryClaim || artifact.Geometry.PdfSha256 != outputHash.Value.Hex
                        || artifact.Geometry.MapSha256 != mapping!.Hash.Hex)
                        return new(null, CompileOutputIssue.SourceMapGeometryMismatch);
                    ownedGeometry = artifact; // parser owns independent bytes before validate/hash
                }
                // Mapping presence is never encoded again. Geometry adds only
                // the versioned domain and explicit length/hash suffix.
                identity = ComputeIdentity(input.InputContentDigest, outputLength, outputHash.Value, format, adapter, mapping, ownedGeometry?.Identity);
            }
            else
            {
                logText = input.LogText ?? "";
            }
        }
        catch (CompileArtifactException error) { return new(null, error.Issue); }
        catch (OutOfMemoryException) { throw; }

        return new(new(input.RequestId, input.SnapshotIdentity, input.InputContentDigest, input.WorkerEpoch, status,
            format, adapter, mapping, diagnostics, logText, owned, ownedSourceMap, ownedGeometry, outputLength, outputHash, identity, metadataLength), null);
    }

    /// <summary>Read-only view of the privately owned payload. The backing array
    /// never escapes this assembly; callers obtain a span or a defensive copy.</summary>
    internal ReadOnlySpan<byte> PayloadSpan => _payload;
    /// <summary>Defensive copy of the owned payload, or null when there is
    /// no artifact (non-success). Never returns an empty alias.</summary>
    internal byte[]? PayloadArray => _payload?.ToArray();
    internal bool HasSourceMapPayload => _sourceMapPayload is not null;
    internal ReadOnlySpan<byte> SourceMapPayloadSpan => _sourceMapPayload;
    internal byte[]? SourceMapPayloadArray => _sourceMapPayload?.ToArray();
    internal bool HasSourceMapGeometry => _geometry is not null;
    internal ReadOnlySpan<byte> SourceMapGeometrySpan => _geometry is { } g ? g.BytesSpan : ReadOnlySpan<byte>.Empty;
    internal byte[]? SourceMapGeometryArray => _geometry?.ToArray();

    public CompileOutputAcceptance TryAccept(CompileRequestContext expected)
    {
        if (expected is null) return new(null, CompileOutputIssue.InvalidMetadata);
        if (EchoedRequestId != expected.RequestId) return new(null, CompileOutputIssue.RequestMismatch);
        if (EchoedSnapshotIdentity != expected.SnapshotIdentity) return new(null, CompileOutputIssue.SnapshotMismatch);
        if (EchoedInputContentDigest != expected.InputContentDigest) return new(null, CompileOutputIssue.DigestMismatch);
        if (EchoedWorkerEpoch != expected.WorkerEpoch) return new(null, CompileOutputIssue.EpochMismatch);
        if (!EqualDescriptor(OutputFormat, expected.OutputFormat)) return new(null, CompileOutputIssue.FormatMismatch);
        if (!EqualDescriptor(OutputAdapter, expected.OutputAdapter)) return new(null, CompileOutputIssue.AdapterMismatch);
        // SourceMapping is output-produced metadata, not request-matched: it
        // enters content identity but the request does not specify a mapping.
        return new(new AcceptedCompileOutput(expected, this), null);
    }

    internal static CompileArtifactContentIdentity ComputeIdentity(ContentHash inputDigest, long outputLength,
        ContentHash outputHash, CompileOutputDescriptor? format, CompileOutputDescriptor? adapter, CompileSourceMappingIdentity? mapping,
        SourceMapGeometryIdentity? geometry = null)
    {
        using var canonical = new CompileArtifactCanonical(geometry is null ? CompileArtifactCanonical.ArtifactDomain : CompileArtifactCanonical.ArtifactDomainV2, Array.MaxLength, hash: true);
        canonical.Text(canonical.Domain);
        canonical.Text(inputDigest.Hex);
        canonical.Int64(outputLength);
        canonical.Text(outputHash.Hex);
        canonical.Descriptor(format);
        canonical.Descriptor(adapter);
        canonical.Mapping(mapping);
        if (geometry is not null) canonical.Geometry(geometry);
        return new(canonical.Finish(), inputDigest, outputLength, outputHash, format, adapter, mapping)
            { ArtifactVersion = geometry is null ? 1 : 2, SourceMapGeometry = geometry };
    }

    private static bool ValidateSnapshotIdentity(CompilationSnapshotIdentity identity)
    {
        // Full identity invariants, not just four GUIDs. Null (passed via null!) is a
        // typed SnapshotMismatch, never a NullReferenceException. Head-null projects
        // (BaseHeadRevisionId == null) are valid; zero-based local counters are valid.
        return identity is not null
            && identity.CaptureId != Guid.Empty && identity.ServiceId != Guid.Empty
            && identity.ProjectId.Value != Guid.Empty && identity.SessionId != Guid.Empty
            && identity.WorkingGeneration >= 0 && identity.LocalWorkspaceEpoch >= 0 && identity.SelectionVersion >= 0
            && (identity.BaseHeadRevisionId is null || identity.BaseHeadRevisionId.Value.Value != Guid.Empty);
    }
    private static bool EqualDescriptor(CompileOutputDescriptor? a, CompileOutputDescriptor? b)
        => (a is null) == (b is null) && (a is null || (a.Id == b!.Id && a.Version == b.Version && a.Hash == b.Hash));
}

/// <summary>Accepted compile output: an untrusted envelope whose echoed identity
/// matched the EXPECTED trusted request context. Retains that trusted context by
/// reference (never reconstructs provenance from echoed values). Only this type
/// exposes the accepted artifact bytes (Succeeded only) via a read-only span and a
/// defensive copy. Non-success accepted outputs carry status and bounded
/// diagnostics only; they never expose a payload or a prior PDF.</summary>
public sealed class AcceptedCompileOutput
{
    private readonly CompileRequestContext _context;
    private readonly CompileOutputEnvelope _envelope;
    internal AcceptedCompileOutput(CompileRequestContext context, CompileOutputEnvelope envelope)
    { _context = context; _envelope = envelope; }
    /// <summary>The trusted request context the envelope was accepted against.</summary>
    public CompileRequestContext RequestContext => _context;
    public CompileRequestId RequestId => _context.RequestId;
    public CompilationInputSnapshot Snapshot => _context.Snapshot;
    public CompilationSnapshotIdentity SnapshotIdentity => _context.SnapshotIdentity;
    public ContentHash InputContentDigest => _context.InputContentDigest;
    public long WorkerEpoch => _context.WorkerEpoch;
    public CompileOutputStatus Status => _envelope.Status;
    public CompileOutputDescriptor? OutputFormat => _envelope.OutputFormat;
    public CompileOutputDescriptor? OutputAdapter => _envelope.OutputAdapter;
    public CompileSourceMappingIdentity? SourceMapping => _envelope.SourceMapping;
    /// <summary>Owned wrapper DATA identity only; not a current geometry/query ticket.</summary>
    public SourceMapGeometryIdentity? SourceMapGeometry => _envelope.SourceMapGeometry;
    public bool HasSourceMapGeometry => _envelope.HasSourceMapGeometry;
    public ReadOnlySpan<byte> SourceMapGeometryBytes => _envelope.SourceMapGeometrySpan;
    public byte[]? ToSourceMapGeometryArray() => _envelope.SourceMapGeometryArray;
    public ImmutableArray<CompileDiagnosticEntry> Diagnostics => _envelope.Diagnostics;
    public string LogText => _envelope.LogText;
    public long MetadataByteLength => _envelope.MetadataByteLength;
    /// <summary>Content identity for Succeeded only; null for non-success.</summary>
    public CompileArtifactContentIdentity? ContentIdentity => _envelope.ContentIdentity;
    public long OutputLength => _envelope.OutputLength;
    public ContentHash? OutputHash => _envelope.OutputHash;
    /// <summary>Read-only view of the accepted artifact. Empty for non-success.
    /// Backing array never escapes; <see cref="ToPayloadArray"/> copies.</summary>
    public ReadOnlySpan<byte> PayloadBytes => _envelope.PayloadSpan;
    public byte[]? ToPayloadArray() => _envelope.PayloadArray;
    /// <summary>True only for owned nonempty map bytes, not identity-only metadata.
    /// This is byte ownership/correlation, NOT semantic validity or navigation.</summary>
    public bool HasSourceMapPayload => _envelope.HasSourceMapPayload;
    /// <summary>Opaque accepted map bytes; empty when absent. The private backing
    /// array never escapes. No parsing or map-production proof is implied.</summary>
    public ReadOnlySpan<byte> SourceMapPayloadBytes => _envelope.SourceMapPayloadSpan;
    /// <summary>Defensive copy of accepted map bytes, or null when absent.</summary>
    public byte[]? ToSourceMapPayloadArray() => _envelope.SourceMapPayloadArray;
}
