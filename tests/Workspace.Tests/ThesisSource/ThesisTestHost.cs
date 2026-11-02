using System.Text.Json.Nodes;
using Client.Analysis;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Transfer;
using Xunit;
using BrowserIo = global::Workspace.Tests.HistoryComparison.HistoryTestHost.BrowserIo;
using Hold = global::Workspace.Tests.HistoryComparison.HistoryTestHost.Hold;

namespace Workspace.Tests.ThesisSource;

// Reuses the existing browser-primitive repository fixture with REAL
// WorkspaceStorage, JsWorkspaceLocks, coordinator, capture and Rust adapter.
// Modelled IO/interop is not physical worker/browser/offline acceptance.
internal sealed class ThesisTestHost : IAsyncDisposable
{
    internal static readonly TimeSpan Bound=TimeSpan.FromSeconds(10);
    internal static TaskCompletionSource<bool> Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal BrowserIo Io=new();internal WorkspaceStorage Repo=null!;internal JsWorkspaceLocks Locks=null!;
    internal WorkspaceCoordinator Workspace=null!;internal RecordingAnalyzer Analyzer=null!;internal AnalysisCoordinator Analysis=null!;
    internal KnuProtocolTests.FixtureJs Js=null!;internal FileId A,B;
    private Hold? _readHold;private int _initialCommits;
    internal int Commits=>Io.Commits-_initialCommits;
    internal void HoldRead()=>_readHold=Io.HoldNext("readContent");
    internal Task ReadReached=>_readHold?.Reached.Task??throw new InvalidOperationException("No held read.");
    internal AnalysisSelection Selection(FileId? root=null,AnalysisWorkType type=AnalysisWorkType.Unspecified)=>AnalysisSelection.KnuWithThesis(type,(root??A).ToString());
    internal static async Task<ThesisTestHost> Open(bool readOnly=false)
    {
        var h=new ThesisTestHost();var fixture=ThesisFixture.Resource("lifecycle-wasm");var packed=Convert.FromHexString(fixture["packedUtf8Hex"]!.GetValue<string>());
        h.Repo=new(h.Io);h.Locks=new(h.Io);h.Workspace=new(h.Repo,h.Locks,new HistoryService(h.Repo));
        var project=await h.Repo.CreateProjectAsync("synthetic thesis",default);await h.Workspace.OpenAsync(project.Id,default);
        var ids=new List<FileId>();
        foreach(var descriptor in fixture["files"]!.AsArray())
        {
            var b=packed.AsSpan(descriptor!["byteOffset"]!.GetValue<int>(),descriptor["byteLength"]!.GetValue<int>()).ToArray();
            Assert.True(h.Workspace.CreateFileFromBytes(descriptor["relativePath"]!.GetValue<string>(),b));ids.Add(h.Workspace.ActiveFileId!.Value);
        }
        h.A=ids[0];h.B=ids[1];Assert.Equal(CommitStatus.Saved,(await h.Workspace.SaveAsync(default)).Status);
        if(readOnly){h.Io.AllowAcquire=false;await h.Workspace.OpenAsync(project.Id,default);Assert.True(h.Workspace.IsReadOnly);}
        h._initialCommits=h.Io.Commits;
        h.Js=new KnuProtocolTests.FixtureJs((meta,rid)=>{
            var p=JsonNode.Parse(meta)!["payload"]!;
            var snapshot=h.Analyzer.Captured.Single(s=>s.Id.ToString()==p["snapshotId"]!.GetValue<string>());
            var root=snapshot.Files.Single(f=>f.FileId.ToString()==p["referenceConfiguration"]!["rootFileId"]!.GetValue<string>());
            var originalRoot=fixture["files"]!.AsArray().Single(d=>d!["relativePath"]!.GetValue<string>()==root.RelativePath)!["fileId"]!.GetValue<string>();
            var variant=fixture["variants"]!.AsArray().Single(v=>v!["rootFileId"]!.GetValue<string>()==originalRoot
                && v["workType"]!.GetValue<string>()==p["configuration"]!["workType"]!.GetValue<string>())!;
            var map=fixture["files"]!.AsArray().ToDictionary(d=>d!["fileId"]!.GetValue<string>(),
                d=>snapshot.Files.Single(f=>f.RelativePath==d!["relativePath"]!.GetValue<string>()).FileId.ToString(),StringComparer.Ordinal);
            return ThesisFixture.Rebind(variant["response"]!.DeepClone().AsObject(),snapshot,rid,map);
        });
        h.Analyzer=new(new RustSourceAnalyzer(h.Js));h.Analysis=new(h.Workspace,h.Analyzer,delayAsync:(_,ct)=>Task.Delay(Timeout.Infinite,ct));return h;
    }
    internal async Task<AnalysisReport> Fresh(Action change)
    {
        var t=new TaskCompletionSource<AnalysisReport>(TaskCreationOptions.RunContinuationsAsynchronously);var previous=(Analysis.State as AnalysisState.Completed)?.Report;
        void Changed(AnalysisState s){if(s is AnalysisState.Completed c&&!ReferenceEquals(c.Report,previous))t.TrySetResult(c.Report);}
        Analysis.Changed+=Changed;try{change();return await t.Task.WaitAsync(Bound);}finally{Analysis.Changed-=Changed;}
    }
    internal void Release(){foreach(var hold in Io.Holds)hold.Release.TrySetResult();Js.ImportGate?.TrySetResult(true);Js.ResponseGate?.TrySetResult(true);Js.EpochGate?.TrySetResult(true);}
    public async ValueTask DisposeAsync(){Release();await Analysis.DisposeAsync();await Analyzer.DisposeAsync();await Workspace.DisposeAsync();await Locks.DisposeAsync();}
    internal sealed class RecordingAnalyzer(ISourceAnalyzer inner):ISourceAnalyzer
    {
        internal List<AnalysisSnapshot> Captured {get;}=[];
        public Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot s,CancellationToken ct)
        {
            Captured.Add(s);
            // Only thesis1.5 actual replies exist in this lifecycle fixture;
            // legacy evaluation itself is covered by immutable existing goldens.
            return AnalysisCatalog.IsThesis(s.Profile)?inner.AnalyzeAsync(s,ct):Task.FromResult<AnalysisJobResult>(new AnalysisJobResult.Cancelled("not-under-test",7));
        }
        public Task<bool> IsCurrentWorkerEpochAsync(long epoch,CancellationToken ct)=>inner.IsCurrentWorkerEpochAsync(epoch,ct);
        public ValueTask DisposeAsync()=>inner.DisposeAsync();
    }
}
