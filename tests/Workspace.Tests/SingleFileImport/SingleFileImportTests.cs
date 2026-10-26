using System.Text;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Imports;
using Client.Workspace.History;
using Client.Workspace.Transfer;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.SingleFileImport;

// Real coordinator/repository working-file contracts; only IO/storage are fake.
// No actual browser, compiler, native/PDF or provider acceptance is implied.
public sealed class SingleFileImportTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);
    private sealed class Host : IAsyncDisposable
    {
        internal CompilationTestWorkspace Work = null!;
        internal WorkspaceCoordinator Workspace => Work.Workspace;
        internal SingleFileImportController Import = null!;
        internal readonly List<ControlledStream> Streams = [];
        internal readonly List<Task> Operations = [];
        private readonly List<Task> _views = [];
        private SynchronizationContext _ui = null!;
        private int _thread;
        internal int Opens, Notifications, Paints;
        internal bool ViewDisposed;
        internal SynchronizationContext? OpenContext;
        internal static async Task<Host> Open(bool readOnly = false)
        {
            var h = new Host { Work = await CompilationTestWorkspace.Open(readOnly), _ui = SynchronizationContext.Current!, _thread = Environment.CurrentManagedThreadId };
            h.Import = new(h.Workspace, h.Work.Lock, h.Dispatch);
            h.Import.Changed += h.Changed;
            return h;
        }
        private Task Dispatch(Action action)
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
            Notifications++;
            var view = Dispatch(() => { if (!ViewDisposed) Paints++; });
            _views.Add(view);
        }
        internal Task Start(ControlledStream stream, string? name = "added.bin", long? advertised = null)
        {
            Streams.Add(stream);
            var task = Import.ImportAsync(name, advertised ?? stream.Total, _ =>
            {
                Opens++; OpenContext = SynchronizationContext.Current;
                return new ValueTask<Stream>(stream);
            });
            Operations.Add(task); return task;
        }
        internal Task StartOpen(Func<CancellationToken, ValueTask<Stream>> open, long size = 1)
        {
            var task = Import.ImportAsync("opened.bin", size, ct => { Opens++; OpenContext = SynchronizationContext.Current; return open(ct); });
            Operations.Add(task); return task;
        }
        internal void Remount()
        {
            Import.Changed -= Changed; Import.Dispose();
            Import = new(Workspace, Work.Lock, Dispatch); Import.Changed += Changed;
        }
        internal async Task Drain() => await Task.WhenAll(_views.ToArray()).WaitAsync(Bound);
        public async ValueTask DisposeAsync()
        {
            ViewDisposed = true; Import.Changed -= Changed; Import.Dispose();
            foreach (var stream in Streams) stream.ReleaseAll();
            await Task.WhenAll(Operations).WaitAsync(Bound);
            await Drain(); await Work.DisposeAsync();
        }
    }
    private sealed class ControlledStream : Stream
    {
        private readonly byte[]? _bytes;
        internal readonly long Total;
        private long _position;
        internal int Reads, Disposals, MaximumRequest;
        internal bool HoldRead, HoldDisposal, ThrowRead, ThrowDisposal;
        internal int? InvalidCount;
        internal long Consumed => _position;
        internal SynchronizationContext? ReadContext, DisposalContext;
        internal readonly TaskCompletionSource ReadReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposeReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ControlledStream(byte[] bytes) { _bytes = bytes; Total = bytes.Length; }
        internal ControlledStream(long generatedBytes) => Total = generatedBytes;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadContext = SynchronizationContext.Current; Reads++; MaximumRequest = Math.Max(MaximumRequest, buffer.Length);
            ReadReached.TrySetResult();
            if (HoldRead) await ReadRelease.Task; // Deliberately INTERNAL capturing await; ignores cancellation.
            if (ThrowRead) throw new IOException("private raw read detail must never become status");
            if (InvalidCount is { } invalid) return invalid;
            int count = (int)Math.Min(buffer.Length, Total - _position);
            if (_bytes is null) buffer.Span[..count].Fill(0xae);
            else _bytes.AsMemory((int)_position, count).CopyTo(buffer);
            _position += count; return count;
        }
        public override async ValueTask DisposeAsync()
        {
            DisposalContext = SynchronizationContext.Current; Disposals++; DisposeReached.TrySetResult();
            if (HoldDisposal) await DisposeRelease.Task; // Noncooperative async cleanup, owned until it REALLY finishes.
            if (ThrowDisposal) throw new IOException("private raw disposal detail");
            GC.SuppressFinalize(this);
        }
        internal void ReleaseAll() { ReadRelease.TrySetResult(); DisposeRelease.TrySetResult(); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("Never trust stream Length metadata");
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void Current_exact_bytes_save_reopen_export_and_prior_revision_are_preserved(bool binary) => Run(async () =>
    {
        await using var h = await Host.Open();
        var old = h.Workspace.CurrentRevision!; var main = h.Workspace.ActiveFileId;
        byte[] original = await h.Workspace.GetFileBytesAsync(h.Workspace.Files.First(f => f.Id == main), default);
        byte[] bytes = binary ? [255, 0, 128, 13, 10, 239, 254] : [239, 187, 191, .. Encoding.UTF8.GetBytes("Україна🙂\r\nhello\r\n")];
        string name = binary ? " figure.pdf " : " added.tex ";
        var stream = new ControlledStream(bytes);
        long generation = h.Workspace.WorkingGeneration;
        await h.Start(stream, name).WaitAsync(Bound);
        Assert.Equal(SingleFileImportState.Imported, h.Import.State); Assert.False(h.Import.Busy);
        Assert.Equal(1, h.Opens); Assert.Equal(1, stream.Disposals); Assert.Null(h.OpenContext); Assert.Null(stream.ReadContext); Assert.Null(stream.DisposalContext);
        var added = h.Workspace.Files.Single(f => f.RelativePath == name.Trim());
        Assert.Equal(added.Id, h.Workspace.ActiveFileId); Assert.Equal(binary ? "binary" : "text", added.MediaKind);
        Assert.Equal(generation + 1, h.Workspace.WorkingGeneration); Assert.True(h.Workspace.IsDirty);
        Assert.Equal(bytes, await h.Workspace.GetFileBytesAsync(added, default));
        Assert.Equal(original, await h.Workspace.GetFileBytesAsync(h.Workspace.Files.Single(f => f.Id == main), default));
        Assert.Equal(0, h.Work.Repo.Commits); // Controller never explicitly saved.
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(default)).Status);
        Assert.DoesNotContain(old.Files, f => f.Id == added.Id);
        Assert.Equal(old, h.Work.Repo.Revisions[old.Id]);
        var source = await h.Workspace.CaptureExportSourceAsync(default);
        var zip = new ZipTransferService(h.Work.Repo);
        var archive = await zip.BuildZipAsync(source, default);
        var staged = await zip.StageZipImportAsync(archive.Bytes, "roundtrip", default);
        Assert.True(staged.Valid); Assert.Equal(bytes, staged.Staged.Files.Single(f => f.RelativePath == added.RelativePath).Bytes);
        var project = h.Workspace.Project!.Id;
        await h.Workspace.CloseAsync(default); await h.Workspace.OpenAsync(project, default);
        Assert.Equal(bytes, await h.Workspace.GetFileBytesAsync(h.Workspace.Files.Single(f => f.Id == added.Id), default));
    });

    [Theory][InlineData("A-B")][InlineData("A-B-A")][InlineData("A-close-A")]
    public void Late_read_never_enters_later_project_or_session(string route) => Run(async () =>
    {
        await using var h = await Host.Open();
        var a = h.Workspace.Project!.Id; var session = h.Workspace.SessionId;
        var read = new ControlledStream([1, 2, 3]) { HoldRead = true };
        var pending = h.Start(read);
        await read.ReadReached.Task.WaitAsync(Bound);
        await h.Workspace.CloseAsync(default);
        if (route != "A-close-A")
        {
            var b = new Project(ProjectId.New(), "B", null); h.Work.Repo.Projects[b.Id] = (b, null);
            await h.Workspace.OpenAsync(b.Id, default);
            if (route == "A-B-A") await h.Workspace.CloseAsync(default);
        }
        if (route != "A-B") await h.Workspace.OpenAsync(a, default);
        Assert.NotEqual(session, h.Workspace.SessionId);
        Assert.Equal(SingleFileImportState.Stale, h.Import.State); Assert.True(h.Import.Busy);
        string before = h.Work.State(); read.ReleaseAll(); await pending.WaitAsync(Bound);
        Assert.Equal(before, h.Work.State()); Assert.DoesNotContain(h.Workspace.Files, f => f.RelativePath == "added.bin");
        Assert.Equal(1, read.Disposals); Assert.False(h.Import.Busy); Assert.Equal(SingleFileImportState.Stale, h.Import.State);
    });

    [Theory][InlineData("cancel")][InlineData("dispose")][InlineData("lost-lock")]
    public void Invalidated_noncooperative_read_holds_slot_and_never_mutates_or_late_paints(string reason) => Run(async () =>
    {
        await using var h = await Host.Open();
        var read = new ControlledStream([9]) { HoldRead = true, HoldDisposal = true };
        var pending = h.Start(read); await read.ReadReached.Task.WaitAsync(Bound);
        if (reason == "dispose") { h.ViewDisposed = true; h.Import.Dispose(); }
        else if (reason == "cancel") h.Import.Cancel();
        else h.Work.Lock.AllowAcquire = false; // No notification: full authority must still be checked after await.
        await h.Drain(); int paints = h.Paints, notifications = h.Notifications;
        var second = new ControlledStream([8]); await h.Start(second).WaitAsync(Bound);
        Assert.Equal(1, h.Opens); Assert.Equal(0, second.Reads); Assert.True(h.Import.Busy);
        read.ReadRelease.TrySetResult(); await read.DisposeReached.Task.WaitAsync(Bound);
        Assert.True(h.Import.Busy); Assert.False(pending.IsCompleted); Assert.Equal(1, read.Disposals);
        string before = h.Work.State(); read.DisposeRelease.TrySetResult(); await pending.WaitAsync(Bound); await h.Drain();
        Assert.False(h.Import.Busy); Assert.Equal(before, h.Work.State());
        if (reason == "dispose") { Assert.Equal(notifications, h.Notifications); Assert.Equal(paints, h.Paints); Assert.Equal(SingleFileImportState.Disposed, h.Import.State); }
        else Assert.Equal(reason == "cancel" ? SingleFileImportState.Cancelled : SingleFileImportState.Stale, h.Import.State);
    });

    [Fact] public void Busy_second_request_opens_nothing_and_explicit_retry_works_only_after_disposal() => Run(async () =>
    {
        await using var h = await Host.Open(); var stream = new ControlledStream([1, 0]) { HoldDisposal = true };
        var pending = h.Start(stream, "first.bin"); await stream.DisposeReached.Task.WaitAsync(Bound);
        Assert.DoesNotContain(h.Workspace.Files, f => f.RelativePath == "first.bin");
        await h.Start(new ControlledStream([2]), "second.bin").WaitAsync(Bound);
        Assert.Equal(1, h.Opens); Assert.True(h.Import.Busy); Assert.Equal(SingleFileImportState.Busy, h.Import.State);
        stream.DisposeRelease.TrySetResult(); await pending.WaitAsync(Bound);
        Assert.Equal(SingleFileImportState.Imported, h.Import.State);
        await h.Start(new ControlledStream([3]), "second.bin").WaitAsync(Bound);
        Assert.Equal(2, h.Opens); Assert.Contains(h.Workspace.Files, f => f.RelativePath == "second.bin");
    });

    [Fact] public void Same_session_edits_and_save_during_read_are_allowed() => Run(async () =>
    {
        await using var h = await Host.Open(); var session = h.Workspace.SessionId;
        var stream = new ControlledStream([7]) { HoldRead = true }; var pending = h.Start(stream);
        await stream.ReadReached.Task.WaitAsync(Bound);
        Assert.True(h.Workspace.EditText(h.Work.Main, "edited while reading Україна🙂\r\n"));
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(default)).Status);
        Assert.Equal(session, h.Workspace.SessionId); stream.ReleaseAll(); await pending.WaitAsync(Bound);
        Assert.Equal(SingleFileImportState.Imported, h.Import.State); Assert.Contains(h.Workspace.Files, f => f.RelativePath == "added.bin");
        Assert.Equal("edited while reading Україна🙂\r\n", h.Workspace.Files.Single(f => f.Id == h.Work.Main).Content);
        Assert.Equal(1, h.Work.Repo.Commits);
    });

    [Theory][InlineData("read")][InlineData("dispose")][InlineData("open")][InlineData("path")][InlineData("duplicate")]
    public void Failure_has_fixed_safe_status_and_no_content_mutation(string failure) => Run(async () =>
    {
        await using var h = await Host.Open();
        var before = h.Workspace.Files.Select(f => (f.Id, f.RelativePath, f.Content, f.BinaryContent)).ToArray();
        var selected = h.Workspace.ActiveFileId; long generation = h.Workspace.WorkingGeneration;
        var read = new ControlledStream([0xff]) { ThrowRead = failure == "read", ThrowDisposal = failure == "dispose" };
        if (failure == "open") await h.StartOpen(_ => throw new IOException("secret raw detail")).WaitAsync(Bound);
        else await h.Start(read, failure == "path" ? "../no.bin" : failure == "duplicate" ? "asset.bin" : "safe.bin").WaitAsync(Bound);
        Assert.Equal(SingleFileImportState.Failed, h.Import.State); Assert.False(h.Import.Busy);
        Assert.DoesNotContain("secret", h.Import.Notice); Assert.DoesNotContain("detail", h.Import.Notice);
        Assert.Equal(generation, h.Workspace.WorkingGeneration); Assert.Equal(selected, h.Workspace.ActiveFileId);
        Assert.Equal(before, h.Workspace.Files.Select(f => (f.Id, f.RelativePath, f.Content, f.BinaryContent)).ToArray());
        if (failure != "open") Assert.Equal(1, read.Disposals);
    });

    [Theory][InlineData(-1)][InlineData(33554433)]
    public void Advertised_invalid_size_opens_nothing(long size) => Run(async () =>
    {
        await using var h = await Host.Open(); string before = h.Work.State();
        await h.Start(new ControlledStream([1]), advertised: size).WaitAsync(Bound);
        Assert.Equal(0, h.Opens); Assert.Equal(before, h.Work.State()); Assert.Equal(SingleFileImportState.Failed, h.Import.State);
    });

    [Theory][InlineData(false)][InlineData(true)]
    public void Real_byte_bound_is_enforced_independently_of_lying_metadata(bool overflow) => Run(async () =>
    {
        await using var h = await Host.Open(); var read = new ControlledStream(ArchiveLimits.SingleFile + (overflow ? 1 : 0));
        long generation = h.Workspace.WorkingGeneration;
        await h.Start(read, advertised: 0).WaitAsync(Bound);
        Assert.Equal(1, read.Disposals); Assert.InRange(read.Consumed, 0, ArchiveLimits.SingleFile + 1);
        Assert.InRange(read.MaximumRequest, 1, 64 * 1024);
        Assert.Equal(overflow ? SingleFileImportState.Failed : SingleFileImportState.Imported, h.Import.State);
        Assert.Equal(generation + (overflow ? 0 : 1), h.Workspace.WorkingGeneration);
    });

    [Fact] public void Valid_empty_file_and_filename_default_are_preserved() => Run(async () =>
    {
        await using var h = await Host.Open(); await h.Start(new ControlledStream([]), "   ").WaitAsync(Bound);
        var created = h.Workspace.Files.Single(f => f.RelativePath == "imported.tex");
        Assert.Equal(created.Id, h.Workspace.ActiveFileId); Assert.Equal("text", created.MediaKind);
        Assert.Empty(await h.Workspace.GetFileBytesAsync(created, default)); Assert.Equal(SingleFileImportState.Imported, h.Import.State);
    });

    [Fact] public void Refused_writer_does_not_open_or_change_the_current_project() => Run(async () =>
    {
        await using var h = await Host.Open(readOnly: true); string before = h.Work.State();
        await h.Start(new ControlledStream([1])).WaitAsync(Bound);
        Assert.Equal(0, h.Opens); Assert.Equal(before, h.Work.State()); Assert.Equal(SingleFileImportState.Failed, h.Import.State);
    });

    [Fact] public void Recovery_project_is_not_read_repaired_or_mutated_by_import() => Run(async () =>
    {
        var project = ProjectId.New(); var damaged = RevisionId.New();
        var recovery = new RecoveryReport(damaged, []);
        var snapshot = new ProjectSnapshot(new Project(project, "recovery", damaged), null,
            new Dictionary<FileId, string>(), new Dictionary<FileId, byte[]>(), new Dictionary<FileId, bool>(), false, recovery);
        var repo = new RecoveryRepository(snapshot); var locks = new CompilationTestWorkspace.Locks();
        await using var workspace = new WorkspaceCoordinator(repo, locks, new HistoryService(repo));
        await workspace.OpenAsync(project, default);
        using var import = new SingleFileImportController(workspace, locks, action => { action(); return Task.CompletedTask; });
        int opens = 0;
        await import.ImportAsync("safe.bin", 1, _ => { opens++; return new ValueTask<Stream>(new MemoryStream([1])); }).WaitAsync(Bound);
        Assert.Equal(0, opens); Assert.Equal(SingleFileImportState.Failed, import.State);
        Assert.Same(recovery, workspace.Recovery); Assert.Empty(workspace.Files);
        Assert.Equal(damaged, workspace.Project!.HeadRevisionId); Assert.Equal(0, workspace.WorkingGeneration);
    });
    // Fake IO snapshot only: forbidden hidden reads/saves/repair fail immediately.
    private sealed class RecoveryRepository(ProjectSnapshot snapshot) : IProjectRepository
    {
        public Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct) => Task.FromResult(snapshot);
        public Task<CommitResult> CommitAsync(ProjectCommit batch, RevisionId? expectedHead, CancellationToken ct) => throw new NotSupportedException();
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct) => throw new NotSupportedException();
        public Task<Project> CreateProjectAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId id, RevisionId oldHead, RevisionId newHead, CancellationToken ct) => throw new NotSupportedException();
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId id, RevisionId revision, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> ReadContentAsync(SHA256 hash, CancellationToken ct) => throw new NotSupportedException();
        public Task<ImportPublishResult> PublishImportAsync(StagedProject staged, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId id, RevisionId expected, ProjectRevision revision,
            RevisionId source, string reason, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact] public void Created_identity_not_unrelated_list_last_is_selected() => Run(async () =>
    {
        await using var h = await Host.Open();
        void Append()
        {
            if (h.Workspace.Files.Any(f => f.RelativePath == "added.bin") && h.Workspace.Files.All(f => f.RelativePath != "other.tex"))
                h.Workspace.CreateFile("other.tex", "unrelated subscriber");
        }
        h.Workspace.WorkspaceChanged += Append;
        try
        {
            await h.Start(new ControlledStream([1])).WaitAsync(Bound);
            var imported = h.Workspace.Files.Single(f => f.RelativePath == "added.bin");
            Assert.NotEqual(imported.Id, h.Workspace.Files.Last().Id); Assert.Equal(imported.Id, h.Workspace.ActiveFileId);
        }
        finally { h.Workspace.WorkspaceChanged -= Append; }
    });

    [Fact] public void Remount_cannot_overlap_disposed_pages_actual_read_or_disposal() => Run(async () =>
    {
        await using var h = await Host.Open(); var stream = new ControlledStream([1]) { HoldRead = true, HoldDisposal = true };
        var pending = h.Start(stream); await stream.ReadReached.Task.WaitAsync(Bound);
        h.Remount(); Assert.True(h.Import.Busy);
        await h.Start(new ControlledStream([2]), "new.bin").WaitAsync(Bound); Assert.Equal(1, h.Opens);
        stream.ReadRelease.TrySetResult(); await stream.DisposeReached.Task.WaitAsync(Bound);
        await h.Start(new ControlledStream([3]), "new.bin").WaitAsync(Bound); Assert.Equal(1, h.Opens);
        stream.DisposeRelease.TrySetResult(); await pending.WaitAsync(Bound); await h.Drain();
        Assert.False(h.Import.Busy); Assert.DoesNotContain(h.Workspace.Files, f => f.RelativePath == "added.bin");
        await h.Start(new ControlledStream([4]), "new.bin").WaitAsync(Bound);
        Assert.Equal(2, h.Opens); Assert.Equal(SingleFileImportState.Imported, h.Import.State);
    });

    [Fact] public void Session_change_during_actual_disposal_discards_completed_bytes() => Run(async () =>
    {
        await using var h = await Host.Open(); var project = h.Workspace.Project!.Id;
        var stream = new ControlledStream([4]) { HoldDisposal = true }; var pending = h.Start(stream);
        await stream.DisposeReached.Task.WaitAsync(Bound);
        Assert.DoesNotContain(h.Workspace.Files, f => f.RelativePath == "added.bin");
        await h.Workspace.CloseAsync(default); await h.Workspace.OpenAsync(project, default);
        string before = h.Work.State(); stream.DisposeRelease.TrySetResult(); await pending.WaitAsync(Bound);
        Assert.Equal(before, h.Work.State()); Assert.Equal(SingleFileImportState.Stale, h.Import.State);
        Assert.Equal(1, stream.Disposals); Assert.False(h.Import.Busy);
    });

    [Fact] public void Same_session_late_duplicate_refuses_without_overwriting_original() => Run(async () =>
    {
        await using var h = await Host.Open(); var stream = new ControlledStream([0xff]) { HoldRead = true };
        var pending = h.Start(stream, "new.tex"); await stream.ReadReached.Task.WaitAsync(Bound);
        Assert.True(h.Workspace.CreateFile("new.tex", "keep this exact text Україна🙂"));
        var original = h.Workspace.ActiveFileId; long generation = h.Workspace.WorkingGeneration;
        stream.ReleaseAll(); await pending.WaitAsync(Bound);
        Assert.Equal(SingleFileImportState.Failed, h.Import.State);
        Assert.Equal(generation, h.Workspace.WorkingGeneration); Assert.Equal(original, h.Workspace.ActiveFileId);
        Assert.Equal("keep this exact text Україна🙂", h.Workspace.ActiveFile!.Content);
        Assert.Single(h.Workspace.Files, f => f.RelativePath == "new.tex");
    });

    [Theory][InlineData(-1)][InlineData(int.MaxValue)]
    public void Malformed_stream_read_counts_refuse_before_copy(int count) => Run(async () =>
    {
        await using var h = await Host.Open(); string before = h.Work.State();
        var stream = new ControlledStream([1]) { InvalidCount = count };
        await h.Start(stream).WaitAsync(Bound);
        Assert.Equal(before, h.Work.State()); Assert.Equal(SingleFileImportState.Failed, h.Import.State);
        Assert.Equal(1, stream.Disposals);
    });

    [Fact] public void Save_failure_keeps_imported_working_bytes_and_previous_durable_revision() => Run(async () =>
    {
        await using var h = await Host.Open(); var old = h.Workspace.CurrentRevision!.Id;
        byte[] bytes = [255, 0, 15]; await h.Start(new ControlledStream(bytes)).WaitAsync(Bound);
        var file = h.Workspace.ActiveFile!; h.Work.Repo.FailSave = true;
        Assert.NotEqual(CommitStatus.Saved, (await h.Workspace.SaveAsync(default)).Status);
        Assert.True(h.Workspace.IsDirty); Assert.Equal(old, h.Workspace.CurrentRevision!.Id);
        Assert.Equal(bytes, await h.Workspace.GetFileBytesAsync(file, default));
        h.Work.Repo.FailSave = false; Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(default)).Status);
    });

    [Fact] public void Late_open_is_disposed_once_after_owner_unmount_without_new_dispatch_or_paint() => Run(async () =>
    {
        await using var h = await Host.Open();
        var open = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new ControlledStream([1]); h.Streams.Add(stream);
        var pending = h.StartOpen(_ => { reached.TrySetResult(); return new(open.Task); });
        try
        {
            await reached.Task.WaitAsync(Bound); h.ViewDisposed = true; h.Import.Dispose(); await h.Drain();
            int paint = h.Paints, notifications = h.Notifications; string before = h.Work.State();
            Assert.True(h.Import.Busy); open.TrySetResult(stream); await pending.WaitAsync(Bound); await h.Drain();
            Assert.Equal(0, stream.Reads); Assert.Equal(1, stream.Disposals); Assert.False(h.Import.Busy);
            Assert.Equal(before, h.Work.State()); Assert.Equal(paint, h.Paints); Assert.Equal(notifications, h.Notifications);
        }
        finally { open.TrySetResult(stream); await pending.WaitAsync(Bound); }
    });
}
