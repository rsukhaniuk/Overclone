using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Imports;
using Client.Workspace.History;
using Xunit;

namespace Workspace.Tests.BinaryAssetReplacement;

// Synthetic IO/storage only. Real coordinator, model commands, hashing and save.
internal sealed class ReplacementTestHost : IAsyncDisposable
{
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);
    internal CompilationTestWorkspace Work = null!;
    internal WorkspaceCoordinator Workspace => Work.Workspace;
    internal BinaryAssetReplacementController Controller = null!;
    internal SingleFileImportController Additive = null!;
    internal FileId TargetId, OtherId;
    internal WorkingFile Target => Workspace.Files.Single(f => f.Id == TargetId);
    internal byte[] Original = [];
    internal readonly List<ReadStream> Streams = [];
    internal readonly List<Task> Operations = [];
    private readonly List<Task> _views = [];
    private SynchronizationContext _ui = null!;
    private int _thread;
    internal int Opens, Paints, Notifications, HashYields;
    internal bool ViewDisposed, FailHashYield;
    internal TaskCompletionSource? HashHold, NextHashReached;
    internal readonly TaskCompletionSource HashReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task<ReplacementTestHost> Open()
    {
        var h = new ReplacementTestHost { Work = await CompilationTestWorkspace.Open(), _ui = SynchronizationContext.Current!, _thread = Environment.CurrentManagedThreadId };
        h.TargetId = h.Workspace.Files.Single(f => f.MediaKind == "binary").Id;
        Assert.True(h.Workspace.RenameFile(h.TargetId, "figures/original.bin"));
        Assert.True(h.Workspace.CreateFileFromBytes("other.bin", [1, 0, 7])); h.OtherId = h.Workspace.ActiveFileId!.Value;
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(default)).Status);
        h.Workspace.SelectFile(h.TargetId);
        h.Original = (await h.Workspace.GetFileBytesAsync(h.Target, default)).ToArray();
        h.Mount(); return h;
    }
    internal void Mount()
    {
        if (Controller is not null) { Controller.Changed -= Changed; Controller.Dispose(); }
        if (Additive is not null) Additive.Dispose();
        Controller = new(Workspace, Work.Lock, Dispatch, Yield); Controller.Changed += Changed; Controller.SetTarget(Target);
        Additive = new(Workspace, Work.Lock, Dispatch);
    }
    private async Task Yield(CancellationToken ct)
    {
        _ = ct; HashYields++; HashReached.TrySetResult(); NextHashReached?.TrySetResult();
        if (FailHashYield) throw new IOException("private scheduler failure");
        if (HashHold is { } held) await held.Task; // Actual hash still runs; only scheduler IO is held noncooperatively.
    }
    internal Task Dispatch(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            try { Assert.Equal(_thread, Environment.CurrentManagedThreadId); action(); done.TrySetResult(); }
            catch (Exception e) { done.TrySetException(e); }
        }, null);
        return done.Task;
    }
    private void Changed()
    {
        Notifications++; _views.Add(Dispatch(() => { if (!ViewDisposed) Paints++; }));
    }
    internal Task Stage(ReadStream stream, string name = "different-name.dat", long? size = null)
    {
        Streams.Add(stream);
        return Track(Controller.StageAsync(name, size ?? stream.Total, _ => { Opens++; return new ValueTask<Stream>(stream); }));
    }
    internal Task Import(ReadStream stream)
    {
        Streams.Add(stream);
        return Track(Additive.ImportAsync("addition.bin", stream.Total, _ => { Opens++; return new ValueTask<Stream>(stream); }));
    }
    internal Task Track(Task task) { Operations.Add(task); return task; }
    internal Task Undo() => Track(Controller.UndoAsync());
    internal Task Drain() => Task.WhenAll(_views.ToArray()).WaitAsync(Bound);
    public async ValueTask DisposeAsync()
    {
        ViewDisposed = true; Controller.Changed -= Changed; Controller.Dispose(); Additive.Dispose();
        HashHold?.TrySetResult();
        foreach (var stream in Streams) stream.ReleaseAll();
        foreach (var gate in Work.Repo.Gates) gate.Release.TrySetResult(Work.Repo.Content[gate.Hash]);
        await Task.WhenAll(Operations).WaitAsync(Bound); await Drain(); await Work.DisposeAsync();
    }

    internal sealed class ReadStream : Stream
    {
        private readonly byte[]? _bytes;
        private long _position;
        internal readonly long Total;
        internal long Consumed => _position;
        internal int Reads, Disposals, MaxRequest;
        internal bool HoldRead, HoldDispose, FailRead, FailDispose;
        internal int? InvalidCount;
        internal SynchronizationContext? ReadContext, DisposeContext;
        internal readonly TaskCompletionSource ReadReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposeReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ReadStream(byte[] bytes) { _bytes = bytes; Total = bytes.Length; }
        internal ReadStream(long total) => Total = total;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken; Reads++; MaxRequest = Math.Max(MaxRequest, buffer.Length); ReadContext = SynchronizationContext.Current;
            ReadReached.TrySetResult(); if (HoldRead) await ReadRelease.Task;
            if (FailRead) throw new IOException("private read error must not be shown");
            if (InvalidCount is { } invalid) return invalid;
            int count = (int)Math.Min(buffer.Length, Total - _position);
            if (_bytes is null) buffer.Span[..count].Fill(0xa7); else _bytes.AsMemory((int)_position, count).CopyTo(buffer);
            _position += count; return count;
        }
        public override async ValueTask DisposeAsync()
        {
            Disposals++; DisposeContext = SynchronizationContext.Current; DisposeReached.TrySetResult();
            if (HoldDispose) await DisposeRelease.Task;
            if (FailDispose) throw new IOException("private disposal detail");
            GC.SuppressFinalize(this);
        }
        internal void ReleaseAll() { ReadRelease.TrySetResult(); DisposeRelease.TrySetResult(); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal sealed class RecoveryRepository(ProjectSnapshot snapshot) : IProjectRepository
    {
        internal int Reads;
        public Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct) => Task.FromResult(snapshot);
        public Task<CommitResult> CommitAsync(ProjectCommit batch, RevisionId? expectedHead, CancellationToken ct) => throw new NotSupportedException();
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct) => throw new NotSupportedException();
        public Task<Project> CreateProjectAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId id, RevisionId oldHead, RevisionId newHead, CancellationToken ct) => throw new NotSupportedException();
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId id, RevisionId revision, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> ReadContentAsync(SHA256 hash, CancellationToken ct) { Reads++; throw new NotSupportedException(); }
        public Task<ImportPublishResult> PublishImportAsync(StagedProject staged, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId id, RevisionId expected, ProjectRevision revision,
            RevisionId source, string reason, CancellationToken ct) => throw new NotSupportedException();
    }
}
