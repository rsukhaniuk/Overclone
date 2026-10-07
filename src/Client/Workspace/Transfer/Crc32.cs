namespace Client.Workspace.Transfer;

/// <summary>
/// Small, tested IEEE 802.3 CRC-32 implementation (polynomial 0xEDB88320).
/// .NET 10's <c>ZipArchiveEntry.Crc32</c> exposes the stored CRC but does not
/// automatically validate it on read, so the import path computes CRC-32 over
/// the extracted entry bytes and compares. This is not a security hash;
/// SHA-256 (in <see cref="Client.Workspace.Domain.SHA256"/>) remains the
/// content-object digest.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            table[i] = c;
        }
        return table;
    }

    /// <summary>Compute CRC-32 over <paramref name="bytes"/>. Initialize to
    /// 0xFFFFFFFF and XOR the final word, per the standard.</summary>
    public static uint Compute(byte[] bytes)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < bytes.Length; i++)
            crc = Table[(crc ^ bytes[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Incremental CRC-32 over a stream, for streaming verification.</summary>
    public static uint Compute(Stream stream)
    {
        uint crc = 0xFFFFFFFFu;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                crc = Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
