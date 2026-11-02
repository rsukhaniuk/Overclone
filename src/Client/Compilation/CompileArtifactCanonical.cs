using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Client.Workspace.Domain;
using Client.Compilation.SyncTeX;
using ContentHash = Client.Workspace.Domain.SHA256;

namespace Client.Compilation;

// Bounded typed failure for the artifact canonical writer. Kept separate from
// B1's CompilationInputException so this slice never edits accepted sources.
internal sealed class CompileArtifactException(CompileOutputIssue issue) : Exception
{ internal CompileOutputIssue Issue { get; } = issue; }

/// <summary>Versioned unambiguous encoding for the artifact content identity and
/// the bounded output-metadata measure. Same primitive rules as B1's input
/// canonical writer (little-endian widths, length-prefixed strict UTF-8, boolean
/// byte, explicit counts) but a distinct domain. Does not share state with B1.
/// Ordered pass/sequence metadata is intentionally NOT modeled here: no completed
/// tool/pass exists, so optional structures are omitted rather than invented.</summary>
internal sealed class CompileArtifactCanonical : IDisposable
{
    internal const string ArtifactDomain = "overclone-compilation-artifact-v1";
    internal const string MetadataDomain = "overclone-compilation-output-metadata-v1";
    internal const string ArtifactDomainV2 = "overclone-compilation-artifact-v2";
    internal const string MetadataDomainV2 = "overclone-compilation-output-metadata-v2";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly long _limit;
    private readonly IncrementalHash? _hash;
    internal CompileArtifactCanonical(string domain, long limit, bool hash = false)
    { _limit = limit; if (hash) _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); Domain = domain; }
    internal string Domain { get; }
    internal long Length { get; private set; }
    private long Remaining => _limit - Length;
    private void Reserve(long count)
    {
        if (count < 0 || count > Remaining) throw new CompileArtifactException(CompileOutputIssue.OverMetadataBudget);
        Length += count;
    }
    internal void Int32(int value)
    {
        Reserve(4);
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _hash?.AppendData(bytes);
    }
    internal void Int64(long value)
    {
        Reserve(8);
        Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        _hash?.AppendData(bytes);
    }
    internal void Boolean(bool value)
    {
        Reserve(1); Span<byte> bytes = stackalloc byte[1]; bytes[0] = value ? (byte)1 : (byte)0;
        _hash?.AppendData(bytes);
    }
    internal void Text(string value, bool allowEmpty = false) => Text(value, -1, allowEmpty);
    internal void Text(string value, int maxBytes, bool allowEmpty = false)
        => WriteText(value, maxBytes, allowEmpty, log: false);
    // LOG ONLY: same binary encoding/budgets, exactly TAB/LF/CR admitted among
    // controls. Generic metadata cannot opt into this policy via Text overloads.
    internal void LogText(string value, int maxBytes)
        => WriteText(value, maxBytes, allowEmpty: true, log: true);
    private void WriteText(string value, int maxBytes, bool allowEmpty, bool log)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw new CompileArtifactException(CompileOutputIssue.InvalidMetadata);
        if (value.Length > Remaining - 4) throw new CompileArtifactException(CompileOutputIssue.OverMetadataBudget);
        // Cheap char-length lower bound before the UTF-8 byte scan: a field whose
        // char count already exceeds its byte budget cannot fit, so reject without
        // scanning (a one-byte cap cannot scan a huge field under a large aggregate cap).
        if (maxBytes >= 0 && value.Length > maxBytes) throw new CompileArtifactException(CompileOutputIssue.OverMetadataBudget);
        int length;
        try { length = Utf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new CompileArtifactException(CompileOutputIssue.MalformedUnicode); }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { throw new CompileArtifactException(CompileOutputIssue.Overflow); }
        if (maxBytes >= 0 && length > maxBytes) throw new CompileArtifactException(CompileOutputIssue.OverMetadataBudget);
        if (value.Any(c => char.IsControl(c) && !(log && c is '\t' or '\n' or '\r')))
            throw new CompileArtifactException(CompileOutputIssue.InvalidMetadata);
        Reserve(4L + length);
        if (_hash is not null)
        {
            Span<byte> prefix = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
            _hash.AppendData(prefix); _hash.AppendData(Utf8.GetBytes(value));
        }
    }
    private void Hash(ContentHash hash)
    {
        if (!CompilationCanonical.ValidHash(hash)) throw new CompileArtifactException(CompileOutputIssue.InvalidHash);
        Text(hash.Hex);
    }
    internal void Descriptor(CompileOutputDescriptor? descriptor)
    {
        Boolean(descriptor is not null);
        if (descriptor is null) return;
        if (descriptor.Id is null || descriptor.Version is null) throw new CompileArtifactException(CompileOutputIssue.InvalidMetadata);
        Text(descriptor.Id); Text(descriptor.Version);
        Boolean(descriptor.Hash is not null);
        if (descriptor.Hash is { } present) Hash(present);
    }
    internal void Mapping(CompileSourceMappingIdentity? mapping)
    {
        Boolean(mapping is not null);
        if (mapping is null) return;
        if (mapping.ByteLength < 0) throw new CompileArtifactException(CompileOutputIssue.InvalidMetadata);
        Int64(mapping.ByteLength); Hash(mapping.Hash);
    }
    // V2 suffix only. No presence/version byte is added to v1 encodings.
    internal void Geometry(SourceMapGeometryIdentity geometry)
    {
        if (!SourceMapGeometryCodec.IsValidIdentity(geometry)) throw new CompileArtifactException(CompileOutputIssue.InvalidSourceMapGeometry);
        Int64(geometry.ByteLength); Hash(geometry.Hash);
    }
    private void Diagnostic(CompileDiagnosticEntry entry, int maxCodeBytes, int maxMessageBytes)
    {
        if (entry is null || entry.Code is null || entry.SafeMessage is null)
            throw new CompileArtifactException(CompileOutputIssue.InvalidMetadata);
        Text(entry.Code, maxCodeBytes); Text(entry.SafeMessage, maxMessageBytes, allowEmpty: true);
    }
    // Validates, bounds and owns diagnostics in ONE walk: the count budget is
    // enforced BEFORE any indexer/enumerator access, so a hostile list whose
    // Count exceeds the limit is rejected without touching its indexer.
    // Returned entries keep the same immutable record/string references (no
    // ToCharArray/byte copies); caller mutation after return cannot retarget them.
    internal ImmutableArray<CompileDiagnosticEntry> Diagnostics(IReadOnlyList<CompileDiagnosticEntry>? diagnostics,
        int maxCount, int maxCodeBytes, int maxMessageBytes)
    {
        int count = diagnostics is null ? 0 : diagnostics.Count;
        if (count < 0 || count > maxCount) throw new CompileArtifactException(CompileOutputIssue.OverDiagnosticCount);
        if (count > Remaining / 8) throw new CompileArtifactException(CompileOutputIssue.OverMetadataBudget);
        Int32(count);
        if (count == 0) return ImmutableArray<CompileDiagnosticEntry>.Empty;
        var builder = ImmutableArray.CreateBuilder<CompileDiagnosticEntry>(count);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            var entry = diagnostics![i];
            Diagnostic(entry, maxCodeBytes, maxMessageBytes);
            if (!codes.Add(entry.Code)) throw new CompileArtifactException(CompileOutputIssue.InvalidMetadata);
            builder.Add(entry); // immutable record + immutable strings: safe shared ownership
        }
        return builder.MoveToImmutable();
    }
    internal ContentHash Finish() => _hash is not null
        ? ContentHash.FromBytes(_hash.GetHashAndReset()) : throw new InvalidOperationException("Measurement has no digest.");
    public void Dispose() => _hash?.Dispose();
}
