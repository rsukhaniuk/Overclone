using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Client.Compilation.Provisioning;
using Workspace.Tests.CompilationNative;
using Xunit;

namespace Workspace.Tests.CompilerZip;

public sealed class ZipSchemaTests
{
    [Fact]
    public async Task Selection_descriptor_is_inert_and_selected_size_cap_precedes_source_open()
    {
        var backend = new SetupBackend(); var selection = new SetupSelection { Value = "prior" };
        await using var session = new ZipRuntimeSetupSession(new(new(backend), selection));
        int opens = 0;
        Task<Stream> Open(CancellationToken _) { opens++; return Task.FromResult<Stream>(new MemoryStream()); }
        Assert.Equal(0, opens); Assert.Equal(RuntimeSetupState.Empty, session.State);
        await session.InstallZipAsync(ZipRuntimeArchive.MaxZipBytes + 1, Open);
        Assert.Equal(RuntimeSetupState.InvalidSelection, session.State); Assert.Equal(0, opens);
        Assert.Equal(0, backend.Calls); Assert.Equal("prior", selection.Value);
    }
    [Theory]
    [InlineData("../outside")]
    [InlineData("files/../outside")]
    [InlineData("files/a/b")]
    [InlineData("files\\a")]
    [InlineData("FILES/a")]
    [InlineData("other.txt")]
    [InlineData("/files/a")]
    public async Task Bad_names_refuse_before_pins_or_resource_entry_streams(string name)
    {
        var entries = ZipFixture.Synthetic(); entries[1] = (name, [1], 0);
        await RefuseEnvelope(ZipFixture.Build(entries));
    }
    [Fact]
    public async Task Missing_extra_duplicate_and_case_alias_entries_preflight_without_entry_reads()
    {
        var entries = ZipFixture.Synthetic(); await RefuseEnvelope(ZipFixture.Build(entries[..^1]));
        await RefuseEnvelope(ZipFixture.Build([.. entries, ("unexpected", new byte[0], 0)]));
        entries[2] = entries[1]; await RefuseEnvelope(ZipFixture.Build(entries));
        entries = ZipFixture.Synthetic(); entries[2] = (entries[1].Name.ToUpperInvariant(), [1], 0);
        await RefuseEnvelope(ZipFixture.Build(entries));
        entries = ZipFixture.Synthetic(); entries[0] = ("files/pins.json", [1], 0);
        await RefuseEnvelope(ZipFixture.Build(entries));
    }
    [Theory]
    [InlineData(0xa1ff0000u)] // symlink
    [InlineData(0x11a40000u)] // FIFO
    [InlineData(0x21a40000u)] // character device
    [InlineData(0x41ed0010u)] // directory masquerading as resource
    public async Task Links_and_special_types_refuse_before_any_entry_read(uint attributes)
    {
        var entries = ZipFixture.Synthetic(); entries[1] = (entries[1].Name, entries[1].Data, unchecked((int)attributes));
        await RefuseEnvelope(ZipFixture.Build(entries));
    }
    [Theory]
    [InlineData(1, 0)] // encryption
    [InlineData(0, 99)] // unsupported compression
    public async Task Flags_and_compression_refuse_before_payload_streams(int flags, int method)
    {
        var bytes = ZipFixture.Build(ZipFixture.Synthetic()); var (central, local) = ZipFixture.Find(bytes, "files/f000");
        ZipFixture.Set16(bytes, central + 8, flags); ZipFixture.Set16(bytes, local + 6, flags);
        ZipFixture.Set16(bytes, central + 10, method); ZipFixture.Set16(bytes, local + 8, method);
        await RefuseEnvelope(bytes);
    }
    [Fact]
    public async Task Declared_resource_and_pins_caps_and_central_corruption_refuse_before_entry_reads()
    {
        foreach (var name in new[] { "pins.json", "files/f000" })
        {
            var bytes = ZipFixture.Build(ZipFixture.Synthetic()); var (central, local) = ZipFixture.Find(bytes, name);
            uint tooBig = (uint)(name == "pins.json" ? LocalRuntimeSelection.MaxPinsBytes + 1 : LocalRuntimeSelection.Limits.MaxFileBytes + 1);
            ZipFixture.Set32(bytes, central + 24, tooBig); ZipFixture.Set32(bytes, local + 22, tooBig);
            await RefuseEnvelope(bytes);
        }
        var broken = ZipFixture.Build(ZipFixture.Synthetic()); broken[^22] ^= 1;
        await RefuseEnvelope(broken);
    }
    [Fact]
    public async Task Resource_total_limit_and_local_name_alias_reject_before_entry_reads()
    {
        var bytes = ZipFixture.Build(ZipFixture.Synthetic(),CompressionLevel.Optimal);
        for (int i=0;i<3;i++)
        {
            var (central,local) = ZipFixture.Find(bytes,$"files/f{i:000}");
            ZipFixture.Set32(bytes,central+24,(uint)LocalRuntimeSelection.Limits.MaxFileBytes);
            ZipFixture.Set32(bytes,local+22,(uint)LocalRuntimeSelection.Limits.MaxFileBytes);
        }
        await RefuseEnvelope(bytes);
        bytes = ZipFixture.Build(ZipFixture.Synthetic());
        var location = ZipFixture.Find(bytes,"files/f000");
        bytes[location.Local+30] = (byte)'X'; // local/central name discrepancy
        await RefuseEnvelope(bytes);
    }
    [Fact]
    public async Task Actual_source_length_mismatch_is_invalid_and_closes_source_without_storage()
    {
        var bytes = ZipFixture.Build(ZipFixture.Synthetic());
        var backend = new SetupBackend(); var selection = new SetupSelection { Value="prior" };
        await using var session = new ZipRuntimeSetupSession(new(new(backend),selection));
        foreach (int declared in new[] { bytes.Length-1,bytes.Length+1 })
        {
            var source = new ZipSpyStream(bytes);
            await session.InstallZipAsync(declared,_ => Task.FromResult<Stream>(source));
            Assert.Equal(RuntimeSetupState.InvalidSelection,session.State); Assert.True(source.Closed);
            Assert.False(session.Busy); Assert.Equal("prior",selection.Value); Assert.Equal(0,backend.Calls);
        }
    }
    [Fact]
    public async Task Forged_497_closure_and_manifest_CRC_refuse_without_resource_entry_reads()
    {
        var fakePins = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0,497).Select(i => new { name = $"f{i:000}", length = 1, sha256 = new string('0',64) }));
        var entries = ZipFixture.Synthetic(); entries[0] = ("pins.json", fakePins, 0);
        await using (var zip = new ZipRuntimeArchive())
        {
            var bytes = ZipFixture.Build(entries);
            await Assert.ThrowsAsync<FormatException>(() => zip.PrepareAsync(bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), default));
            Assert.Equal(1, zip.PinsStreamsOpened); Assert.Equal(0, zip.ResourceStreamsOpened);
        }
        var corrupt = ZipFixture.Build(ZipFixture.Synthetic()); var (c,l) = ZipFixture.Find(corrupt,"pins.json");
        ZipFixture.Set32(corrupt,c+16,123); ZipFixture.Set32(corrupt,l+14,123);
        await using var bad = new ZipRuntimeArchive();
        await Assert.ThrowsAsync<InvalidDataException>(() => bad.PrepareAsync(corrupt.Length,_ => Task.FromResult<Stream>(new MemoryStream(corrupt,false)),default));
        Assert.Equal(1,bad.PinsStreamsOpened); Assert.Equal(0,bad.ResourceStreamsOpened);
    }
    [Fact]
    public async Task Cancellation_and_disposal_hold_shared_admission_until_actual_source_read_and_close()
    {
        var bytes = ZipFixture.Build(ZipFixture.Synthetic());
        var stream = new ZipSpyStream(bytes) { ReadRelease = ZipFixture.Signal(), DisposeRelease = ZipFixture.Signal() };
        var backend = new SetupBackend(); var selection = new SetupSelection { Value = "prior" };
        var session = new ZipRuntimeSetupSession(new(new(backend), selection));
        var operation = session.InstallZipAsync(bytes.Length, _ => Task.FromResult<Stream>(stream));
        Task? disposal = null;
        try
        {
            await stream.ReadEntered.Task; Assert.True(session.Busy); Assert.Equal(RuntimeSetupState.Preparing,session.State);
            session.Cancel(); Assert.True(session.Busy); Assert.Equal(RuntimeSetupState.Finishing,session.State);
            int otherReads = 0;
            await session.RecheckAsync();
            await session.InstallZipAsync(bytes.Length,_ => { otherReads++; throw new Exception("No queued ZIP"); });
            await session.InstallManualAsync(_ => { otherReads++; throw new Exception("No queued pins"); }, ZipFixture.Descriptors());
            Assert.Equal(0,otherReads); Assert.Equal(0,selection.Reads); Assert.Equal(0,backend.Calls);
            disposal = session.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
            stream.ReadRelease.SetResult(true); await stream.DisposeEntered.Task;
            Assert.True(session.Busy); Assert.False(operation.IsCompleted); Assert.False(disposal.IsCompleted);
            stream.DisposeRelease.SetResult(true); await operation; await disposal;
            Assert.False(session.Busy); Assert.True(stream.Closed); Assert.Equal(RuntimeSetupState.Cancelled,session.State);
            Assert.Equal("prior",selection.Value); Assert.Equal(0,backend.Calls);
        }
        finally
        {
            stream.ReadRelease.TrySetResult(true); stream.DisposeRelease.TrySetResult(true);
            await operation; if (disposal is not null) await disposal; await session.DisposeAsync();
        }
    }
    [Fact]
    public async Task Manual_pins_read_shares_admission_and_completed_fault_is_observed_before_reuse()
    {
        var gate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new SetupBackend(); var selection = new SetupSelection { Value = "prior" };
        await using var session = new ZipRuntimeSetupSession(new(new(backend), selection));
        var first = session.InstallManualAsync(_ => gate.Task,ZipFixture.Descriptors());
        try
        {
            Assert.True(session.Busy); Assert.Equal(RuntimeSetupState.Preparing,session.State); session.Cancel();
            await session.RecheckAsync(); Assert.Equal(0,selection.Reads);
            gate.SetException(new IOException("private detail MUST NOT appear in status")); await first;
            Assert.False(session.Busy); Assert.Equal(RuntimeSetupState.Cancelled,session.State); Assert.Equal(0,backend.Calls); Assert.Equal("prior",selection.Value);
            await session.RecheckAsync(); Assert.Equal(1,selection.Reads);
            Assert.Equal(RuntimeSetupState.InvalidSelection,session.State); // invalid prior metadata, actual setup path
        }
        finally { gate.TrySetResult([]); await first; }
    }
    private static async Task RefuseEnvelope(byte[] bytes)
    {
        await using var archive = new ZipRuntimeArchive(); var spy = new ZipSpyStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.PrepareAsync(bytes.Length,_ => Task.FromResult<Stream>(spy),default));
        Assert.Equal(0,archive.PinsStreamsOpened); Assert.Equal(0,archive.ResourceStreamsOpened);
        await archive.DisposeAsync(); Assert.True(spy.Closed);
    }
}

