using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Microsoft.JSInterop;
using Xunit;
using BrowserIo = global::Workspace.Tests.HistoryComparison.HistoryTestHost.BrowserIo;

namespace Workspace.Tests.ThesisSource;

// Real Rust public-entry results over unchanged CRLF/Unicode source, with only
// transport and browser storage modeled. Initial bridge epoch 0 is a valid
// generation; its exact currentness must still be checked after the await.
public sealed class InitialWorkerEpochTests
{
    public static IEnumerable<object[]> AdapterCases =>
        from version in new[] { "1.5.0", "1.6.0" }
        from order in new[] { "root-first", "root-last" }
        from epoch in new long[] { 0, 1 }
        select new object[] { version, order, epoch };

    [Theory, MemberData(nameof(AdapterCases))]
    public async Task Real_Rust_CRLF_capture_accepts_initial_and_later_epoch_in_either_file_order(string version, string order, long epoch)
    {
        var fixture = Load(version, order); var snapshot = fixture.Snapshot();
        var js = new EpochJs((_, id) => fixture.Reply(snapshot, id), epoch);
        await using var adapter = new RustSourceAnalyzer(js);
        var result = Assert.IsType<AnalysisJobResult.Completed>(await adapter.AnalyzeAsync(snapshot, default));
        Assert.Equal(epoch, result.WorkerEpoch);
        ThesisValidation.ValidateCapturedResult(result.Result, snapshot);
        Assert.True(await adapter.IsCurrentWorkerEpochAsync(epoch, default));
        Assert.Equal(fixture.Packed, js.Packed);
        AssertOriginalInputs(snapshot);
        Assert.Equal(order == "root-first" ? 0 : 1,
            Array.FindIndex(snapshot.Files.ToArray(), f => f.FileId.ToString() == snapshot.ReferenceConfiguration!.RootFileId));
    }

    [Theory]
    [InlineData("1.5.0", 0L)][InlineData("1.5.0", 1L)]
    [InlineData("1.6.0", 0L)][InlineData("1.6.0", 1L)]
    public async Task Coordinator_publishes_original_caption_capture_and_navigation_at_current_epoch(string version, long epoch)
    {
        await using var h = await Host.Open(version, epoch);
        var state = await h.Run();
        var report = Assert.IsType<AnalysisState.Completed>(state).Report;
        Assert.Equal(epoch, report.WorkerEpoch); Assert.Equal(version, report.Result.Profile.Version);
        Assert.Equal("Orbit comparison", report.Result.Thesis!.Events[0].Text);
        Assert.Equal(new AnalysisSpanDto(187, 203), report.Result.Thesis.Events[0].Span);
        AssertOriginalInputs(report.Snapshot);
        Assert.Equal(report.Snapshot.ReferenceConfiguration!.RootFileId, report.Result.Thesis.RootFileId);
        var navigator = new SourceNavigator(h.Analysis, h.Workspace, h.Analyzer);
        Assert.True((await navigator.VerifyCurrentAsync(report, default)).Ok);
        h.Js.CurrentEpoch = epoch + 1;
        Assert.False((await navigator.VerifyCurrentAsync(report, default)).Ok);
        Assert.True(h.Js.EpochQueries >= 3); // publication, current, reset
    }

    [Theory]
    [InlineData("1.5.0", -1L)][InlineData("1.6.0", -1L)]
    [InlineData("1.5.0", 9007199254740992L)][InlineData("1.6.0", 9007199254740992L)]
    public async Task Invalid_epoch_still_refuses_before_publication(string version, long epoch)
    {
        await using var h = await Host.Open(version, epoch);
        var state = Assert.IsType<AnalysisState.Failed>(await h.Run());
        Assert.Equal("InvalidResponse", state.Error.Code); Assert.Equal(0, h.Js.EpochQueries);
    }

    [Theory]
    [InlineData("1.5.0", 0L)][InlineData("1.6.0", 0L)]
    [InlineData("1.5.0", 1L)][InlineData("1.6.0", 1L)]
    public async Task Worker_replacement_during_publish_await_never_publishes(string version, long epoch)
    {
        await using var h = await Host.Open(version, epoch);
        h.Js.EpochRelease = Gate();
        var pending = h.Run(); await h.Js.EpochReached.Task.WaitAsync(Bound);
        h.Js.CurrentEpoch = epoch + 1; h.Js.EpochRelease.TrySetResult(true);
        Assert.IsType<AnalysisState.Superseded>(await pending);
        Assert.Equal(1, h.Js.EpochQueries);
    }

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ThesisFixture Load(string version, string order) => new(
        ThesisFixture.Resource("initial-worker-epoch-native-vectors")["vectors"]!.AsArray()
            .Single(v => v!["id"]!.GetValue<string>() == version + "-" + order)!.DeepClone().AsObject());
    private static void AssertOriginalInputs(AnalysisSnapshot snapshot)
    {
        var main = snapshot.Files.Single(f => f.RelativePath == "тези/my thesis.tex");
        var chapter = snapshot.Files.Single(f => f.RelativePath == "chapter.tex");
        Assert.Equal(293, main.ByteLength); Assert.Equal(121, chapter.ByteLength);
        Assert.Equal("d13fafa67790ba9f8d63353b5ee277b80ed50601c52880d7a0ef2a7cef85716a", main.Sha256.ToString());
        Assert.Equal("b21a3969ef92adca041c6d4edfc9197fa97ae05bdb56ce80ba558cacae5c583a", chapter.Sha256.ToString());
        Assert.False(main.HasBom); Assert.False(chapter.HasBom);
    }

