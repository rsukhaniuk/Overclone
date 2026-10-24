using System.IO.Compression;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Workspace.Tests.CompilationNative;
using Workspace.Tests.CompilerZip;
using Xunit;

namespace Workspace.Tests.SyncTeXResources;

public sealed class ZipProfileTests
{
    // Zero payloads intentionally establish envelope/pins preflight only, never
    // runtime verification. Actual-byte install is tested separately below.
    private static byte[] Envelope(NativeResourcePin[] pins, bool directory = false, string? rename = null, int extra = 0)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var manifest = zip.CreateEntry("pins.json", CompressionLevel.Optimal); manifest.ExternalAttributes = 0;
            using (var output = manifest.Open()) output.Write(SyncTeXResourceFixture.Json(pins));
            var buffer = new byte[8192];
            foreach (var pin in pins)
            {
                var entry = zip.CreateEntry("files/" + (pin.Name == "synctex-query.js" && rename is not null ? rename : pin.Name), CompressionLevel.Optimal);
                entry.ExternalAttributes = 0;
                using var output = entry.Open();
                int remaining = pin.Length;
                while (remaining > 0) { int take = Math.Min(remaining, buffer.Length); output.Write(buffer, 0, take); remaining -= take; }
            }
            if (directory) { var folder = zip.CreateEntry("files/"); folder.ExternalAttributes = 0x10; }
            for (int i = 0; i < extra; i++) { var entry = zip.CreateEntry($"files/extra{i}"); entry.ExternalAttributes = 0; }
        }
        return memory.ToArray();
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Only_exact497_or499_envelopes_with_optional_directory_pass_preflight(bool sync, bool directory)
    {
        var pins = sync ? SyncTeXResourceFixture.Pins() : SyncTeXResourceFixture.OldPins();
        var bytes = Envelope(pins, directory);
        await using var archive = new ZipRuntimeArchive();
        await archive.PrepareAsync(bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), default);
        Assert.Equal(pins.Length, archive.Files.Count);
        Assert.Equal(1, archive.PinsStreamsOpened); Assert.Equal(0, archive.ResourceStreamsOpened);
        Assert.Equal(pins, LocalRuntimeSelection.ParsePins(archive.Pins));
    }

    [Theory]
    [InlineData("../outside")] [InlineData("engine.js")] [InlineData("Synctex-query.wasm")]
    public async Task New_profile_traversal_duplicates_and_case_aliases_reject_before_any_entry_stream(string name)
    {
        var bytes = Envelope(SyncTeXResourceFixture.Pins(), rename: name);
        await using var archive = new ZipRuntimeArchive();
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.PrepareAsync(bytes.Length,
            _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), default));
        Assert.Equal(0, archive.PinsStreamsOpened); Assert.Equal(0, archive.ResourceStreamsOpened);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task Extra_new_resources_never_widen_the499_resource_envelope(int extra)
    {
        var bytes = Envelope(SyncTeXResourceFixture.Pins(), extra: extra);
        await using var archive = new ZipRuntimeArchive();
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.PrepareAsync(bytes.Length,
            _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), default));
        Assert.Equal(0, archive.PinsStreamsOpened); Assert.Equal(0, archive.ResourceStreamsOpened);
    }

    [Fact]
    public async Task Complete_but_untrusted499_manifest_is_refused_before_resource_streams()
    {
        var pins = SyncTeXResourceFixture.Pins().Select(p => p.Name == "synctex-query.js" ? p with { Sha256 = new string('a', 64) } : p).ToArray();
        var bytes = Envelope(pins); await using var archive = new ZipRuntimeArchive();
        await Assert.ThrowsAsync<FormatException>(() => archive.PrepareAsync(bytes.Length,
            _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), default));
        Assert.Equal(1, archive.PinsStreamsOpened); Assert.Equal(0, archive.ResourceStreamsOpened);
    }

    [Fact]
    public async Task Trusted_new_pins_still_require_exact_declared_lengths_before_resource_streams()
    {
        var bytes = Envelope(SyncTeXResourceFixture.Pins()); var (central, local) = ZipFixture.Find(bytes, "files/synctex-query.wasm");
        ZipFixture.Set32(bytes, central + 24, NativeRuntimeProfiles.SyncTeXQueryWasmLength + 1u);
        ZipFixture.Set32(bytes, local + 22, NativeRuntimeProfiles.SyncTeXQueryWasmLength + 1u);
        await using var archive = new ZipRuntimeArchive();
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.PrepareAsync(bytes.Length,
            _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), default));
        Assert.Equal(1, archive.PinsStreamsOpened); Assert.Equal(0, archive.ResourceStreamsOpened);
    }

    [SyncTeXRuntimeFact]
    public async Task Actual499_zip_install_rechecks_new_profile_and_refuses_extra_decoded_bytes_and_CRC()
    {
        var fixture = ActualSyncTeXResources.Fixture.Value;
        (string Name, byte[] Data, int Attributes)[] entries =
            [("pins.json", fixture.Json, 0), .. fixture.Inputs.Select(i => ("files/" + i.Name, i.Bytes, 0))];
        var backend = new SetupBackend(); var selection = new SetupSelection();
        await using var session = new ZipRuntimeSetupSession(new(new(backend), selection));
        foreach (bool directory in new[] { false, true })
        {
            var bytes = ZipFixture.Build(directory ? [.. entries, ("files/", Array.Empty<byte>(), 0x10)] : entries, CompressionLevel.Optimal);
            await session.InstallZipAsync(bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)));
            Assert.Equal(RuntimeSetupState.Installed, session.State); Assert.Equal(499, session.Completed);
            Assert.Equal(SyncTeXResourceFixture.Profile, LocalRuntimeSelection.Decode(selection.Value!).Profile);
            await session.RecheckAsync(); Assert.Equal(RuntimeSetupState.Installed, session.State);
        }
        string prior = selection.Value!; int calls = backend.Calls;
        var original = ZipFixture.Build(entries, CompressionLevel.Optimal);
        var (c, l) = ZipFixture.Find(original, "files/synctex-query.js");
        uint crc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(c + 16, 4));
        var corrupt = original.ToArray(); ZipFixture.Set32(corrupt, c + 16, crc ^ 1); ZipFixture.Set32(corrupt, l + 14, crc ^ 1);
        var extended = entries.ToArray(); int index = Array.FindIndex(extended, e => e.Name == "files/synctex-query.js");
        extended[index] = (extended[index].Name, [.. extended[index].Data, 0], 0);
        var bomb = ZipFixture.Build(extended, CompressionLevel.Optimal); (c, l) = ZipFixture.Find(bomb, "files/synctex-query.js");
        ZipFixture.Set32(bomb, c + 24, NativeRuntimeProfiles.SyncTeXQueryLoaderLength); ZipFixture.Set32(bomb, l + 22, NativeRuntimeProfiles.SyncTeXQueryLoaderLength);
        ZipFixture.Set32(bomb, c + 16, crc); ZipFixture.Set32(bomb, l + 14, crc);
        foreach (var bytes in new[] { corrupt, bomb })
        {
            await session.InstallZipAsync(bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)));
            Assert.Equal(RuntimeSetupState.Corrupt, session.State); Assert.Equal(prior, selection.Value); Assert.Equal(calls, backend.Calls);
        }
    }
}