internal static class ZipFixture
{
    internal static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static LocalRuntimeFile[] Descriptors() => Enumerable.Range(0,497).Select(i => new LocalRuntimeFile($"f{i:000}",1,_ => throw new Exception("No resource read expected"))).ToArray();
    internal static (string Name, byte[] Data, int Attributes)[] Synthetic()
        => [("pins.json",Encoding.UTF8.GetBytes("[]"),0), .. Enumerable.Range(0,497).Select(i => ($"files/f{i:000}",new byte[]{1},0))];
    internal static byte[] Build((string Name, byte[] Data, int Attributes)[] entries, CompressionLevel level = CompressionLevel.NoCompression)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory,ZipArchiveMode.Create,true))
            foreach (var item in entries)
            {
                var entry = zip.CreateEntry(item.Name,level); entry.ExternalAttributes = item.Attributes;
                if (item.Name == "files/") continue;
                using var stream = entry.Open(); stream.Write(item.Data);
            }
        return memory.ToArray();
    }
    internal static (int Central,int Local) Find(byte[] bytes,string name)
    {
        int end = bytes.Length-22; int p = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end+16,4)));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end+10,2));
        for(int i=0;i<count;i++)
        {
            int n = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p+28,2));
            if(Encoding.ASCII.GetString(bytes,p+46,n)==name)
                return(p,checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p+42,4))));
            p+=46+n+BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p+30,2))+BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p+32,2));
        }
        throw new Exception("Test fixture entry missing");
    }
    internal static void Set16(byte[] b,int p,int value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p,2),checked((ushort)value));
    internal static void Set32(byte[] b,int p,uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p,4),value);
}

internal sealed class ZipSpyStream(byte[] bytes) : Stream
{
    private readonly MemoryStream _inner = new(bytes,false);
    public TaskCompletionSource<bool> ReadEntered { get; } = ZipFixture.Signal();
    public TaskCompletionSource<bool> DisposeEntered { get; } = ZipFixture.Signal();
    public TaskCompletionSource<bool>? ReadRelease, DisposeRelease;
    public bool Closed { get; private set; }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
    {
        ReadEntered.TrySetResult(true);
        if(ReadRelease is not null) await ReadRelease.Task; // deliberately ignores cancellation; real settlement gate
        return await _inner.ReadAsync(buffer,CancellationToken.None);
    }
    public override int Read(byte[] b,int o,int n) => _inner.Read(b,o,n);
    public override async ValueTask DisposeAsync()
    {
        DisposeEntered.TrySetResult(true); if(DisposeRelease is not null) await DisposeRelease.Task;
        Closed=true; _inner.Dispose(); GC.SuppressFinalize(this);
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long o,SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long n) => throw new NotSupportedException();
    public override void Write(byte[] b,int o,int n) => throw new NotSupportedException();
}
