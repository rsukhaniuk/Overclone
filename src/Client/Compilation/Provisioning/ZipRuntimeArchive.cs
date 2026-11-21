using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Client.Compilation.Native;

namespace Client.Compilation.Provisioning;

/// <summary>Bounded local ZIP bytes, never extraction or executable selection.
/// The compressed source is buffered once (browser streams need not seek).
/// Directory preflight and trusted pins precede every resource entry stream.
/// </summary>
public sealed class ZipRuntimeArchive : IAsyncDisposable
{
    public const long MaxZipBytes = 268435456;
    private Stream? _source;
    private byte[] _bytes = [];
    private Task? _preparation;
    private readonly List<Task<byte[]>> _reads = [];
    private bool _closing;
    private int _reading;
    public int PinsStreamsOpened { get; private set; }
    public int ResourceStreamsOpened { get; private set; }
    public byte[] Pins { get; private set; } = [];
    public IReadOnlyList<LocalRuntimeFile> Files { get; private set; } = [];

    public Task PrepareAsync(long length, Func<CancellationToken, Task<Stream>> open, CancellationToken token)
    {
        if (_preparation is not null || _closing) throw Invalid();
        _preparation = PrepareActual(length, open, token);
        return _preparation;
    }
    private async Task PrepareActual(long length, Func<CancellationToken, Task<Stream>> open, CancellationToken token)
    {
        if (length < 22 || length > MaxZipBytes) throw Invalid();
        token.ThrowIfCancellationRequested();
        _source = await open(token); // Await actual IO, never a cancellation/timeout proxy.
        var bytes = new byte[checked((int)length)];
        await _source.ReadExactlyAsync(bytes, token);
        if (await _source.ReadAsync(new byte[1], token) != 0) throw Invalid();
        await PrepareEntries(bytes, token);
    }
    // Application-owned HTTP bytes, transferred without a second archive-sized copy.
    internal Task PrepareOwnedAsync(byte[] bytes, CancellationToken token, NativeRuntimeProfile profile = NativeRuntimeProfile.Legacy)
    {
        bool pinnedLargeArchive = profile switch
        {
            NativeRuntimeProfile.LocalCompilerCoursework => NativeCompilerCourseworkPins.Ready
                && bytes.Length == NativeCompilerCourseworkPins.ArchiveLength,
            NativeRuntimeProfile.LocalCompilerDviGraphics => NativeCompilerDviGraphicsPins.Ready
                && bytes.Length == NativeCompilerDviGraphicsPins.ArchiveLength,
            NativeRuntimeProfile.LocalCompilerPdfOrigins => NativeCompilerPdfOriginPins.Ready
                && bytes.Length == NativeCompilerPdfOriginPins.ArchiveLength,
            NativeRuntimeProfile.LocalCompilerPageLabels => NativeCompilerPageLabelPins.Ready
                && bytes.Length == NativeCompilerPageLabelPins.ArchiveLength,
            _ => false
        };
        if (_preparation is not null || _closing || bytes.Length < 22 || bytes.Length > MaxZipBytes && !pinnedLargeArchive) throw Invalid();
        _preparation = PrepareEntries(bytes, token, pinnedLargeArchive ? profile : NativeRuntimeProfile.Legacy);
        return _preparation;
    }
    private async Task PrepareEntries(byte[] bytes, CancellationToken token, NativeRuntimeProfile largeArchiveProfile = NativeRuntimeProfile.Legacy)
    {
        token.ThrowIfCancellationRequested();
        if (_closing) throw Invalid();
        var directory = ZipDirectory.Read(bytes, largeArchiveProfile); // sole envelope parser; bounded metadata first
        _bytes = bytes;
        var entries = directory.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var manifest = entries["pins.json"];
        PinsStreamsOpened++;
        Pins = await ReadEntry(manifest, token);
        var pins = LocalRuntimeSelection.ParsePins(Pins); // exact trusted legacy or ready suite closure
        foreach (var pin in pins)
            if (!entries.TryGetValue("files/" + pin.Name, out var item) || item.Length != pin.Length) throw Invalid();
        // Exact count + unique schema names + all pins present excludes every extra.
        if (entries.Count != pins.Length + 1 + (entries.ContainsKey("files/") ? 1 : 0)) throw Invalid();
        token.ThrowIfCancellationRequested();
        Files = pins.Select(pin => new LocalRuntimeFile(pin.Name, pin.Length, ct =>
        {
            if (_closing || _reads.Count >= pins.Length) throw Invalid();
            var item = entries["files/" + pin.Name];
            var task = ReadResource(item, ct); _reads.Add(task); return task;
        })).ToArray();
    }
    private async Task<byte[]> ReadResource(ZipDirectory.Item item, CancellationToken token)
    {
        // Existing verifier is sequential. Explicitly refuse accidental concurrent entry use.
        if (Interlocked.Exchange(ref _reading, 1) != 0) throw Invalid();
        try
        {
            await Task.Yield(); token.ThrowIfCancellationRequested();
            ResourceStreamsOpened++; return await ReadEntry(item, token);
        }
        finally { Volatile.Write(ref _reading, 0); }
    }
    private async Task<byte[]> ReadEntry(ZipDirectory.Item item, CancellationToken token)
    {
        // The sole envelope parser validated this compressed span and method. Do NOT
        // use ZipArchiveEntry.Open: it can clamp decoded bytes to declared Length,
        // hiding extra output even when the original prefix CRC was retained.
        await using var segment = new MemoryStream(_bytes, item.DataOffset, checked((int)item.Compressed), false, false);
        if (item.Method == 8)
        {
            await using var inflater = new DeflateStream(segment, CompressionMode.Decompress, true);
            return await ReadOutput(inflater, item, token);
        }
        return await ReadOutput(segment, item, token);
    }
    private static async Task<byte[]> ReadOutput(Stream stream, ZipDirectory.Item item, CancellationToken token)
    {
        // Only the preflighted trusted output length controls allocation. Deflate
        // itself is NOT length-clamped: the extra-byte probe observes real output.
        var data = new byte[checked((int)item.Length)];
        await stream.ReadExactlyAsync(data, token);
        if (await stream.ReadAsync(new byte[1], token) != 0 || Crc(data) != item.Crc) throw Invalid();
        return data; // NativeRuntimeSetup performs canonical SHA/Package.Create verification.
    }
    private static readonly uint[] CrcTable = MakeCrcTable();
    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (int i = 0; i < table.Length; i++)
        {
            uint value = (uint)i;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0);
            table[i] = value;
        }
        return table;
    }
    private static uint Crc(byte[] bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) crc = (crc >> 8) ^ CrcTable[(int)((crc ^ value) & 255)];
        return ~crc;
    }
    public async ValueTask DisposeAsync()
    {
        _closing = true;
        try
        {
            if (_preparation is not null)
                try { await _preparation; } catch (Exception e) when (e is not OutOfMemoryException) { }
            // Observe even already-faulted reads. Do not dispose an active entry/source.
            try { await Task.WhenAll(_reads); } catch (Exception e) when (e is not OutOfMemoryException) { }
        }
        finally
        {
            try { _bytes = []; Pins = []; Files = []; _reads.Clear(); _preparation = null; }
            finally { if (_source is { } source) { _source = null; await source.DisposeAsync(); } }
        }
    }
    private static InvalidDataException Invalid() => new("Invalid supported compiler resource ZIP.");
}

