using System.Buffers.Binary;
using System.Text;
using Client.Compilation.Native;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests.CompilationNative;

public sealed class NativeDviImageSidecarTests
{
    [Fact]
    public async Task MetricSidecarFromCapturedImageMatchesExtractbbAndLeavesTheSourceUntouched()
    {
        await using var workspace = await CompilationTestWorkspace.Open();
        const string name = "рисунки/знімок екрана.PNG";
        byte[] png = Png(937, 345, (3150, 3150, 1));
        var originalHash = CompilationTestWorkspace.Hash(png);
        Assert.True(workspace.Workspace.CreateFileFromBytes(name, png), workspace.Workspace.LastError);
        var captured = await workspace.Snapshot();
        var source = Assert.Single(captured.Files, f => f.RelativePath == name);

        // Changing the working copy after capture cannot change this job's box.
        Assert.True(workspace.Workspace.ReplaceFileBytes(source.FileId, Png(10, 20)));
        var (lease, bytes) = Assert.Single(NativeDviImageSidecarContract.Create(captured.Files, ["entry.dvi"], default));
        Assert.Equal(new NativeDviImageSidecar("рисунки/знімок екрана.xbb", name, originalHash.Hex), lease);
        Assert.Equal("%%BoundingBox: 0 0 843 310\n%%HiResBoundingBox: 0.000000 0.000000 843.194601 310.461192\n",
            Encoding.ASCII.GetString(bytes));
        Assert.Equal(png, source.ToArray());
        Assert.DoesNotContain(captured.Files, f => f.RelativePath == lease.Name);
        Assert.DoesNotContain(workspace.Workspace.Files, f => f.RelativePath == lease.Name);
    }

    [Fact]
    public async Task AuthoredAmbiguousUnsafeAndMalformedImagesDoNotAcquireSidecars()
    {
        await using var workspace = await CompilationTestWorkspace.Open();
        var unknownDensity = Png(10, 20, (1000, 2000, 0));
        var badHeader = Png(10, 20);
        badHeader[16] ^= 1;
        var malformedDensity = Png(10, 20, (3150, 3150, 1));
        malformedDensity[43] ^= 1;
        var provided = "%%BoundingBox: 0 0 51 61\n"u8.ToArray();
        foreach (var (path, bytes) in new (string, byte[])[]
        {
            ("ok.png", unknownDensity), ("unitless.png", Png(10, 20)),
            ("manual.png", unknownDensity), ("manual.xbb", provided),
            ("same.png", unknownDensity), ("same.pdf", [42]),
            ("occupied.png", unknownDensity), ("occupied.xbb/child.tex", [42]),
            ("collision.png", unknownDensity), ("badheader.png", badHeader),
            ("baddensity.png", malformedDensity), ("bignatural.png", Png(16384, 1)),
            ("suffix.png", [42]), ("png", [42]), ("other.jpg", unknownDensity),
        }) Assert.True(workspace.Workspace.CreateFileFromBytes(path, bytes), workspace.Workspace.LastError);

        var snapshot = await workspace.Snapshot();
        var prepared = NativeDviImageSidecarContract.Create(snapshot.Files, ["collision.xbb"], default);
        Assert.Equal(new[] { "ok.xbb", "unitless.xbb" }, prepared.Select(result => result.Lease.Name).Order(StringComparer.Ordinal));
        Assert.All(prepared, result => Assert.Equal("%%BoundingBox: 0 0 10 20\n%%HiResBoundingBox: 0.000000 0.000000 10.000000 20.000000\n",
            Encoding.ASCII.GetString(result.Bytes)));
        Assert.Equal(provided, snapshot.Files.Single(f => f.RelativePath == "manual.xbb").ToArray());
        Assert.Throws<OperationCanceledException>(() => NativeDviImageSidecarContract.Create(snapshot.Files, [], new CancellationToken(true)));
    }

    private static byte[] Png(uint width, uint height, (uint X, uint Y, byte Unit)? density = null)
    {
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        byte[] physical = new byte[9];
        if (density is { } p)
        {
            BinaryPrimitives.WriteUInt32BigEndian(physical, p.X);
            BinaryPrimitives.WriteUInt32BigEndian(physical.AsSpan(4), p.Y);
            physical[8] = p.Unit;
        }
        // This test supplies geometry headers only. Full decode is exercised by
        // the DVI converter on the independent native graphics control.
        return [137, 80, 78, 71, 13, 10, 26, 10, .. Chunk("IHDR", header),
            .. (density is null ? Array.Empty<byte>() : Chunk("pHYs", physical)), .. Chunk("IDAT", [0]), .. Chunk("IEND", [])];
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] body = [.. Encoding.ASCII.GetBytes(type), .. data];
        var result = new byte[body.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)data.Length);
        body.CopyTo(result, 4);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), Crc32.Compute(body));
        return result;
    }
}
