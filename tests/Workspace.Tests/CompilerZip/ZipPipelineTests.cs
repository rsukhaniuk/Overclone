using System.IO.Compression;
using System.Security.Cryptography;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.RuntimeAssets;
using Workspace.Tests.CompilationNative;
using Xunit;

namespace Workspace.Tests.CompilerZip;

// Reuse the existing fixture attribute, without new skip/fake-pass logic.
// These tests require root's explicit AUTHORING_RUNTIME_DIR mount. No vendor bytes shipped.
public sealed class ZipPipelineTests
{
    private static readonly Lazy<(byte[] Pins, NativeResourcePin[] Declared, Dictionary<string, byte[]> Bytes)> Fixture = new(() =>
    {
        var root = Environment.GetEnvironmentVariable("AUTHORING_RUNTIME_DIR") ?? throw new InvalidOperationException("Root must mount AUTHORING_RUNTIME_DIR.");
        var pins = File.ReadAllBytes(Path.Combine(root,"pins.json"));
        var declared = LocalRuntimeSelection.ParsePins(pins).ToArray();
        return (pins,declared,declared.ToDictionary(p => p.Name,p => File.ReadAllBytes(Path.Combine(root,"files",p.Name)),StringComparer.Ordinal));
    });
    private static (string Name,byte[] Data,int Attributes)[] Entries()
        => [("pins.json",Fixture.Value.Pins,0), .. Fixture.Value.Declared.Select(p => ("files/"+p.Name,Fixture.Value.Bytes[p.Name],0))];
    private static byte[] Zip(bool directory=false)
        => ZipFixture.Build(directory ? [.. Entries(), ("files/",Array.Empty<byte>(),0x10)] : Entries(),CompressionLevel.Optimal);
    private static LocalRuntimeFile[] Files() => Fixture.Value.Declared.Select(p => new LocalRuntimeFile(p.Name,p.Length,_ => Task.FromResult(Fixture.Value.Bytes[p.Name]))).ToArray();

