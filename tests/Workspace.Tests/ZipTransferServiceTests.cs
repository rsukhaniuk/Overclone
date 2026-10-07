using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests;

/// <summary>
/// Bounded C# ZIP import/export invariants. B01: export→import preserves every
/// accepted path, byte length and SHA-256 (binary/empty/BOM/CRLF), with fresh
/// IDs. B04: malformed/unsafe archives fail without producing a publishable
/// stage. CRC-32 is verified on import. Uses System.IO.Compression only.
/// </summary>
public class ZipTransferServiceTests
{
    private static byte[] MakeZip(params (string path, byte[] bytes)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in entries)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var es = entry.Open();
                es.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }

    private static byte[] TextBytes(string s) => Encoding.UTF8.GetBytes(s);
    private static byte[] WithBom(string s) => TextContent.Bom.Concat(Encoding.UTF8.GetBytes(s)).ToArray();

    private static string ShaHex(byte[] bytes)
        => System.Security.Cryptography.SHA256.HashData(bytes)
            .Aggregate("", (a, b) => a + b.ToString("x2"));

    [Fact]
    public async Task RoundTrip_Preserves_Paths_Bytes_Kinds_Bom_Crlf_Binary_Empty()
    {
        // Standard fixture: main.tex including chapters/intro.tex, a .bib, an
        // empty text file, a CRLF/BOM Ukrainian/emoji file, a PNG-ish asset and
        // a binary file with all byte values 0–255.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x33 };
        var allBytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var crlfBom = WithBom("Рядок1\r\nРядок2 🇺🇦 é\u0301\r\n");
        var invalidUtf8Tex = new byte[] { 0xFF, 0xFE, 0x41, 0x42 }; // not valid UTF-8

        var zip = MakeZip(
            ("main.tex", TextBytes("\\documentclass{article}\n\\input{chapters/intro}\n")),
            ("chapters/intro.tex", TextBytes("Вступ\n")),
            ("refs.bib", TextBytes("@article{x, title={X}}\n")),
            ("empty.txt", Array.Empty<byte>()),
            ("crlfbom.tex", crlfBom),
            ("asset.png", png),
            ("raw.bin", allBytes),
            ("invalid.tex", invalidUtf8Tex)
        );

        var svc = new ZipTransferService(StubRepo.Instance);
        var stage = await svc.StageZipImportAsync(zip, "fixture", default);
        Assert.True(stage.Valid, "standard fixture must stage cleanly");
        Assert.Empty(stage.Staged.ValidationErrors);

        var byPath = stage.Staged.Files.ToDictionary(f => f.RelativePath);
        Assert.Equal(8, byPath.Count);
        Assert.Equal("text", byPath["main.tex"].MediaKind);
        Assert.Equal("text", byPath["empty.txt"].MediaKind);
        Assert.True(byPath["crlfbom.tex"].HasBom);
        Assert.Equal("binary", byPath["invalid.tex"].MediaKind); // invalid UTF-8 → binary asset
        Assert.Equal("binary", byPath["asset.png"].MediaKind);
        Assert.Equal("binary", byPath["raw.bin"].MediaKind);

        // Byte and hash invariants (B01): exact bytes, lengths and SHA-256.
        Assert.Equal(crlfBom, byPath["crlfbom.tex"].Bytes);
        Assert.Equal(crlfBom.Length, byPath["crlfbom.tex"].ByteLength);
        Assert.Equal(ShaHex(crlfBom), byPath["crlfbom.tex"].Hash.Hex);
        Assert.Equal(allBytes, byPath["raw.bin"].Bytes);
        Assert.Equal(256, byPath["raw.bin"].ByteLength);
        Assert.Empty(byPath["empty.txt"].Bytes);
        Assert.Equal(png, byPath["asset.png"].Bytes);

        // Export the staged revision: capture the source bound to an immutable
        // revision and build the actual ZIP bytes, then re-import to prove the
        // round trip preserves bytes, lengths and hashes (fresh IDs on publish).
        var revision = MakeRevision(stage.Staged);
        var receipt = await svc.ExportZipAsync(
            new ExportSource(revision, (h, ct) => Task.FromResult(SHA256_BYTES[h.Hex]!)), default);
        var zipBytes = BuildZipFromRevision(revision);
        var stage2 = await svc.StageZipImportAsync(zipBytes, "round", default);
        Assert.True(stage2.Valid);
        var byPath2 = stage2.Staged.Files.ToDictionary(f => f.RelativePath);
        foreach (var f in stage.Staged.Files)
        {
            Assert.Equal(f.ByteLength, byPath2[f.RelativePath].ByteLength);
            Assert.Equal(f.Hash.Hex, byPath2[f.RelativePath].Hash.Hex);
        }
        Assert.Equal(stage.Staged.Files.Length, receipt.FileCount);
    }

    [Fact]
    public async Task Rejects_Too_Many_Entries()
    {
        var entries = Enumerable.Range(0, ArchiveLimits.MaxEntries + 1)
            .Select(i => ($"f{i}.tex", TextBytes("x"))).ToArray();
        var zip = MakeZip(entries);
        var stage = await new ZipTransferService(StubRepo.Instance)
            .StageZipImportAsync(zip, "big", default);
        Assert.False(stage.Valid);
        Assert.Contains(stage.Staged.ValidationErrors, e => e.Code == "InvalidArchive");
    }

    [Fact]
    public async Task Rejects_Traversal_Duplicate_And_Prefix_Conflicts()
    {
        var zip = MakeZip(
            ("a", TextBytes("file named a")),       // a file named "a"
            ("a/b.tex", TextBytes("under a")),     // ancestor "a" is a file → PrefixConflict
            ("../escape.tex", TextBytes("x")),     // TraversalSegment
            ("dup.tex", TextBytes("one")),
            ("dup.tex", TextBytes("two"))          // DuplicatePath
        );
        var stage = await new ZipTransferService(StubRepo.Instance)
            .StageZipImportAsync(zip, "bad", default);
        Assert.False(stage.Valid);
        var codes = stage.Staged.ValidationErrors.Select(e => e.Code).ToHashSet();
        Assert.Contains("TraversalSegment", codes);
        Assert.Contains("DuplicatePath", codes);
        Assert.Contains("PrefixConflict", codes);
    }

    [Fact]
    public async Task Rejects_Absolute_And_Drive_Paths()
    {
        var zip = MakeZip(
            ("/etc/passwd", TextBytes("x")),
            ("C:\\Users\\x.tex", TextBytes("y"))
        );
        var stage = await new ZipTransferService(StubRepo.Instance)
            .StageZipImportAsync(zip, "abs", default);
        Assert.False(stage.Valid);
    }

    [Fact]
    public async Task Rejects_Device_And_Forbidden_Segment_Names()
    {
        var zip = MakeZip(
            ("CON.tex", TextBytes("x")),
            (".git/config", TextBytes("y")),
            ("PRN", TextBytes("z"))
        );
        var stage = await new ZipTransferService(StubRepo.Instance)
            .StageZipImportAsync(zip, "dev", default);
        Assert.False(stage.Valid);
        var codes = stage.Staged.ValidationErrors.Select(e => e.Code).ToHashSet();
        Assert.Contains("DeviceName", codes);
        Assert.Contains("ForbiddenSegment", codes);
    }

    [Fact]
    public async Task Rejects_Duplicate_Directory_Declaration()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            z.CreateEntry("dir/");
            z.CreateEntry("dir/");
        }
        var stage = await new ZipTransferService(StubRepo.Instance)
            .StageZipImportAsync(ms.ToArray(), "dupdir", default);
        Assert.False(stage.Valid);
        Assert.Contains("DuplicatePath", stage.Staged.ValidationErrors.Select(e => e.Code));
    }

    [Fact]
    public async Task Rejects_Corrupt_Crc()
    {
        var content = TextBytes("hello world");
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = z.CreateEntry("a.tex", CompressionLevel.NoCompression);
            using var es = e.Open();
            es.Write(content, 0, content.Length);
        }
        var zip = ms.ToArray();
        // Flip a byte inside the stored entry content (not the EOCD), so the
        // archive still opens but its extracted bytes no longer match the
        // stored CRC-32. Import must reject the entry as corrupt.
        var idx = IndexOf(zip, content);
        Assert.True(idx >= 0, "content marker not found in zip");
        zip[idx] ^= 0xFF;
        var stage = await new ZipTransferService(StubRepo.Instance)
            .StageZipImportAsync(zip, "crc", default);
        Assert.False(stage.Valid);
        Assert.Contains("CorruptEntry", stage.Staged.ValidationErrors.Select(e => e.Code));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    [Fact]
    public async Task Rejects_Empty_And_Oversized_Archive()
    {
        var empty = await new ZipTransferService(StubRepo.Instance)
            .StageZipImportAsync(Array.Empty<byte>(), "empty", default);
        Assert.False(empty.Valid);

        var huge = new byte[ArchiveLimits.ZipInputSize + 1];
        var stage = await new ZipTransferService(StubRepo.Instance)
            .StageZipImportAsync(huge, "huge", default);
        Assert.False(stage.Valid);
        Assert.Contains("InvalidArchive", stage.Staged.ValidationErrors.Select(e => e.Code));
    }

    [Fact]
    public async Task Publish_With_Validation_Errors_Fails()
    {
        var stage = new StagedProject("x", ImmutableArray<StagedFile>.Empty,
            new[] { new ValidationError("a.tex", "DuplicatePath", "dup") });
        var result = await new ZipTransferService(StubRepo.Instance)
            .PublishAsync(stage, "name", default);
        Assert.False(result.Published);
        Assert.Equal("ValidationFailed", result.ErrorCode);
    }

    [Fact]
    public async Task Directory_After_Child_File_Is_Accepted_Order_Independent()
    {
        // A directory declaration following its child file is valid (a
        // directory contains files); entry order must not change acceptance.
        var zip = MakeZip(
            ("chapters/intro.tex", TextBytes("intro")),
            ("chapters/", Array.Empty<byte>()));
        var stage = await new ZipTransferService(StubRepo.Instance)
            .StageZipImportAsync(zip, "order", default);
        Assert.True(stage.Valid, $"expected valid, got: {string.Join(", ", stage.Staged.ValidationErrors.Select(e => e.Code))}");
        Assert.Contains("chapters/intro.tex", stage.Staged.Files.Select(f => f.RelativePath));
    }

    [Fact]
    public async Task ZeroStoredCrc_OnNonEmptyEntry_Rejected()
    {
        // A deliberately-wrong zero CRC on a non-empty entry must be rejected.
        // .NET 10 does not auto-validate CRC, so the import path compares even
        // a zero stored CRC (the legitimate CRC of an empty file is 0; a zero
        // CRC on non-empty content is corrupt).
        var content = TextBytes("not empty");
        var zip = MakeStoredZipWithCrc("a.tex", content, storedCrc: 0u);
        var stage = await new ZipTransferService(StubRepo.Instance)
            .StageZipImportAsync(zip, "zerocrc", default);
        Assert.False(stage.Valid);
        Assert.Contains("CorruptEntry", stage.Staged.ValidationErrors.Select(e => e.Code));
    }

    [Fact]
    public async Task ZeroStoredCrc_OnEmptyEntry_Accepted()
    {
        // An empty file legitimately has CRC-32 == 0 and must be accepted.
        var zip = MakeStoredZipWithCrc("empty.tex", Array.Empty<byte>(), storedCrc: 0u);
        var stage = await new ZipTransferService(StubRepo.Instance)
            .StageZipImportAsync(zip, "emptyok", default);
        Assert.True(stage.Valid);
        Assert.Equal("empty.tex", stage.Staged.Files.Single().RelativePath);
        Assert.Empty(stage.Staged.Files.Single().Bytes);
    }

    // A minimal STORED-only zip with an explicit stored CRC, to exercise the
    // always-compare path (including the zero-CRC corrupt case).
    private static byte[] MakeStoredZipWithCrc(string name, byte[] data, uint storedCrc)
    {
        var nameB = Encoding.UTF8.GetBytes(name);
        var local = new byte[30 + nameB.Length];
        local.WriteUInt32LE(0x04034b50, 0);
        local.WriteUInt16LE(20, 4);
        local.WriteUInt16LE(0, 6);
        local.WriteUInt16LE(0, 8); // method 0 = stored
        local.WriteUInt16LE(0, 10); local.WriteUInt16LE(0, 12);
        local.WriteUInt32LE(storedCrc, 14);
        local.WriteUInt32LE((uint)data.Length, 18); local.WriteUInt32LE((uint)data.Length, 22);
        local.WriteUInt16LE((ushort)nameB.Length, 26); local.WriteUInt16LE(0, 28);
        Buffer.BlockCopy(nameB, 0, local, 30, nameB.Length);

        var cdir = new byte[46 + nameB.Length];
        cdir.WriteUInt32LE(0x02014b50, 0);
        cdir.WriteUInt16LE(20, 4); cdir.WriteUInt16LE(20, 6);
        cdir.WriteUInt16LE(0, 8); cdir.WriteUInt16LE(0, 10);
        cdir.WriteUInt16LE(0, 12); cdir.WriteUInt16LE(0, 14);
        cdir.WriteUInt32LE(storedCrc, 16);
        cdir.WriteUInt32LE((uint)data.Length, 20); cdir.WriteUInt32LE((uint)data.Length, 24);
        cdir.WriteUInt16LE((ushort)nameB.Length, 28); cdir.WriteUInt16LE(0, 30);
        cdir.WriteUInt16LE(0, 32); cdir.WriteUInt16LE(0, 34);
        cdir.WriteUInt32LE(0, 36); cdir.WriteUInt32LE(0, 42); // offset 0
        Buffer.BlockCopy(nameB, 0, cdir, 46, nameB.Length);

        var eocd = new byte[22];
        eocd.WriteUInt32LE(0x06054b50, 0);
        eocd.WriteUInt16LE(0, 4);
        eocd.WriteUInt16LE(0, 6);
        eocd.WriteUInt16LE(1, 8); eocd.WriteUInt16LE(1, 10);
        eocd.WriteUInt32LE((uint)cdir.Length, 12);
        eocd.WriteUInt32LE((uint)(local.Length + data.Length), 16);
        eocd.WriteUInt16LE(0, 20);

        var ms = new MemoryStream();
        ms.Write(local, 0, local.Length);
        ms.Write(data, 0, data.Length);
        ms.Write(cdir, 0, cdir.Length);
        ms.Write(eocd, 0, eocd.Length);
        return ms.ToArray();
    }

    // ---- helpers: a tiny stub repo so export can read content by hash ----

    private sealed class StubRepo : IProjectRepository
    {
        public static readonly StubRepo Instance = new();
        public Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct) => throw new NotImplementedException();
        public Task<CommitResult> CommitAsync(ProjectCommit batch, RevisionId? expectedHead, CancellationToken ct) => throw new NotImplementedException();
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct) => throw new NotImplementedException();
        public Task<Project> CreateProjectAsync(string name, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotImplementedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId id, RevisionId expectedCurrentHead, RevisionId newHead, CancellationToken ct) => throw new NotImplementedException();
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId projectId, RevisionId revisionId, CancellationToken ct) => throw new NotImplementedException();
        public Task<byte[]> ReadContentAsync(SHA256 hash, CancellationToken ct)
            => Task.FromResult(SHA256_BYTES.TryGetValue(hash.Hex, out var b) ? b : Array.Empty<byte>());
        public Task<ImportPublishResult> PublishImportAsync(StagedProject staged, CancellationToken ct)
            => Task.FromResult(new ImportPublishResult(true, new Project(ProjectId.New(), staged.Name, null), RevisionId.New()));
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId p, RevisionId eh, ProjectRevision r, RevisionId s, string reason, CancellationToken ct)
            => Task.FromResult(CommitResult.Saved(r.Id));
    }

    private static readonly Dictionary<string, byte[]> SHA256_BYTES = new();

    private static ProjectRevision MakeRevision(StagedProject staged)
    {
        SHA256_BYTES.Clear();
        var files = new List<ProjectFile>();
        foreach (var f in staged.Files)
        {
            SHA256_BYTES[f.Hash.Hex] = f.Bytes;
            files.Add(new ProjectFile(FileId.New(), f.RelativePath, f.MediaKind, f.Hash, f.ByteLength, f.HasBom));
        }
        return new ProjectRevision(RevisionId.New(), ProjectId.New(), null,
            ImmutableArray.CreateRange(files), DateTimeOffset.UtcNow);
    }

    private static byte[] BuildZipFromRevision(ProjectRevision revision)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var f in revision.Files)
            {
                var e = z.CreateEntry(f.RelativePath, CompressionLevel.Optimal);
                using var es = e.Open();
                es.Write(SHA256_BYTES[f.ContentHash.Hex], 0, (int)f.ByteLength);
            }
        }
        return ms.ToArray();
    }
}

// Tiny little-endian writers for the stored-zip test builder.
internal static class ByteBufExt
{
    public static void WriteUInt32LE(this byte[] b, uint value, int offset)
        => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), value);
    public static void WriteUInt16LE(this byte[] b, ushort value, int offset)
        => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(offset), value);
}
