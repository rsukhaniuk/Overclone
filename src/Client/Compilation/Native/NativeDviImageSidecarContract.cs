using System.Buffers.Binary;
using System.Text;
using Client.Workspace.Transfer;

namespace Client.Compilation.Native;

/// <summary>Volatile PNG bounding boxes for the P19 LaTeX/DVI input phase.</summary>
public static class NativeDviImageSidecarContract
{
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];
    private static ReadOnlySpan<byte> PngEnd => [0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130];
    private const int MaxHeaderChunks = 256;
    private const double MaxBoxBp = 16320;

    public static (NativeDviImageSidecar Lease, byte[] Bytes)[] Create(
        IReadOnlyList<CompilationInputFile> sources, IReadOnlyList<string> outputs, CancellationToken ct)
    {
        if (sources.Count > NativeThesisCompilerContract.ProjectFiles) return [];
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in sources.Select(source => source.RelativePath).Concat(outputs))
        {
            var key = ProjectPathValidator.ComparisonKey(name);
            occupied.Add(key);
            for (int i = key.IndexOf('/'); i >= 0; i = key.IndexOf('/', i + 1))
                directories.Add(key[..(i + 1)]);
        }
        var stems = sources.Select(source => source.RelativePath).Where(UsesXbb)
            .ToLookup(name => ProjectPathValidator.ComparisonKey(name[..name.LastIndexOf('.')]), StringComparer.Ordinal);

        var result = new List<(NativeDviImageSidecar, byte[])>();
        foreach (var image in sources)
        {
            ct.ThrowIfCancellationRequested();
            var name = image.RelativePath;
            if (!name.EndsWith(".png", StringComparison.Ordinal) && !name.EndsWith(".PNG", StringComparison.Ordinal)) continue;
            var stem = name[..^4];
            var sidecar = stem + ".xbb";
            // Keep authored sidecars and do not choose a bounding box for a
            // stem shared by different graphics or occupied virtual paths.
            if (stems[ProjectPathValidator.ComparisonKey(stem)].Count() != 1
                || NativeAuthoringV4Contract.ValidatePath(sidecar) is not null
                || ProjectPathValidator.FindConflict(sidecar, false, occupied, directories) is not null) continue;
            if (PngBox(image.ByteSpan) is { } box)
                result.Add((new(sidecar, name, image.Hash.Hex), box));
        }
        return result.ToArray();
    }

    private static bool UsesXbb(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 && name[(dot + 1)..].ToLowerInvariant() is "ai" or "pdf" or "bmp" or "png" or "jpg" or "jpeg" or "jp2" or "jpf";
    }

    private static byte[]? PngBox(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.StartsWith(PngSignature) || !bytes.EndsWith(PngEnd)) return null;
        bytes = bytes[PngSignature.Length..];
        var header = ReadChunk(bytes);
        if (header.Length != 21 || !header[..4].SequenceEqual("IHDR"u8)
            || !ValidCrc(header) || !ValidPngFormat(header[12..^4])) return null;
        uint width = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue) return null;
        bytes = bytes[(4 + header.Length)..];

        uint xPpm = 0, yPpm = 0;
        bool foundDensity = false;
        for (int i = 0; i < MaxHeaderChunks; i++)
        {
            var chunk = ReadChunk(bytes);
            if (chunk.IsEmpty) return null;
            var type = chunk[..4];
            if (type.SequenceEqual("IDAT"u8)) return FormatBox(width, height, xPpm, yPpm);
            if (type.SequenceEqual("pHYs"u8))
            {
                if (foundDensity || chunk.Length != 17 || !ValidCrc(chunk) || chunk[12] > 1) return null;
                foundDensity = true;
                if (chunk[12] == 1)
                {
                    xPpm = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
                    yPpm = BinaryPrimitives.ReadUInt32BigEndian(chunk[8..]);
                }
            }
            else if ((type[0] & 32) == 0 && !type.SequenceEqual("PLTE"u8)) return null;
            bytes = bytes[(4 + chunk.Length)..];
        }
        return null;
    }

    // An owned chunk starts with its type and ends with its checksum.
    private static ReadOnlySpan<byte> ReadChunk(ReadOnlySpan<byte> remaining)
    {
        if (remaining.Length < 12) return default;
        uint length = BinaryPrimitives.ReadUInt32BigEndian(remaining);
        return length > remaining.Length - 12 ? default : remaining.Slice(4, (int)length + 8);
    }

    private static bool ValidCrc(ReadOnlySpan<byte> chunk)
        => Crc32.Compute(chunk[..^4].ToArray()) == BinaryPrimitives.ReadUInt32BigEndian(chunk[^4..]);

    private static bool ValidPngFormat(ReadOnlySpan<byte> format)
        => format[2] == 0 && format[3] == 0 && format[4] <= 1 && (format[1] switch
        {
            0 => format[0] is 1 or 2 or 4 or 8 or 16,
            3 => format[0] is 1 or 2 or 4 or 8,
            2 or 4 or 6 => format[0] is 8 or 16,
            _ => false,
        });

    private static byte[]? FormatBox(uint width, uint height, uint xPpm, uint yPpm)
    {
        // dvipdfmx uses libpng's metre density, then extractbb rounds the
        // integer box and writes six decimals for the high-resolution box.
        double x = width * (xPpm == 0 ? 1 : 72.0 / 0.0254 / xPpm);
        double y = height * (yPpm == 0 ? 1 : 72.0 / 0.0254 / yPpm);
        if (x > MaxBoxBp || y > MaxBoxBp) return null;
        return Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"%%BoundingBox: 0 0 {(int)(x + 0.5)} {(int)(y + 0.5)}\n%%HiResBoundingBox: 0.000000 0.000000 {x:F6} {y:F6}\n"));
    }
}
