using System.Globalization;
using System.Text.Json.Nodes;
using Client.Compilation.History;
using Client.Compilation.History.Index;
using Client.Workspace.Domain;
using Xunit;
using IndexService = Client.Compilation.History.Index.CompileHistoryIndex;

namespace Workspace.Tests.CompileHistoryIndex;

// Discovery POINTER fixtures only, never fake accepted compile/artifact receipts.
public sealed class HistoryIndexTests
{
    private static HistoryIndexBudget Budget(int count = 64, int deadline = 10000) => new(count, 65536, deadline);
    private static HistoricalArtifactKey Key(ProjectId p) => new(Guid.NewGuid(), p, new(new string('a', 64)), new(new string('b', 64)));
    [Fact]
    public async Task Reopen_roundtrip_idempotent_retry_and_conflicting_record_are_explicit()
    {
        var p = ProjectId.New(); var key = Key(p); var shared = new State();
        await using (var service = new IndexService(new Backend(shared)))
        {
            Assert.Equal(HistoryIndexStatus.Missing, (await service.ListAsync(p, Budget())).Status);
            var saved = await service.AppendPersistedAsync(p, key, 0, Budget()); Assert.Equal(HistoryIndexStatus.Saved, saved.Status);
            Assert.Equal(1, saved.Snapshot!.Revision); Assert.Equal(key, Assert.Single(saved.Snapshot.Entries));
            Assert.Equal(HistoryIndexStatus.AlreadyIndexed, (await service.AppendPersistedAsync(p, key, 0, Budget())).Status);
            Assert.Equal(HistoryIndexStatus.RecordConflict, (await service.AppendPersistedAsync(p, key with { ContentDigest = new(new string('c', 64)) }, 1, Budget())).Status);
        }
        await using var reopened = new IndexService(new Backend(shared)); var read = await reopened.ListAsync(p, Budget());
        Assert.Equal(HistoryIndexStatus.Ready, read.Status); Assert.Equal(key, Assert.Single(read.Snapshot!.Entries));
        Assert.Null(typeof(HistoryIndexSnapshot).GetProperty("IsCurrent")); Assert.Null(typeof(HistoryIndexSnapshot).GetProperty("AcceptedOutput"));
    }
    [Fact]
    public async Task Wrong_project_is_rejected_before_IO_and_full_index_never_evicts()
    {
        var p = ProjectId.New(); var backend = new Backend(new()); await using var service = new IndexService(backend);
        Assert.Equal(HistoryIndexStatus.WrongProject, (await service.AppendPersistedAsync(p, Key(ProjectId.New()), 0, Budget())).Status); Assert.Equal(0, backend.Reads);
        var first = Key(p); await service.AppendPersistedAsync(p, first, 0, Budget(1));
        Assert.Equal(HistoryIndexStatus.Full, (await service.AppendPersistedAsync(p, Key(p), 1, Budget(1))).Status);
        Assert.Equal(HistoryIndexStatus.AlreadyIndexed, (await service.AppendPersistedAsync(p, first, 0, Budget(1))).Status);
        Assert.Equal(first, Assert.Single((await service.ListAsync(p, Budget(1))).Snapshot!.Entries)); Assert.Equal(1, backend.Writes);
    }
    [Theory]
    [InlineData("unknown", HistoryIndexStatus.Corrupt)] [InlineData("duplicate", HistoryIndexStatus.Corrupt)]
    [InlineData("version", HistoryIndexStatus.UnsupportedSchema)] [InlineData("duplicate-key", HistoryIndexStatus.Corrupt)]
    [InlineData("hash", HistoryIndexStatus.Corrupt)] [InlineData("project", HistoryIndexStatus.WrongProject)]
    [InlineData("revision", HistoryIndexStatus.Corrupt)] [InlineData("token", HistoryIndexStatus.Corrupt)]
    [InlineData("extra-entry", HistoryIndexStatus.Corrupt)] [InlineData("huge", HistoryIndexStatus.LimitExceeded)]
    public async Task Strict_untrusted_metadata_never_resets_or_publishes(string mode, HistoryIndexStatus expected)
    {
        var p = ProjectId.New(); var shared = new State(); var io = new Backend(shared); await using var service = new IndexService(io);
        await service.AppendPersistedAsync(p, Key(p), 0, Budget()); var raw = shared.Rows[p]; var node = JsonNode.Parse(raw.Json!)!;
        switch (mode)
        {
            case "unknown": node["current"] = true; break;
            case "version": node["version"] = 2; break;
            case "duplicate-key": node["entries"]!.AsArray().Add(node["entries"]![0]!.DeepClone()); break;
            case "hash": node["entries"]![0]!["contentDigest"] = "BAD"; break;
            case "project": node["projectId"] = Guid.NewGuid().ToString("N"); break;
            case "revision": node["revision"] = 2; break;
            case "extra-entry": node["entries"]![0]!["trusted"] = true; break;
        }
        string json = mode == "duplicate" ? "{\"version\":1," + raw.Json![1..] : mode == "huge" ? new string('x', 65537) : node.ToJsonString();
        shared.Rows[p] = new(mode == "token" ? "01" : "1", json); int writes = io.Writes;
        Assert.Equal(expected, (await service.ListAsync(p, Budget())).Status);
        Assert.Equal(expected, (await service.AppendPersistedAsync(p, Key(p), 1, Budget())).Status);
        Assert.Equal(writes, io.Writes); Assert.Equal(json, shared.Rows[p].Json);
    }
    [Theory] [InlineData(HistoryIndexStatus.QuotaExceeded)] [InlineData(HistoryIndexStatus.PermissionDenied)] [InlineData(HistoryIndexStatus.Unavailable)]
    public async Task Known_transaction_failure_retains_previous_index(HistoryIndexStatus status)
    {
        var p = ProjectId.New(); var state = new State(); var io = new Backend(state); await using var service = new IndexService(io);
        await service.AppendPersistedAsync(p, Key(p), 0, Budget()); var previous = state.Rows[p]; io.Failure = status;
        Assert.Equal(status, (await service.AppendPersistedAsync(p, Key(p), 1, Budget())).Status); Assert.Equal(previous, state.Rows[p]);
        Assert.Equal(HistoryIndexStatus.Ready, (await service.ListAsync(p, Budget())).Status);
    }
    [Fact]
    public async Task CAS_race_does_not_lose_other_writer_and_requires_explicit_rebase()
    {
        var p = ProjectId.New(); var state = new State(); var io1 = new Backend(state) { HoldRead = true }; var io2 = new Backend(state) { HoldRead = true };
        await using var a = new IndexService(io1); await using var b = new IndexService(io2); var k1 = Key(p); var k2 = Key(p);
        var first = a.AppendPersistedAsync(p, k1, 0, Budget()); var second = b.AppendPersistedAsync(p, k2, 0, Budget());
        try { await Task.WhenAll(io1.Entered.Task, io2.Entered.Task).WaitAsync(TimeSpan.FromSeconds(2)); Assert.False(io1.Release.Task.IsCompleted); Assert.False(io2.Release.Task.IsCompleted); }
        finally { io1.Release.TrySetResult(true); io2.Release.TrySetResult(true); }
        var results = await Task.WhenAll(first, second); Assert.Equal(1, results.Count(r => r.Status == HistoryIndexStatus.Saved)); Assert.Equal(1, results.Count(r => r.Status == HistoryIndexStatus.Conflict));
        var listed = await a.ListAsync(p, Budget()); Assert.Single(listed.Snapshot!.Entries);
        var missing = listed.Snapshot.Entries.Contains(k1) ? k2 : k1;
        Assert.Equal(HistoryIndexStatus.Saved, (await a.AppendPersistedAsync(p, missing, 1, Budget())).Status);
        Assert.Equal(2, (await a.ListAsync(p, Budget())).Snapshot!.Entries.Length);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Actual_read_or_committed_write_retains_admission_after_cancel(bool write)
    {
        var p = ProjectId.New(); var state = new State(); var io = new Backend(state) { HoldRead = !write, HoldWrite = write };
        var service = new IndexService(io); using var caller = new CancellationTokenSource(); var key = Key(p);
        Task<HistoryIndexResult> operation = write ? service.AppendPersistedAsync(p, key, 0, Budget(), caller.Token) : service.ListAsync(p, Budget(), caller.Token); Exception? primary = null;
        try
        {
            await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); caller.Cancel(); var logical = await operation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(write ? HistoryIndexStatus.Uncertain : HistoryIndexStatus.Cancelled, logical.Status); Assert.Equal(write, logical.WriteMayHaveCommitted);
            Assert.False(io.Release.Task.IsCompleted); Assert.False(service.WhenSettled.IsCompleted);
            Assert.Equal(HistoryIndexStatus.Busy, (await service.ListAsync(p, Budget())).Status); Assert.Equal(1, io.Reads);
            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)); Assert.Equal(0, io.Disposals);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            io.Release.TrySetResult(true);
            try { await service.WhenSettled.WaitAsync(TimeSpan.FromSeconds(2)); await service.DisposeAsync(); }
            catch (Exception cleanup) when (primary is not null) { throw new AggregateException(primary, cleanup); }
        }
        Assert.Equal(1, io.Disposals);
        await using var fresh = new IndexService(new Backend(state)); var read = await fresh.ListAsync(p, Budget());
        Assert.Equal(write ? HistoryIndexStatus.Ready : HistoryIndexStatus.Missing, read.Status);
        if (write) Assert.Equal(HistoryIndexStatus.AlreadyIndexed, (await fresh.AppendPersistedAsync(p, key, 0, Budget())).Status);
    }
    [Fact]
    public async Task Deadline_and_foreign_cancellation_are_distinct_and_actual_work_is_not_freed()
    {
        var p = ProjectId.New(); var io = new Backend(new()) { HoldRead = true }; await using var service = new IndexService(io);
        try { var first = service.ListAsync(p, Budget(deadline: 100)); await io.Entered.Task; Assert.Equal(HistoryIndexStatus.TimedOut, (await first).Status); Assert.False(io.Release.Task.IsCompleted); Assert.Equal(HistoryIndexStatus.Busy, (await service.ListAsync(p, Budget())).Status); }
        finally { io.Release.TrySetResult(true); await service.WhenSettled.WaitAsync(TimeSpan.FromSeconds(2)); }
        io.Foreign = true; Assert.Equal(HistoryIndexStatus.StorageFailure, (await service.ListAsync(p, Budget())).Status);
    }
    [Fact]
    public async Task Metadata_byte_limit_precedes_write_and_long_revision_exhaustion_is_explicit()
    {
        var p = ProjectId.New(); var state = new State(); var io = new Backend(state); await using var service = new IndexService(io);
        Assert.Equal(HistoryIndexStatus.LimitExceeded, (await service.AppendPersistedAsync(p, Key(p), 0, new(64, 8, 1000))).Status); Assert.Equal(0, io.Writes);
        var key = Key(p); state.Rows[p] = new(long.MaxValue.ToString(CultureInfo.InvariantCulture), HistoryIndexCodec.Encode(new(p, long.MaxValue, [key]), Budget()));
        Assert.Equal(HistoryIndexStatus.LimitExceeded, (await service.AppendPersistedAsync(p, Key(p), long.MaxValue, Budget())).Status);
        Assert.Equal(HistoryIndexStatus.AlreadyIndexed, (await service.AppendPersistedAsync(p, key, long.MaxValue, Budget())).Status);
    }
    private sealed class State { public readonly Dictionary<ProjectId, HistoryIndexRead> Rows = []; }
    private sealed class Backend(State state) : IHistoryIndexBackend
    {
        public int Reads, Writes, Disposals; public bool HoldRead, HoldWrite, Foreign; public HistoryIndexStatus? Failure;
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HistoryIndexRead> ReadAsync(ProjectId project, int max)
        {
            Reads++; if (Foreign) throw new OperationCanceledException("foreign TEST", new CancellationToken(true)); HistoryIndexRead read;
            lock (state) read = state.Rows.GetValueOrDefault(project) ?? new("0", null);
            if (HoldRead) { Entered.TrySetResult(true); await Release.Task; } return read;
        }
        public async Task<HistoryIndexCas> CompareExchangeAsync(ProjectId project, long expected, long next, string json, int max)
        {
            Writes++; if (Failure is { } failure) return new(failure);
            lock (state)
            {
                if ((state.Rows.GetValueOrDefault(project)?.Revision ?? "0") != expected.ToString(CultureInfo.InvariantCulture)) return new(HistoryIndexStatus.Conflict);
                state.Rows[project] = new(next.ToString(CultureInfo.InvariantCulture), json);
            }
            if (HoldWrite) { Entered.TrySetResult(true); await Release.Task; } return new(HistoryIndexStatus.Saved);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
