using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Client.Workspace.Domain;
using ContentHash = Client.Workspace.Domain.SHA256;

namespace Client.Compilation;

internal sealed class CompilationInputException(CompilationCaptureIssue issue) : Exception
{ internal CompilationCaptureIssue Issue { get; } = issue; }

/// <summary>Versioned unambiguous encoding: little-endian integer widths,
/// strict UTF-8 strings prefixed by int32 byte length, boolean byte, explicit
/// collection counts. Measure first; hashing allocates only already-bounded fields.</summary>
internal sealed class CompilationCanonical : IDisposable
{
    internal const string Domain = "overclone-compilation-input-v1";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly long _limit;
    private readonly IncrementalHash? _hash;
    internal CompilationCanonical(long limit, bool hash = false)
    { _limit = limit; if (hash) _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); }
    internal long Length { get; private set; }
    private long Remaining => _limit - Length;
    private void Reserve(long count)
    {
        if (count < 0 || count > Remaining) throw new CompilationInputException(CompilationCaptureIssue.MetadataLimit);
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
    internal void Text(string value, bool allowEmpty = false)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw new CompilationInputException(CompilationCaptureIssue.InvalidMetadata);
        if (value.Length > Remaining - 4) throw new CompilationInputException(CompilationCaptureIssue.MetadataLimit);
        int length;
        try { length = Utf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new CompilationInputException(CompilationCaptureIssue.InvalidMetadata); }
        // BCL byte counting can overflow int for a representable UTF-16 string.
        // That cannot become a representable bounded metadata field.
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { throw new CompilationInputException(CompilationCaptureIssue.MetadataLimit); }
        if (value.Any(char.IsControl)) throw new CompilationInputException(CompilationCaptureIssue.InvalidMetadata);
        Reserve(4L + length);
        if (_hash is not null)
        {
            Span<byte> prefix = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
            _hash.AppendData(prefix); _hash.AppendData(Utf8.GetBytes(value));
        }
    }
    internal static bool ValidHash(ContentHash hash) => hash.Hex is { Length: 64 }
        && hash.Hex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private void Hash(ContentHash hash)
    {
        if (!ValidHash(hash)) throw new CompilationInputException(CompilationCaptureIssue.InvalidMetadata);
        Text(hash.Hex);
    }
    private void Id(FileId id)
    {
        if (id.Value == Guid.Empty) throw new CompilationInputException(CompilationCaptureIssue.InvalidMetadata);
        Text(id.ToString());
    }
    private void Descriptor(CompilationDescriptor descriptor)
    {
        if (descriptor is null) throw new CompilationInputException(CompilationCaptureIssue.InvalidMetadata);
        Text(descriptor.Id); Text(descriptor.Version); Hash(descriptor.Hash);
    }
    private void Descriptors(IReadOnlyList<CompilationDescriptor> descriptors)
    {
        if (descriptors.Count < 0 || descriptors.Count > Remaining / 12)
            throw new CompilationInputException(CompilationCaptureIssue.MetadataLimit);
        Int32(descriptors.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < descriptors.Count; i++)
        {
            var item = descriptors[i]; Descriptor(item);
            if (!ids.Add(item.Id)) throw new CompilationInputException(CompilationCaptureIssue.InvalidMetadata);
        }
    }
    internal void Selection(FileId main, CompilationDescriptor engine, IReadOnlyList<CompilationDescriptor> bundles,
        IReadOnlyList<CompilationDescriptor> tools, IReadOnlyList<CompilationOption> options)
    {
        Id(main); Descriptor(engine); Descriptors(bundles); Descriptors(tools);
        if (options.Count < 0 || options.Count > Remaining / 8)
            throw new CompilationInputException(CompilationCaptureIssue.MetadataLimit);
        Int32(options.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < options.Count; i++)
        {
            var option = options[i];
            if (option is null) throw new CompilationInputException(CompilationCaptureIssue.InvalidMetadata);
            Text(option.Key); Text(option.Value, allowEmpty: true);
            if (!keys.Add(option.Key)) throw new CompilationInputException(CompilationCaptureIssue.InvalidMetadata);
        }
    }
    internal void Selection(CompilationSelection selection)
        => Selection(selection.MainFileId, selection.Engine, selection.Bundles, selection.Tools, selection.Options);
    internal void File(FileId id, string path, string kind, bool bom, long length, ContentHash hash)
    { FileHeader(id, path, kind, bom, length); Hash(hash); }
    internal void MeasureFile(FileId id, string path, string kind, bool bom, long length)
    {
        if (_hash is not null) throw new InvalidOperationException("File hash is required for a digest.");
        FileHeader(id, path, kind, bom, length);
        Reserve(4 + 64); // Length-prefixed, lowercase SHA-256 field, not dummy file content.
    }
    private void FileHeader(FileId id, string path, string kind, bool bom, long length)
    { Id(id); Text(path); Text(kind); Boolean(bom); Int64(length); }
    internal ContentHash Finish() => _hash is not null
        ? ContentHash.FromBytes(_hash.GetHashAndReset()) : throw new InvalidOperationException("Measurement has no digest.");
    public void Dispose() => _hash?.Dispose();
}