    [AuthoringRuntimeSetupFact]
    public async Task Exact497_ZIP_calls_real_setup_installs_and_rechecks_without_changing_source_bytes()
    {
        var backend = new SetupBackend(); var selection = new SetupSelection();
        await using var session = new ZipRuntimeSetupSession(new(new(backend),selection));
        foreach (bool directory in new[] { false,true })
        {
            var bytes = Zip(directory); var hash = SHA256.HashData(bytes); var source = new ZipSpyStream(bytes);
            await session.InstallZipAsync(bytes.Length,_ => Task.FromResult<Stream>(source));
            Assert.Equal(RuntimeSetupState.Installed,session.State); Assert.False(session.Busy); Assert.Equal(497,session.Completed);
            Assert.True(source.Closed); Assert.Equal(hash,SHA256.HashData(bytes));
            var mapping = LocalRuntimeSelection.Decode(selection.Value!);
            Assert.Equal(LocalRuntimeSelection.Profile,mapping.Profile); Assert.Equal(497,mapping.Resources.Length);
            await session.RecheckAsync(); Assert.Equal(RuntimeSetupState.Installed,session.State);
        }
        Assert.Equal(2,selection.Writes); Assert.False(backend.Locked);
    }
    [AuthoringRuntimeSetupFact]
    public async Task Trusted_pins_preflight_every_resource_name_and_length_before_resource_streams()
    {
        for(int change=0;change<2;change++)
        {
            var entries = Entries();
            entries[1] = change==0 ? ("files/foreign-but-flat",entries[1].Data,0) : (entries[1].Name,new byte[entries[1].Data.Length+1],0);
            var bytes = ZipFixture.Build(entries,CompressionLevel.Optimal); await using var archive = new ZipRuntimeArchive();
            await Assert.ThrowsAsync<InvalidDataException>(() => archive.PrepareAsync(bytes.Length,_ => Task.FromResult<Stream>(new ZipSpyStream(bytes)),default));
            Assert.Equal(1,archive.PinsStreamsOpened); Assert.Equal(0,archive.ResourceStreamsOpened);
        }
    }
    [AuthoringRuntimeSetupFact]
    public async Task Actual_decompressed_extra_length_and_CRC_corruption_fail_canonical_pipeline_preserving_prior()
    {
        var backend = new SetupBackend(); var selection = new SetupSelection { Value="prior" };
        await using var session = new ZipRuntimeSetupSession(new(new(backend),selection));
        var original = Zip(); var pin = Fixture.Value.Declared.First(p => p.Length>0 && p.Length<1048576);
        var originalLocations = ZipFixture.Find(original,"files/"+pin.Name);
        uint originalCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(originalLocations.Central+16,4));
        var entries = Entries(); int index = Array.FindIndex(entries,e => e.Name=="files/"+pin.Name);
        // Actual deflate output is larger than the trusted/declared length. Keep the
        // ORIGINAL expected CRC so a length-clamping implementation cannot pass by CRC alone.
        entries[index] = (entries[index].Name,[.. entries[index].Data, .. new byte[1048576]],0);
        var bomb = ZipFixture.Build(entries,CompressionLevel.Optimal); var (c,l) = ZipFixture.Find(bomb,"files/"+pin.Name);
        ZipFixture.Set32(bomb,c+24,(uint)pin.Length); ZipFixture.Set32(bomb,l+22,(uint)pin.Length);
        ZipFixture.Set32(bomb,c+16,originalCrc); ZipFixture.Set32(bomb,l+14,originalCrc);
        var crc = original.ToArray(); (c,l) = ZipFixture.Find(crc,"files/"+pin.Name);
        ZipFixture.Set32(crc,c+16,originalCrc^1); ZipFixture.Set32(crc,l+14,originalCrc^1);
        foreach(var input in new[] { bomb,crc })
        {
            var source = new ZipSpyStream(input);
            await session.InstallZipAsync(input.Length,_ => Task.FromResult<Stream>(source));
            Assert.Equal(RuntimeSetupState.Corrupt,session.State); Assert.Equal("prior",selection.Value);
            Assert.Equal(0,backend.Calls); Assert.True(source.Closed); Assert.False(session.Busy);
        }
    }
    [AuthoringRuntimeSetupFact]
    public async Task Manual_resource_cancellation_drains_actual_read_and_blocks_ZIP_and_recheck()
    {
        var entered = ZipFixture.Signal(); var release = ZipFixture.Signal();
        var backend = new SetupBackend(); var selection = new SetupSelection { Value="prior" };
        await using var session = new ZipRuntimeSetupSession(new(new(backend),selection));
        var files = Files(); var first = files[0]; files[0] = first with { Read=async token => { entered.SetResult(true); await release.Task; return Fixture.Value.Bytes[first.Name]; } };
        var install = session.InstallManualAsync(_ => Task.FromResult(Fixture.Value.Pins),files);
        try
        {
            await entered.Task; session.Cancel(); await Task.Yield(); Assert.True(session.Busy);
            int opens=0; await session.InstallZipAsync(100,_ => { opens++; throw new Exception("No second route"); });
            await session.RecheckAsync(); Assert.Equal(0,opens); Assert.Equal(0,selection.Reads); Assert.Equal(0,backend.Calls);
        }
        finally { release.TrySetResult(true); await install; }
        Assert.Equal(RuntimeSetupState.Cancelled,session.State); Assert.False(session.Busy); Assert.Equal("prior",selection.Value);
    }
    [AuthoringRuntimeSetupFact]
    public async Task Uncancellable_publication_selection_and_source_disposal_retain_one_admission_and_late_success()
    {
        var bytes = Zip(); var source = new ZipSpyStream(bytes) { DisposeRelease=ZipFixture.Signal() };
        var backend = new SetupBackend { WriteEntered=ZipFixture.Signal(),WriteRelease=ZipFixture.Signal() };
        var selection = new SetupSelection { CommitEntered=ZipFixture.Signal(),CommitRelease=ZipFixture.Signal() };
        var session = new ZipRuntimeSetupSession(new(new(backend),selection));
        var install = session.InstallZipAsync(bytes.Length,_ => Task.FromResult<Stream>(source)); Task? disposal=null;
        try
        {
            await backend.WriteEntered.Task; Assert.True(session.Busy); Assert.False(session.CanCancel); Assert.False(source.Closed);
            session.Cancel(); await session.RecheckAsync(); Assert.Equal(0,selection.Reads);
            backend.WriteRelease.SetResult(true); await selection.CommitEntered.Task;
            Assert.False(session.CanCancel); Assert.False(source.Closed);
            disposal=session.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
            selection.CommitRelease.SetResult(true); await source.DisposeEntered.Task;
            Assert.True(session.Busy); Assert.False(install.IsCompleted); Assert.False(disposal.IsCompleted); Assert.NotNull(selection.Value);
            source.DisposeRelease.SetResult(true); await install; await disposal;
            Assert.Equal(RuntimeSetupState.Installed,session.State); Assert.False(session.Busy); Assert.True(source.Closed);
            Assert.Equal(1,selection.Writes); Assert.False(backend.Locked);
        }
        finally
        {
            backend.WriteRelease.TrySetResult(true); selection.CommitRelease.TrySetResult(true); source.DisposeRelease.TrySetResult(true);
            await install; if(disposal is not null) await disposal; await session.DisposeAsync();
        }
    }
    [AuthoringRuntimeSetupFact]
    public async Task Quota_permission_and_store_errors_preserve_previous_installed_selection_and_close_ZIP()
    {
        var backend = new SetupBackend(); var selection = new SetupSelection();
        await using var session = new ZipRuntimeSetupSession(new(new(backend),selection));
        var bytes = Zip(); await session.InstallZipAsync(bytes.Length,_ => Task.FromResult<Stream>(new ZipSpyStream(bytes)));
        Assert.Equal(RuntimeSetupState.Installed,session.State); var prior=selection.Value;
        foreach(var (failure,state) in new[] { (RuntimeAssetStatus.QuotaExceeded,RuntimeSetupState.QuotaExceeded),
            (RuntimeAssetStatus.PermissionDenied,RuntimeSetupState.PermissionDenied),(RuntimeAssetStatus.StorageFailure,RuntimeSetupState.StorageFailure) })
        {
            backend.Failure=failure; var source=new ZipSpyStream(bytes);
            await session.InstallZipAsync(bytes.Length,_ => Task.FromResult<Stream>(source));
            Assert.Equal(state,session.State); Assert.Equal(prior,selection.Value); Assert.True(source.Closed); Assert.False(session.Busy);
        }
        backend.Failure=null;
        var invalid=ZipFixture.Build(ZipFixture.Synthetic());
        await session.InstallZipAsync(invalid.Length,_ => Task.FromResult<Stream>(new ZipSpyStream(invalid)));
        Assert.Equal(RuntimeSetupState.InvalidSelection,session.State); Assert.Equal(prior,selection.Value);
        await session.RecheckAsync(); Assert.Equal(RuntimeSetupState.Installed,session.State);
    }
}
