using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Review;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.HistoryComparison;

// REAL WorkspaceStorage protocol/verification, JsWorkspaceLocks, coordinator,
// capture, preparer and review. Only the browser's primitive IO/transactions are
// modeled here; no native/PDF/browser/offline acceptance claim.
internal sealed class HistoryTestHost : IAsyncDisposable
{
    internal static readonly CancellationToken None = CancellationToken.None;
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    internal readonly BrowserIo Io = new();
    internal WorkspaceStorage Repo = null!;
    internal JsWorkspaceLocks Locks = null!;
    internal WorkspaceCoordinator Workspace = null!;
    internal HistoryService History = null!;
    internal HistoryComparisonSession Session = null!;
    internal AnalysisCoordinator Analysis = null!;
    internal ChangeReviewService Review = null!;
    internal FileId Main, Other, Binary;
    internal RevisionId Old, Current;
    internal byte[] Original = [239, 187, 191, .. Encoding.UTF8.GetBytes("Україна🙂 e\u0301\r\nold\r\n")];
    internal byte[] OtherBytes = Encoding.UTF8.GetBytes("unrelated\r\n");
    internal byte[] BinaryBytes = [0, 255, 9, 128];
    internal static async Task<HistoryTestHost> Open(bool readOnly = false)
    {
        var h = new HistoryTestHost(); h.Repo = new(h.Io); h.Locks = new(h.Io); h.History = new(h.Repo);
        h.Workspace = new(h.Repo, h.Locks, h.History);
        var p = await h.Repo.CreateProjectAsync("synthetic history", None); await h.Workspace.OpenAsync(p.Id, None);
        Assert.True(h.Workspace.CreateFileFromBytes("теза/main.tex", h.Original)); h.Main = h.Workspace.ActiveFileId!.Value;
        Assert.True(h.Workspace.CreateFileFromBytes("other.tex", h.OtherBytes)); h.Other = h.Workspace.ActiveFileId!.Value;
        Assert.True(h.Workspace.CreateFileFromBytes("asset.bin", h.BinaryBytes)); h.Binary = h.Workspace.ActiveFileId!.Value;
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(None)).Status); h.Old = h.Workspace.CurrentRevision!.Id;
        Assert.True(h.Workspace.EditText(h.Main, "Україна🙂 e\u0301\nnew\n"));
        Assert.Equal(CommitStatus.Saved, (await h.Workspace.SaveAsync(None)).Status); h.Current = h.Workspace.CurrentRevision!.Id;
        if (readOnly) { h.Io.AllowAcquire = false; await h.Workspace.OpenAsync(p.Id, None); Assert.True(h.Workspace.IsReadOnly); }
        h.Session = new(h.Workspace, h.History);
        var analyzer = new UnusedAnalyzer();
        h.Analysis = new(h.Workspace, analyzer, delayAsync: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        h.Review = new(h.Workspace, h.Analysis, new SourceNavigator(h.Analysis, h.Workspace, analyzer));
        return h;
    }
    internal WorkingFile File(FileId id) => Workspace.Files.Single(f => f.Id == id);
    internal Task<byte[]> Bytes(FileId id) => Workspace.GetFileBytesAsync(File(id), None);
    internal string State() => $"{Workspace.Project}/{Workspace.CurrentRevision?.Id}/{Workspace.SessionId}/{Workspace.WorkingGeneration}/{Workspace.ActiveFileId}/{Workspace.SavePhase}/{Workspace.IsDirty}/"
        + string.Join("|", Workspace.Files.Select(f => $"{f.Id}:{f.RelativePath}:{f.Content}:{f.HasBom}:{f.DirtyContent}:{f.DirtyBinary}:{f.TreeChanged}:{f.IsNew}:{f.Deleted}:{f.DurableHash}:{f.DurableLength}:{f.PendingTextBytes?.Hash}"));
    internal async Task<HistoryComparisonSnapshot> Compare() => Assert.IsType<HistoryComparisonSnapshot>((await Session.CompareWorkingAsync(Old, None)).Comparison);
    internal async Task<HistoryTextRestoreProposal> Proposal(params FileId[] ids)
    { await Compare(); return Assert.IsType<HistoryTextRestoreProposal>((await HistoryTextRestoreProposal.CreateAsync(Session, ids, None)).Proposal); }
    public async ValueTask DisposeAsync()
    {
        foreach (var hold in Io.Holds) hold.Release.TrySetResult();
        Session.Dispose(); await Review.DisposeAsync(); await Analysis.DisposeAsync(); await Workspace.DisposeAsync(); await Locks.DisposeAsync();
    }
    private sealed class UnusedAnalyzer : ISourceAnalyzer
    {
        public Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot snapshot, CancellationToken ct)
            => Task.FromResult<AnalysisJobResult>(new AnalysisJobResult.Cancelled("synthetic-no-analysis", 1));
        public Task<bool> IsCurrentWorkerEpochAsync(long epoch, CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class Hold(string operation, string? key)
    {
        internal string Operation = operation;
        internal string? Key = key;
        internal bool Used;
        internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Wait() => Reached.Task.WaitAsync(Bound);
    }
    internal sealed class BrowserIo : IJSRuntime, IJSObjectReference
    {
        internal readonly Dictionary<string, JsonObject> Projects = new(), Revisions = new();
        internal readonly List<JsonObject> Checkpoints = [];
        internal readonly Dictionary<string, byte[]> Content = new();
        internal readonly List<Hold> Holds = [];
        internal readonly List<string> Calls = [];
        internal bool AllowAcquire = true, Owned = true, FailWrites;
        internal int Commits, Reads;
        internal Hold HoldNext(string op, string? key = null) { var hold = new Hold(op, key); Holds.Add(hold); return hold; }
        internal string Head(ProjectId project) => Projects[project.ToString()]["HeadRevisionId"]!.GetValue<string>();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            if (identifier == "import") return (TValue)(object)this;
            Calls.Add(identifier); var a = args ?? [];
            string Key(int i) => a[i]!.ToString()!;
            var hold = Holds.FirstOrDefault(h => !h.Used && h.Operation == identifier && (h.Key is null || h.Key == Key(0)));
            if (hold is not null) { hold.Used = true; hold.Reached.TrySetResult(); await hold.Release.Task; } // actual noncooperative IO
            object? value;
            switch (identifier)
            {
                case "capabilities": value = new { Opfs = true, IndexedDB = true, WebLocks = true, Persist = true }; break;
                case "tryAcquireLock": value = AllowAcquire ? "history-writer" : null; break;
                case "waitForLock": value = "history-writer"; break;
                case "releaseLock": value = true; break;
                case "ownsLock": value = Owned && AllowAcquire; break;
                case "lockHeld": value = true; break;
                case "putProject":
                    var project = JsonSerializer.SerializeToNode(a[0])!.AsObject(); Projects[project["Id"]!.GetValue<string>()] = project; value = null; break;
                case "getProject": value = Projects.GetValueOrDefault(Key(0)); break;
                case "getRevision": value = Revisions.GetValueOrDefault(Key(0)); break;
                case "listCheckpoints": value = Checkpoints.Where(c => c["ProjectId"]!.GetValue<string>() == Key(0)).ToArray(); break;
                case "readContent": Reads++; return (TValue)(object)Content[Key(0)]; // deliberate alias; consumers must own it
                case "writeContent":
                    if (FailWrites) throw new JSException("QuotaExceededError: synthetic quota");
                    var bytes = ((byte[])a[1]!).ToArray(); Content[Key(0)] = bytes;
                    value = new { Hash = Key(0), ByteLength = bytes.LongLength, Verified = Hash(bytes) == Key(0) }; break;
                case "verifyContent":
                    bool exists = Content.TryGetValue(Key(0), out var stored);
                    value = new { Exists = exists, Length = stored?.LongLength ?? 0, HashMatch = exists && Hash(stored!) == Key(0) }; break;
                case "commitBatch":
                    Commits++; var p = Projects[Key(0)]; var head = p["HeadRevisionId"]?.GetValue<string>();
                    if (head != a[1]?.ToString()) { value = new { Ok = false, ErrorCode = "RevisionConflict", ActualHead = head }; break; }
                    foreach (var revision in JsonSerializer.SerializeToNode(a[3])!.AsArray())
                    { var r = revision!.DeepClone().AsObject(); Revisions[r["Id"]!.GetValue<string>()] = r; }
                    if (a[4] is not null) Checkpoints.Add(JsonSerializer.SerializeToNode(a[4])!.AsObject());
                    p["HeadRevisionId"] = Key(2); value = new { Ok = true }; break;
                default: throw new InvalidOperationException("Unexpected modeled browser primitive: " + identifier);
            }
            return JsonSerializer.Deserialize<TValue>(JsonSerializer.Serialize(value))!;
        }
        private static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
