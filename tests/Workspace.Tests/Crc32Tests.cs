using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests;

/// <summary>
/// CRC-32 (IEEE 802.3) known-answer vectors. The import path computes CRC-32
/// over extracted entry bytes and compares against ZipArchiveEntry.Crc32; .NET 10
/// does not auto-validate on read, so a tested implementation is mandatory.
/// </summary>
public class Crc32Tests
{
    [Theory]
    [InlineData("", 0u)]
    [InlineData("a", 0xe8b7be43u)]
    [InlineData("abc", 0x352441c2u)]
    [InlineData("message digest", 0x20159d7fu)]
    [InlineData("123456789", 0xcbf43926u)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0x414fa339u)]
    public void Compute_MatchesKnownVectors(string input, uint expected)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        Assert.Equal(expected, Crc32.Compute(bytes));
    }

    [Fact]
    public void Compute_StreamMatchesByteArray()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("Hello, Привіт 🇺🇦!");
        using var ms = new MemoryStream(bytes);
        Assert.Equal(Crc32.Compute(bytes), Crc32.Compute(ms));
    }
}