/// <summary>Strict single-disk non-ZIP64 central/local directory envelope.
/// ZIP64, encryption, links, alternate names and unknown compression are not supported.
/// CRC is archive corruption detection only; resource SHA authority stays unchanged.
/// </summary>
internal static class ZipDirectory
{
    internal sealed record Item(string Name, uint Length, uint Compressed, uint Crc, int DataOffset, int Method);
    private static InvalidDataException Invalid() => new("Invalid supported compiler resource ZIP.");
    private static ushort U16(byte[] b, int p) { Check(b, p, 2); return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2)); }
    private static uint U32(byte[] b, int p) { Check(b, p, 4); return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4)); }
    private static void Check(byte[] b, int p, int n) { if (p < 0 || n < 0 || p > b.Length - n) throw Invalid(); }
    public static List<Item> Read(byte[] b, NativeRuntimeProfile largeArchiveProfile = NativeRuntimeProfile.Legacy)
    {
        int end = -1;
        for (int p = b.Length - 22; p >= Math.Max(0, b.Length - 65557); p--)
            if (U32(b, p) == 0x06054b50 && p + 22 + U16(b, p + 20) == b.Length) { end = p; break; }
        if (end < 0 || U16(b, end + 4) != 0 || U16(b, end + 6) != 0) throw Invalid();
        int count = U16(b, end + 10);
        bool legacyEnvelope = count is 498 or 499 or 500 or 501 or 508 or 509;
        bool suiteEnvelope = LocalRuntimeSelection.SupportedSuiteCount(count - 1) || LocalRuntimeSelection.SupportedSuiteCount(count - 2);
        if ((!legacyEnvelope && !suiteEnvelope) || U16(b, end + 8) != count) throw Invalid();
        bool largeArchive = largeArchiveProfile is NativeRuntimeProfile.LocalCompilerCoursework or NativeRuntimeProfile.LocalCompilerDviGraphics or NativeRuntimeProfile.LocalCompilerPdfOrigins or NativeRuntimeProfile.LocalCompilerPageLabels;
        if (largeArchive)
        {
            int installed = NativeRuntimeProfiles.InstalledCount(largeArchiveProfile);
            if (count != installed + 1 && count != installed + 2) throw Invalid();
        }
        if (!suiteEnvelope && b.Length > 67108864) throw Invalid();
        long resourceTotalCap = largeArchive ? NativeRuntimeProfiles.SuiteTotalBytes(largeArchiveProfile)
            : suiteEnvelope ? NativeCompilerSuiteContract.TotalBytes : LocalRuntimeSelection.Limits.MaxTotalBytes;
        long central = U32(b, end + 16), centralSize = U32(b, end + 12);
        if (central + centralSize != end || central > int.MaxValue) throw Invalid();
        int cursor = (int)central; long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<Item>(count); var spans = new List<(long Start, long End)>(count);
        for (int i = 0; i < count; i++)
        {
            Check(b, cursor, 46);
            if (U32(b, cursor) != 0x02014b50 || U16(b, cursor + 34) != 0 || U16(b, cursor + 6) > 20) throw Invalid();
            int flags = U16(b, cursor + 8), method = U16(b, cursor + 10);
            if ((flags & ~0x080e) != 0 || method is not (0 or 8) || method == 0 && (flags & 6) != 0) throw Invalid();
            uint crc = U32(b, cursor + 16), compressed = U32(b, cursor + 20), length = U32(b, cursor + 24);
            int nameLength = U16(b, cursor + 28), extra = U16(b, cursor + 30), comment = U16(b, cursor + 32);
            if (nameLength is < 1 or > 206) throw Invalid();
            Check(b, cursor + 46, nameLength + extra + comment);
            var nameBytes = b.AsSpan(cursor + 46, nameLength);
            foreach (byte value in nameBytes) if (value < 32 || value > 126) throw Invalid();
            string name = Encoding.ASCII.GetString(nameBytes);
            bool folder = name == "files/";
            if (!names.Add(name) || !(name == "pins.json" || folder || ResourceName(name, suiteEnvelope, largeArchiveProfile))) throw Invalid();
            uint attributes = U32(b, cursor + 38), type = (attributes >> 16) & 0xf000;
            if (type != 0 && type != (folder ? 0x4000u : 0x8000u)
                || (attributes & 0x48) != 0 || !folder && (attributes & 0x10) != 0) throw Invalid();
            long cap = name == "pins.json" ? (suiteEnvelope ? LocalRuntimeSelection.SuiteMaxPinsBytes : LocalRuntimeSelection.MaxPinsBytes)
                : suiteEnvelope ? NativeCompilerSuiteContract.FileBytes : LocalRuntimeSelection.Limits.MaxFileBytes;
            if (length > cap || compressed > ZipRuntimeArchive.MaxZipBytes || method == 0 && compressed != length
                || folder && (length != 0 || compressed != 0)) throw Invalid();
            if (name != "pins.json" && !folder)
            { total += length; if (total > resourceTotalCap) throw Invalid(); }
            // Reject ZIP64 and ambiguous extra fields rather than interpret a second length/name.
            if (extra != 0) ValidateExtra(b, cursor + 46 + nameLength, extra);
            long local = U32(b, cursor + 42);
            if (local > int.MaxValue || local + 30 > central) throw Invalid();
            int lp = (int)local;
            if (U32(b, lp) != 0x04034b50 || U16(b, lp + 6) != flags || U16(b, lp + 8) != method
                || U16(b, lp + 4) > 20 || U16(b, lp + 26) != nameLength) throw Invalid();
            int le = U16(b, lp + 28); Check(b, lp + 30, nameLength + le);
            if (!b.AsSpan(lp + 30, nameLength).SequenceEqual(nameBytes)) throw Invalid();
            ValidateExtra(b, lp + 30 + nameLength, le);
            if ((flags & 8) == 0)
            {
                if (U32(b, lp + 14) != crc || U32(b, lp + 18) != compressed || U32(b, lp + 22) != length) throw Invalid();
            }
            else if ((U32(b, lp + 14) != 0 && U32(b, lp + 14) != crc)
                || (U32(b, lp + 18) != 0 && U32(b, lp + 18) != compressed)
                || (U32(b, lp + 22) != 0 && U32(b, lp + 22) != length)) throw Invalid();
            long dataOffset = local + 30 + nameLength + le;
            long payloadEnd = dataOffset + compressed;
            if (payloadEnd > central) throw Invalid();
            if ((flags & 8) != 0)
            {
                int dp = checked((int)payloadEnd);
                if (U32(b, dp) == 0x08074b50) dp += 4;
                if (U32(b, dp) != crc || U32(b, dp + 4) != compressed || U32(b, dp + 8) != length) throw Invalid();
                payloadEnd = dp + 12L;
                if (payloadEnd > central) throw Invalid();
            }
            spans.Add((local, payloadEnd)); items.Add(new(name, length, compressed, crc, checked((int)dataOffset), method));
            cursor += 46 + nameLength + extra + comment;
            if (cursor > end) throw Invalid();
        }
        if (cursor != end || !names.Contains("pins.json")
            || !LocalRuntimeSelection.SupportedCount(items.Count(x => x.Name.StartsWith("files/", StringComparison.Ordinal) && x.Name != "files/"))) throw Invalid();
        long next = 0;
        foreach (var span in spans.OrderBy(x => x.Start)) { if (span.Start != next) throw Invalid(); next = span.End; }
        if (next != central) throw Invalid();
        return items;
    }
    private static bool ResourceName(string name, bool suite, NativeRuntimeProfile largeArchiveProfile)
    {
        if (!name.StartsWith("files/", StringComparison.Ordinal)) return false;
        var flat = name[6..];
        return suite ? NativeCompilerSuiteContract.ResourceName(flat) || NativeThesisCompilerContract.NoticeName(flat) || NativeInputBudgetContract.NoticeName(flat)
            || largeArchiveProfile is NativeRuntimeProfile.LocalCompilerCoursework or NativeRuntimeProfile.LocalCompilerDviGraphics or NativeRuntimeProfile.LocalCompilerPdfOrigins or NativeRuntimeProfile.LocalCompilerPageLabels
                && NativeCompilerCourseworkPins.NoticeName(flat)
            || largeArchiveProfile is (NativeRuntimeProfile.LocalCompilerDviGraphics or NativeRuntimeProfile.LocalCompilerPdfOrigins or NativeRuntimeProfile.LocalCompilerPageLabels) && NativeCompilerDviGraphicsPins.NoticeName(flat)
            || largeArchiveProfile == NativeRuntimeProfile.LocalCompilerPageLabels && NativeCompilerPageLabelPins.NoticeName(flat)
            : flat.Length is > 0 and <= 200 && flat.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            && flat is not ("." or "..");
    }
    private static void ValidateExtra(byte[] b, int at, int length)
    {
        int end = at + length;
        while (at < end)
        {
            if (end - at < 4) throw Invalid();
            int id = U16(b, at), size = U16(b, at + 2); at += 4;
            // Only inert extended timestamp/uid-gid metadata; no Unix link or alternate name records.
            if (id is not (0x5455 or 0x7875) || size > end - at) throw Invalid();
            at += size;
        }
    }
}
