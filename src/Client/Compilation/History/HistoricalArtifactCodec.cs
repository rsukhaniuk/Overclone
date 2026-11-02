using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;
using Client.RuntimeAssets;
using Client.Compilation.SyncTeX;
using Hash = Client.Workspace.Domain.SHA256;

namespace Client.Compilation.History;

internal static class HistoricalArtifactCodec
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record Metadata(int Version, string Classification, ProjectId ProjectId, CompileRequestId OriginalRequest,
        CompilationSnapshotIdentity OriginalSnapshot, long OriginalWorkerEpoch, Hash InputDigest,
        CompilationSelectionInput Selection, ProjectFile[] Files, long PdfLength, Hash PdfHash,
        CompileOutputDescriptor Format, CompileOutputDescriptor Adapter, CompileSourceMappingIdentity? Mapping,
        Hash ContentDigest, RevisionId? OriginalRequestedRevision)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ArtifactVersion { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SourceMapGeometryIdentity? SourceMapGeometry { get; init; }
    }
    internal sealed record Validated(Metadata Metadata, CompilationSelection Selection, ImmutableArray<ProjectFile> Files, CompileArtifactContentIdentity Identity);
    internal static Metadata FromAccepted(AcceptedCompileOutput accepted, RevisionId? target) => new(accepted.ContentIdentity!.ArtifactVersion, "HistoricalOnly", accepted.SnapshotIdentity.ProjectId,
        accepted.RequestId, accepted.SnapshotIdentity, accepted.WorkerEpoch, accepted.InputContentDigest,
        new(accepted.Snapshot.Selection.MainFileId, accepted.Snapshot.Selection.Engine, accepted.Snapshot.Selection.Bundles,
            accepted.Snapshot.Selection.Tools, accepted.Snapshot.Selection.Options),
        accepted.Snapshot.Files.Select(f => new ProjectFile(f.FileId, f.RelativePath, f.MediaKind, f.Hash, f.ByteLength, f.HasBom)).ToArray(),
        accepted.OutputLength, accepted.OutputHash!.Value, accepted.OutputFormat!, accepted.OutputAdapter!, accepted.SourceMapping, accepted.ContentIdentity!.Digest, target)
        { ArtifactVersion = accepted.ContentIdentity!.ArtifactVersion == 2 ? 2 : null, SourceMapGeometry = accepted.SourceMapGeometry };
    internal static Validated Validate(Metadata value, ProjectId expectedProject, CompileOutputDescriptor format, CompileOutputDescriptor adapter, ArtifactHistoryBudget budget,
        IReadOnlyCollection<CompileOutputDescriptor>? additionalAdapters = null)
    {
        void Check([DoesNotReturnIf(false)] bool ok) { if (!ok) throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt); }
        Check(value is not null && value.Version is 1 or 2 && value.Classification == "HistoricalOnly" && value.ProjectId == expectedProject && expectedProject.Value != Guid.Empty);
        Check(value!.Version == 1 ? value.ArtifactVersion is null && value.SourceMapGeometry is null
            : value.ArtifactVersion == 2 && SourceMapGeometryCodec.IsValidIdentity(value.SourceMapGeometry)
                && value.Mapping is { ByteLength: > 0 and <= CompileOutputBudget.SourceMapPayloadByteLimit }
                && value.PdfLength > 0);
        var s = value.OriginalSnapshot;
        Check(s is not null && s.ProjectId == expectedProject && s.CaptureId != Guid.Empty && s.ServiceId != Guid.Empty && s.SessionId != Guid.Empty
            && s.WorkingGeneration >= 0 && s.LocalWorkspaceEpoch >= 0 && s.SelectionVersion >= 0 && (s.BaseHeadRevisionId is null || s.BaseHeadRevisionId.Value.Value != Guid.Empty));
        Check(value.OriginalRequest.Value != Guid.Empty && value.OriginalWorkerEpoch > 0 && value.PdfLength >= 0 && value.PdfLength <= budget.MaxPdfBytes
            && CompilationCanonical.ValidHash(value.InputDigest) && CompilationCanonical.ValidHash(value.PdfHash) && CompilationCanonical.ValidHash(value.ContentDigest)
            && value.Format == format && (value.Adapter == adapter || additionalAdapters?.Contains(value.Adapter) == true)
            && (value.OriginalRequestedRevision is null || value.OriginalRequestedRevision.Value.Value != Guid.Empty));
        using (var descriptors = new CompileArtifactCanonical(value.Version == 2 ? CompileArtifactCanonical.MetadataDomainV2 : CompileArtifactCanonical.MetadataDomain, budget.MaxMetadataBytes))
        {
            descriptors.Text(descriptors.Domain); descriptors.Descriptor(value.Format); descriptors.Descriptor(value.Adapter); descriptors.Mapping(value.Mapping);
            if (value.SourceMapGeometry is not null) descriptors.Geometry(value.SourceMapGeometry);
        }
        var selection = CompilationSelection.Create(value.Selection, budget.Input.MaxMetadataBytes).Selection; Check(selection is not null);
        Check(value.Files is not null && value.Files.Length > 0 && value.Files.Length <= budget.Input.MaxFiles);
        long total = 0; var ids = new HashSet<FileId>();
        using (var measure = new CompilationCanonical(budget.Input.MaxMetadataBytes))
        {
            measure.Text(CompilationCanonical.Domain); measure.Selection(selection!); measure.Int32(value.Files!.Length);
            foreach (var f in value.Files)
            {
                Check(f is not null && f.Id.Value != Guid.Empty && ids.Add(f.Id) && f.ByteLength >= 0 && f.ByteLength <= budget.Input.MaxFileBytes && f.ByteLength <= budget.Input.MaxTotalBytes - total
                    && f.MediaKind is "text" or "binary" && (f.MediaKind == "text" || !f.HasBom));
                total += f!.ByteLength;
                // Existing strict B1 encoding checks UTF8/hash/control constraints
                // BEFORE path normalization. No source bytes are persisted.
                measure.File(f.Id, f.RelativePath, f.MediaKind, f.HasBom, f.ByteLength, f.ContentHash);
                Check(f.RelativePath == ProjectPathValidator.Normalize(f.RelativePath) && ProjectPathValidator.Validate(f.RelativePath) is null);
            }
        }
        Check(value.Files!.Any(f => f.Id == selection!.MainFileId && f.MediaKind == "text") && ProjectTreeValidator.FindPathConflicts(value.Files.Select(f => f.RelativePath)) is null);
        var ordered = value.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ThenBy(f => f.Id.ToString(), StringComparer.Ordinal).ToImmutableArray();
        Hash digest;
        using (var canonical = new CompilationCanonical(budget.Input.MaxMetadataBytes, hash: true))
        {
            canonical.Text(CompilationCanonical.Domain); canonical.Selection(selection!); canonical.Int32(ordered.Length);
            foreach (var f in ordered) canonical.File(f.Id, f.RelativePath, f.MediaKind, f.HasBom, f.ByteLength, f.ContentHash);
            digest = canonical.Finish();
        }
        Check(digest == value.InputDigest);
        var identity = CompileOutputEnvelope.ComputeIdentity(digest, value.PdfLength, value.PdfHash, value.Format, value.Adapter, value.Mapping, value.SourceMapGeometry);
        Check(identity.Digest == value.ContentDigest);
        return new(value, selection!, ordered, identity);
    }
    internal static byte[] Encode(Metadata value, int maximum)
    {
        using var stream = new LimitedStream(maximum); using (var writer = new Utf8JsonWriter(stream)) JsonSerializer.Serialize(writer, value, Options);
        return stream.ToArray();
    }
    internal static Validated Decode(byte[] bytes, ProjectId project, CompileOutputDescriptor format, CompileOutputDescriptor adapter, ArtifactHistoryBudget budget,
        IReadOnlyCollection<CompileOutputDescriptor>? additionalAdapters = null)
    {
        if (bytes.Length > budget.MaxMetadataBytes) throw new RuntimeAssetIoException(RuntimeAssetStatus.Corrupt);
        using var doc = JsonDocument.Parse(bytes); Unique(doc.RootElement);
        var files = doc.RootElement.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is < 1 || files.GetArrayLength() > budget.Input.MaxFiles) throw new JsonException("Historical manifest count.");
        var value = JsonSerializer.Deserialize<Metadata>(bytes, Options) ?? throw new JsonException("Historical metadata.");
        using var shape = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, Options)); SameShape(doc.RootElement, shape.RootElement);
        return Validate(value, project, format, adapter, budget, additionalAdapters);
    }
    private static void SameShape(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind && !(actual.ValueKind == JsonValueKind.Number && expected.ValueKind == JsonValueKind.Number)) throw new JsonException("Historical field type.");
        if (actual.ValueKind == JsonValueKind.Object)
        {
            if (!actual.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected.EnumerateObject().Select(p => p.Name))) throw new JsonException("Historical schema.");
            foreach (var p in actual.EnumerateObject()) SameShape(p.Value, expected.GetProperty(p.Name));
        }
        else if (actual.ValueKind == JsonValueKind.Array)
        { if (actual.GetArrayLength() != expected.GetArrayLength()) throw new JsonException(); for (int i = 0; i < actual.GetArrayLength(); i++) SameShape(actual[i], expected[i]); }
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) { var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var p in value.EnumerateObject()) { if (!seen.Add(p.Name)) throw new JsonException("Duplicate historical field."); Unique(p.Value); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    private sealed class LimitedStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] b, int offset, int count) { Check(count); base.Write(b, offset, count); }
        public override void Write(ReadOnlySpan<byte> b) { Check(b.Length); base.Write(b); }
        private void Check(int count) { if (count > maximum - Length) throw new RuntimeAssetIoException(RuntimeAssetStatus.QuotaExceeded); }
    }
}