    private sealed class Host : IAsyncDisposable
    {
        internal WorkspaceCoordinator Workspace = null!;
        internal AnalysisCoordinator Analysis = null!;
        internal RustSourceAnalyzer Analyzer = null!;
        internal EpochJs Js = null!;
        private JsWorkspaceLocks _locks = null!;
        private ThesisTestHost.RecordingAnalyzer _recording = null!;
        private AnalysisSelection _selection;

        internal static async Task<Host> Open(string version, long epoch)
        {
            var h = new Host(); var fixture = Load(version, "root-last");
            var io = new BrowserIo(); var repository = new WorkspaceStorage(io); h._locks = new(io);
            h.Workspace = new(repository, h._locks, new HistoryService(repository));
            var project = await repository.CreateProjectAsync("original synthetic CRLF capture", default);
            await h.Workspace.OpenAsync(project.Id, default);
            FileId root = default;
            foreach (var descriptor in fixture.Data["request"]!["payload"]!["files"]!.AsArray())
            {
                var path = descriptor!["relativePath"]!.GetValue<string>();
                var bytes = fixture.Packed.AsSpan(descriptor["byteOffset"]!.GetValue<int>(), descriptor["byteLength"]!.GetValue<int>()).ToArray();
                Assert.True(h.Workspace.CreateFileFromBytes(path, bytes));
                if (path == "тези/my thesis.tex") root = h.Workspace.ActiveFileId!.Value;
            }
            h._selection = new(new(AnalysisCatalog.KnuId, version), new(AnalysisWorkType.BachelorQualification), new(root.ToString()));
            h.Js = new EpochJs((metadata, id) =>
            {
                var p = JsonNode.Parse(metadata)!["payload"]!;
                var snapshot = h._recording.Captured.Single(s => s.Id.ToString() == p["snapshotId"]!.GetValue<string>());
                // Opaque IDs are rebound; source facts, events, spans, rules and
                // original file bytes remain those of the genuine Rust call.
                return fixture.Reply(snapshot, id);
            }, epoch);
            h.Analyzer = new(h.Js); h._recording = new(h.Analyzer);
            h.Analysis = new(h.Workspace, h._recording, delayAsync: (_, ct) => Task.Delay(Timeout.Infinite, ct));
            return h;
        }

        internal async Task<AnalysisState> Run()
        {
            var done = new TaskCompletionSource<AnalysisState>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(AnalysisState s)
            { if (s is AnalysisState.Completed or AnalysisState.Failed or AnalysisState.Superseded) done.TrySetResult(s); }
            Analysis.Changed += Changed;
            try { Analysis.SetSelection(_selection); return await done.Task.WaitAsync(Bound); }
            finally { Analysis.Changed -= Changed; }
        }
        public async ValueTask DisposeAsync()
        {
            Js.EpochRelease?.TrySetResult(true);
            await Analysis.DisposeAsync(); await Analyzer.DisposeAsync(); await Workspace.DisposeAsync(); await _locks.DisposeAsync();
        }
    }

    private sealed class EpochJs(Func<string, string, string> reply, long epoch) : IJSRuntime, IJSObjectReference
    {
        private readonly long _responseEpoch = epoch;
        internal long CurrentEpoch = epoch;
        internal byte[]? Packed;
        internal int EpochQueries;
        internal TaskCompletionSource<bool> EpochReached = Gate();
        internal TaskCompletionSource<bool>? EpochRelease;
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, default, args);
        public async ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args)
        {
            if (id == "import") return (T)(object)this;
            if (id == "analyzeSource")
            {
                Packed = ((byte[])args![1]!).ToArray(); var metadata = (string)args[0]!;
                string request = JsonNode.Parse(metadata)!["requestId"]!.GetValue<string>();
                return (T)(object)new AnalysisBridgeResponse(reply(metadata, request), _responseEpoch);
            }
            if (id == "getWorkerEpoch")
            {
                EpochQueries++; EpochReached.TrySetResult(true);
                if (EpochRelease is { } gate) await gate.Task;
                return (T)(object)CurrentEpoch;
            }
            if (id is "cancelSource" or "dispose") return default!;
            throw new InvalidOperationException(id);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
