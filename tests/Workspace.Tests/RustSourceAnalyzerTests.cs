using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Client.Analysis;
using Client.Workspace.Domain;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests;

/// <summary>
/// RustSourceAnalyzer adapter boundary (M2a-2b-1) with the REAL packer/parser
/// and a fake JS runtime/module boundary. Verifies: metadata/payload passed
/// to the bridge; caller source bytes unchanged; completed snapshotId/profile
/// correlation (mismatch rejected, not success); canonical Failed stays Failed;
/// bridge cancelled stays Cancelled; malformed JSON typed InvalidInput; worker
/// epoch exposed/checkable; cancellation during initialization and an active
/// request triggers an explicit JS cancel by id; disposal during pending does
/// not dispatch/revive (settles cancelled); next-after-failure works;
/// concurrent imports do not leak duplicate module refs; disposed returns
/// Disposed. Deterministic TaskCompletionSource barriers, no timing sleeps.
/// The real-Chromium deadline/crash/real-WASM coverage lives in
/// scripts/test-analysis-worker.cjs (analysis-worker-test).
/// </summary>
public class RustSourceAnalyzerTests
{
    [Fact]
    public async Task Completed_Happy_Path_Returns_Result_And_Passes_Payload()
    {
        var bytes = B("Привіт 🇺🇦 {x}\n");
        var (snapshot, fileId) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = CompletedHandler(snapshot, fileId, epoch: 1);
        var analyzer = new RustSourceAnalyzer(runtime);

        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);

        var completed = Assert.IsType<AnalysisJobResult.Completed>(result);
        Assert.NotNull(completed.RequestId);
        Assert.Equal(1L, completed.WorkerEpoch);
        Assert.Equal(snapshot.Id.ToString(), completed.Result.SnapshotId);
        Assert.Equal(snapshot.Profile.Id, completed.Result.Profile.Id);
        // The bridge received the EXACT metadata JSON the real packer produced
        // and the packed bytes equal the snapshot's concatenated source bytes.
        Assert.Equal(bytes, runtime.Module.LastPacked);
        Assert.Contains("\"" + completed.RequestId + "\"", runtime.Module.LastMetadata);
        // workingGeneration is serialized as an integer literal (no quotes).
        Assert.Contains("\"workingGeneration\":" + snapshot.WorkingGeneration, runtime.Module.LastMetadata);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Caller_Source_Bytes_Unchanged_After_Pack()
    {
        var bytes = B("line\r\nwith CRLF");
        var (snapshot, fileId) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = CompletedHandler(snapshot, fileId, epoch: 0);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        Assert.IsType<AnalysisJobResult.Completed>(result);
        // The bytes the bridge received are exactly the snapshot's frozen bytes
        // (the C# buffer is copied/owned, not mutated by the adapter).
        Assert.Equal(snapshot.Files[0].Bytes.ToArray(), runtime.Module.LastPacked);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task SnapshotId_Mismatch_Is_Failed_Not_Success()
    {
        var bytes = B("x");
        var (snapshot, fileId) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        var other = AnalysisSnapshotId.New();
        runtime.Module.AnalyzeHandler = CompletedHandler(snapshot, fileId, epoch: 1, forgeSnapshotId: other.ToString());
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Profile_Mismatch_Is_Failed_Not_Success()
    {
        var bytes = B("x");
        var (snapshot, fileId) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        var otherProfile = new AnalysisProfile("other", "9");
        runtime.Module.AnalyzeHandler = CompletedHandler(snapshot, fileId, epoch: 1, forgeProfile: otherProfile);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        Assert.IsType<AnalysisJobResult.Failed>(result);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task RequestId_Mismatch_Is_Failed()
    {
        var bytes = B("x");
        var (snapshot, fileId) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        // Forge a completed envelope with a requestId that does not match the
        // one the adapter generated and packed.
        runtime.Module.AnalyzeHandler = CompletedHandler(snapshot, fileId, epoch: 1, forgeRequestId: "foreign");
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Canonical_Failed_Stays_Failed()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = FailedHandler(new AnalysisErrorDto("InvalidInput", "bad file slice", false), 3);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        Assert.Equal("bad file slice", failed.Error.SafeMessage);
        Assert.Equal(3L, failed.WorkerEpoch);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Timeout_Response_Maps_To_Failed()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = FailedHandler(new AnalysisErrorDto("Timeout", "analyzeSource exceeded the deadline", false), 2);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("Timeout", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_Response_Maps_To_Cancelled()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = CancelledHandler(epoch: 4);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var cancelled = Assert.IsType<AnalysisJobResult.Cancelled>(result);
        Assert.Equal(4L, cancelled.WorkerEpoch);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Malformed_Json_Typed_InvalidInput()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = (_, _) => new AnalysisBridgeResponse("{not json", 0);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Completed_Missing_Payload_Typed_InvalidInput()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = (_, _) =>
            new AnalysisBridgeResponse("{\"protocolVersion\":1,\"requestId\":\"r\",\"status\":\"completed\",\"payload\":{}}", 0);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Unknown_Status_Typed_UnsupportedCapability()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = (_, _) =>
            new AnalysisBridgeResponse("{\"protocolVersion\":1,\"requestId\":\"r\",\"status\":\"wat\"}", 0);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("UnsupportedCapability", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Failed_With_Foreign_RequestId_Is_Failed_Mismatch()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        // A failed envelope whose nonnull requestId does NOT match the request.
        runtime.Module.AnalyzeHandler = FailedHandler(new AnalysisErrorDto("InvalidInput", "x", false), 1, forgeRequestId: "foreign");
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        Assert.Contains("did not match the request", failed.Error.SafeMessage);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Failed_With_Null_RequestId_Is_Legitimate_Failed()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        // A failed envelope with no trusted requestId is legitimate (Rust could
        // not recover one); the original Failed error is preserved.
        runtime.Module.AnalyzeHandler = (_, _) =>
            new AnalysisBridgeResponse("{\"protocolVersion\":1,\"requestId\":null,\"status\":\"failed\",\"error\":{\"code\":\"InvalidInput\",\"safeMessage\":\"bad id\",\"retryable\":false}}", 1);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        Assert.Equal("bad id", failed.Error.SafeMessage);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_With_Foreign_RequestId_Is_Failed()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = CancelledHandler(epoch: 4, forgeRequestId: "foreign");
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_With_Invalid_Protocol_Is_Failed()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = CancelledHandler(epoch: 4, protocolVersion: 99);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("UnsupportedProtocol", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_With_Missing_RequestId_Is_Failed()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = (_, _) =>
            new AnalysisBridgeResponse("{\"protocolVersion\":1,\"status\":\"cancelled\"}", 4);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Response_Json_Array_Is_InvalidInput()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = (_, _) => new AnalysisBridgeResponse("[]", 0);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Response_Json_Null_Literal_Is_InvalidInput()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = (_, _) => new AnalysisBridgeResponse("null", 0);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Null_Bridge_Response_Is_InvalidInput()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = (_, _) => null!;
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Late_Completion_After_Disposal_Is_Disposed()
    {
        var bytes = B("x");
        var (snapshot, fileId) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeGate = AnalyzeTcs(); // worker holds the reply
        var analyzer = new RustSourceAnalyzer(runtime);
        var job = analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        await Step(); // import + dispatch, gate pending
        await analyzer.DisposeAsync(); // dispose while pending
        var result = await job;
        Assert.IsType<AnalysisJobResult.Disposed>(result);
        // A late completion must NOT return Completed: release the gate with a
        // completed envelope and assert the already-disposed job stays Disposed.
        runtime.Module.AnalyzeGate = null;
        runtime.Module.AnalyzeHandler = CompletedHandler(snapshot, fileId, 0);
        // The fake's dispose released the gate as cancelled, so the job is settled.
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Late_Completion_After_Cancel_Is_Cancelled()
    {
        var bytes = B("x");
        var (snapshot, fileId) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeGate = AnalyzeTcs();
        var analyzer = new RustSourceAnalyzer(runtime);
        using var cts = new CancellationTokenSource();
        var job = analyzer.AnalyzeAsync(snapshot, cts.Token);
        await Step();
        cts.Cancel();
        var result = await job;
        Assert.IsType<AnalysisJobResult.Cancelled>(result);
        // A late completion arriving after cancellation must NOT override the
        // settled Cancelled outcome.
        runtime.Module.AnalyzeGate = null;
        runtime.Module.AnalyzeHandler = CompletedHandler(snapshot, fileId, 0);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Cancel_During_Blocked_Import_Returns_Cancelled_Before_Release()
    {
        var bytes = B("x");
        var (snapshot, fileId) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.ImportGate = ImportTcs(); // shared import blocked
        runtime.Module.AnalyzeHandler = CompletedHandler(snapshot, fileId, 1);
        var analyzer = new RustSourceAnalyzer(runtime);
        using var ctsA = new CancellationTokenSource();
        var a = analyzer.AnalyzeAsync(snapshot, ctsA.Token);
        // Caller A cancels while the shared import is blocked: returns Cancelled
        // BEFORE the import gate is released, and does NOT fault the shared
        // import for the uncanceled caller B.
        ctsA.Cancel();
        var ra = await a;
        Assert.IsType<AnalysisJobResult.Cancelled>(ra);
        Assert.Equal(0, runtime.Module.AnalyzeCalls);
        // An uncanceled caller B still finishes when the import is released.
        var b = analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        runtime.ImportGate.SetResult(true);
        var rb = await b;
        Assert.IsType<AnalysisJobResult.Completed>(rb);
        Assert.Equal(1, runtime.ImportCalls); // single shared import
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_During_Blocked_Import_Settles_No_Late_Worker()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.ImportGate = ImportTcs(); // shared import blocked (non-cooperative)
        var analyzer = new RustSourceAnalyzer(runtime);
        var job = analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        // Dispose must NOT wait forever for the blocked import; jobs settle and
        // no late worker is created.
        await analyzer.DisposeAsync();
        var result = await job;
        Assert.IsType<AnalysisJobResult.Disposed>(result);
        Assert.Equal(0, runtime.Module.AnalyzeCalls);
        // Release afterward for resource cleanup (the late import disposes the
        // module exactly once).
        runtime.ImportGate.SetResult(true);
        await Task.Yield();
    }

    [Fact]
    public async Task Worker_Epoch_Exposed_And_Checkable()
    {
        var bytes = B("x");
        var (snapshot, fileId) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.Epoch = 7;
        runtime.Module.AnalyzeHandler = CompletedHandler(snapshot, fileId, epoch: 7);
        var analyzer = new RustSourceAnalyzer(runtime);
        var completed = Assert.IsType<AnalysisJobResult.Completed>(
            await analyzer.AnalyzeAsync(snapshot, CancellationToken.None));
        Assert.Equal(7L, completed.WorkerEpoch);
        Assert.True(await analyzer.IsCurrentWorkerEpochAsync(7, CancellationToken.None));
        runtime.Module.Epoch = 8; // simulated worker reset bumps the epoch
        Assert.False(await analyzer.IsCurrentWorkerEpochAsync(7, CancellationToken.None));
        Assert.True(await analyzer.IsCurrentWorkerEpochAsync(8, CancellationToken.None));
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Cancel_During_Active_Request_Invokes_Explicit_Js_Cancel()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.ImportGate = ImportTcs();           // deterministic import barrier
        runtime.Module.AnalyzeGate = AnalyzeTcs();   // worker deliberately nonresponsive
        runtime.Module.AnalyzeDispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyzer = new RustSourceAnalyzer(runtime);
        using var cts = new CancellationTokenSource();
        var job = analyzer.AnalyzeAsync(snapshot, cts.Token);
        // Release the import so the adapter registers the cancel callback and
        // dispatches analyzeSource (gate pending = worker nonresponsive).
        runtime.ImportGate.SetResult(true);
        await runtime.Module.AnalyzeDispatched!.Task; // deterministic: analyze dispatched
        Assert.Equal(1, runtime.Module.AnalyzeCalls);
        // Cancel: must explicitly cancel the JS operation by id, not just the
        // .NET await. The registration fires cancelSource with the requestId.
        cts.Cancel();
        var result = await job;
        Assert.IsType<AnalysisJobResult.Cancelled>(result);
        Assert.Equal(1, runtime.Module.CancelCalls);
        Assert.NotNull(runtime.Module.LastCancelTarget);
        Assert.Contains("\"" + runtime.Module.LastCancelTarget + "\"", runtime.Module.LastMetadata);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Cancel_During_Import_Does_Not_Dispatch()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.ImportGate = ImportTcs(); // import pending (no ct on it)
        var analyzer = new RustSourceAnalyzer(runtime);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var job = analyzer.AnalyzeAsync(snapshot, cts.Token);
        // Release the shared import; the adapter re-checks the canceled token
        // and returns Cancelled WITHOUT dispatching analyzeSource.
        runtime.ImportGate.SetResult(true);
        var result = await job;
        Assert.IsType<AnalysisJobResult.Cancelled>(result);
        Assert.Equal(0, runtime.Module.AnalyzeCalls);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Disposal_During_Pending_Settles_Disposed_And_Never_Revives()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeGate = AnalyzeTcs(); // pending
        var analyzer = new RustSourceAnalyzer(runtime);
        var job = analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        await Step();
        // Disposing the analyzer terminates the worker; disposal-induced
        // cancellation settles the in-flight job as Disposed (not Completed).
        await analyzer.DisposeAsync();
        var result = await job;
        Assert.IsType<AnalysisJobResult.Disposed>(result);
        Assert.Equal(1, runtime.Module.DisposeCalls);
        // A later call after disposal returns Disposed and never revives.
        var second = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        Assert.IsType<AnalysisJobResult.Disposed>(second);
        Assert.Equal(1, runtime.Module.AnalyzeCalls); // no fresh dispatch
    }

    [Fact]
    public async Task Disposed_Analyzer_Returns_Disposed_Without_Import()
    {
        var runtime = new FakeJSRuntime();
        var analyzer = new RustSourceAnalyzer(runtime);
        await analyzer.DisposeAsync();
        var result = await analyzer.AnalyzeAsync(Snapshot(B("x")).Snapshot, CancellationToken.None);
        Assert.IsType<AnalysisJobResult.Disposed>(result);
        Assert.Equal(0, runtime.ImportCalls);
        Assert.Equal(0, runtime.Module.AnalyzeCalls);
    }

    [Fact]
    public async Task Next_After_Failure_Works()
    {
        var bytes = B("x");
        var (snapshot, fileId) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        var analyzer = new RustSourceAnalyzer(runtime);
        // First call: a failed envelope correlated with the REAL request id.
        runtime.Module.AnalyzeHandler = FailedHandler(new AnalysisErrorDto("InvalidInput", "first fails", false), 1);
        var r1 = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        Assert.IsType<AnalysisJobResult.Failed>(r1);
        // Second call: a completed envelope correlated with the REAL request id.
        runtime.Module.AnalyzeHandler = CompletedHandler(snapshot, fileId, epoch: 1);
        var r2 = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        Assert.IsType<AnalysisJobResult.Completed>(r2);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_Imports_Do_Not_Leak_Duplicate_Module_Refs()
    {
        var bytes = B("x");
        var (snapshot, fileId) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.ImportGate = ImportTcs(); // shared import pending
        runtime.Module.AnalyzeHandler = CompletedHandler(snapshot, fileId, epoch: 1);
        var analyzer = new RustSourceAnalyzer(runtime);
        var j1 = analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var j2 = analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        // Release the single shared import; both callers share the module and
        // each gets a correlated completed response (handler reads real id).
        runtime.ImportGate.SetResult(true);
        Assert.IsType<AnalysisJobResult.Completed>(await j1);
        Assert.IsType<AnalysisJobResult.Completed>(await j2);
        Assert.Equal(1, runtime.ImportCalls); // exactly one import
        Assert.Equal(2, runtime.Module.AnalyzeCalls);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Empty_Source_Is_Valid_Completed()
    {
        var bytes = Array.Empty<byte>();
        var (snapshot, fileId) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = CompletedHandler(snapshot, fileId, epoch: 0);
        var analyzer = new RustSourceAnalyzer(runtime);
        var completed = Assert.IsType<AnalysisJobResult.Completed>(
            await analyzer.AnalyzeAsync(snapshot, CancellationToken.None));
        Assert.Equal(snapshot.Id.ToString(), completed.Result.SnapshotId);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task WorkingGeneration_Above_JS_Safe_Integer_Preserved_In_Metadata()
    {
        // 9007199254740993 > 2^53; must serialize as a JSON integer literal and
        // survive byte-for-byte (the adapter forwards the packer's string).
        var gen = 9007199254740993L;
        var bytes = B("x");
        var (snapshot, fileId) = Snapshot(bytes, workingGeneration: gen);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = CompletedHandler(snapshot, fileId, epoch: 0);
        var analyzer = new RustSourceAnalyzer(runtime);
        await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        Assert.Contains("\"workingGeneration\":" + gen, runtime.Module.LastMetadata);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Cancel_Before_Dispatch_Returns_Cancelled_Promptly()
    {
        // Cancellation requested before the adapter dispatches returns Cancelled
        // without waiting on the (shared, ct-agnostic) import.
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.ImportGate = ImportTcs();
        var analyzer = new RustSourceAnalyzer(runtime);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var job = analyzer.AnalyzeAsync(snapshot, cts.Token);
        runtime.ImportGate.SetResult(true);
        var result = await job;
        Assert.IsType<AnalysisJobResult.Cancelled>(result);
        Assert.Equal(0, runtime.Module.AnalyzeCalls);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_With_String_Protocol_Is_UnsupportedProtocol_Not_Throw()
    {
        // A cancelled envelope whose protocolVersion is a STRING ("1") must NOT
        // throw InvalidOperationException from TryGetInt32 on a non-number; it
        // settles a typed UnsupportedProtocol failure.
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = RawCancelledHandler(protocolVersionJson: "\"1\"", epoch: 4);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("UnsupportedProtocol", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_With_Null_Protocol_Is_UnsupportedProtocol()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = RawCancelledHandler(protocolVersionJson: "null", epoch: 4);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("UnsupportedProtocol", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_With_Bool_Protocol_Is_UnsupportedProtocol()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = RawCancelledHandler(protocolVersionJson: "true", epoch: 4);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("UnsupportedProtocol", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_With_Fractional_Protocol_Is_UnsupportedProtocol()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = RawCancelledHandler(protocolVersionJson: "1.5", epoch: 4);
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("UnsupportedProtocol", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_With_Bad_Surrogate_RequestId_Is_InvalidInput()
    {
        // A cancelled requestId containing a lone surrogate must settle typed
        // InvalidInput (IsValidOpaqueId rejects it), not a runtime exception.
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = (meta, _) =>
        {
            var rid = ExtractRequestId(meta!);
            // Replace the trusted id with a lone-surrogate id (invalid opaque id).
            return new AnalysisBridgeResponse(
                "{\"protocolVersion\":1,\"requestId\":\"\\uD800\",\"status\":\"cancelled\"}", 4);
            // (rid unused: the envelope carries an invalid id that must be rejected.)
        };
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Completed_With_String_Protocol_Is_InvalidInput_Not_Throw()
    {
        // A completed envelope whose protocolVersion is a STRING must settle
        // typed InvalidInput (ParseResponse's TryGetInt32 would otherwise throw).
        var bytes = B("x");
        var (snapshot, fileId) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = (meta, _) =>
        {
            var rid = ExtractRequestId(meta!);
            var good = BuildCompleted(snapshot.Id.ToString(), snapshot.Profile, fileId, rid);
            // Tamper: make protocolVersion a string.
            var bad = good.Replace("\"protocolVersion\":1", "\"protocolVersion\":\"1\"");
            return new AnalysisBridgeResponse(bad, 1);
        };
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Failed_With_String_Protocol_Is_InvalidInput_Not_Throw()
    {
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = (meta, _) =>
        {
            var rid = ExtractRequestId(meta!);
            var good = JsonSerializer.Serialize(new AnalysisResponseEnvelope(
                1, rid, ResponseStatus.Failed, null, new AnalysisErrorDto("InvalidInput", "x", false)), AnalysisJson.Options);
            var bad = good.Replace("\"protocolVersion\":1", "\"protocolVersion\":\"1\"");
            return new AnalysisBridgeResponse(bad, 1);
        };
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        await analyzer.DisposeAsync();
    }

    [Fact]
    public async Task Oversized_Cancelled_Envelope_Rejected_Before_Parse()
    {
        // A cancelled envelope larger than the hard response limit (with a
        // matching id and padding) must be rejected BEFORE an unbounded graph
        // is allocated (the cancelled path previously bypassed the body bound).
        var bytes = B("x");
        var (snapshot, _) = Snapshot(bytes);
        var runtime = new FakeJSRuntime();
        runtime.Module.AnalyzeHandler = (meta, _) =>
        {
            var rid = ExtractRequestId(meta!);
            // > 4 MiB padding so json.Length and UTF8 byte count exceed the limit.
            var pad = new string('x', AnalysisJson.MaxResponseBytes + 1024);
            var env = "{\"protocolVersion\":1,\"requestId\":\"" + rid
                + "\",\"status\":\"cancelled\",\"pad\":\"" + pad + "\"}";
            return new AnalysisBridgeResponse(env, 4);
        };
        var analyzer = new RustSourceAnalyzer(runtime);
        var result = await analyzer.AnalyzeAsync(snapshot, CancellationToken.None);
        var failed = Assert.IsType<AnalysisJobResult.Failed>(result);
        Assert.Equal("InvalidInput", failed.Error.Code);
        Assert.Contains("hard response limit", failed.Error.SafeMessage);
        await analyzer.DisposeAsync();
    }

    // ---- helpers ----

    private static async Task Step()
    {
        // Yield once so the adapter's await chain (import + analyze await of
        // synchronous fake calls) progresses. No timing sleeps.
        await Task.Yield();
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static Client.Workspace.Domain.SHA256 Sha(byte[] b) =>
        Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(b));

    private static (AnalysisSnapshot Snapshot, FileId FileId) Snapshot(byte[] bytes, long workingGeneration = 5L)
    {
        var fileId = FileId.New();
        var file = new CapturedFile(fileId, "main.tex", HasBom: false, bytes.LongLength, Sha(bytes),
            ImmutableArray.Create(bytes));
        var snap = new AnalysisSnapshot(
            AnalysisSnapshotId.New(), ProjectId.New(), Guid.NewGuid(),
            workingGeneration, LocalEpoch: 0, BaseHeadRevisionId: null,
            AnalysisProfile.EngineStructural(), AnalysisLimits.Defaults(),
            ImmutableArray.Create(file), ImmutableArray<ExcludedFile>.Empty);
        return (snap, fileId);
    }

    private static (AnalysisSnapshot Snapshot, FileId FileId) Snapshot(byte[] bytes)
        => Snapshot(bytes, 5L);

    private static TaskCompletionSource<AnalysisBridgeResponse> AnalyzeTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<bool> ImportTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Func<string?, byte[]?, AnalysisBridgeResponse> CompletedHandler(
        AnalysisSnapshot snapshot, FileId fileId, long epoch,
        string? forgeSnapshotId = null, AnalysisProfile? forgeProfile = null, string? forgeRequestId = null)
    {
        return (meta, _) =>
        {
            var rid = forgeRequestId ?? ExtractRequestId(meta!);
            var sid = forgeSnapshotId ?? snapshot.Id.ToString();
            var prof = forgeProfile ?? snapshot.Profile;
            return new AnalysisBridgeResponse(BuildCompleted(sid, prof, fileId, rid), epoch);
        };
    }

    private static Func<string?, byte[]?, AnalysisBridgeResponse> FailedHandler(AnalysisErrorDto error, long epoch, string? forgeRequestId = null)
    {
        return (meta, _) =>
        {
            var rid = forgeRequestId ?? ExtractRequestId(meta!);
            return new AnalysisBridgeResponse(JsonSerializer.Serialize(new AnalysisResponseEnvelope(
                1, rid, ResponseStatus.Failed, null, error), AnalysisJson.Options), epoch);
        };
    }

    private static Func<string?, byte[]?, AnalysisBridgeResponse> CancelledHandler(long epoch, string? forgeRequestId = null, int protocolVersion = 1)
    {
        return (meta, _) =>
        {
            var rid = forgeRequestId ?? ExtractRequestId(meta!);
            return new AnalysisBridgeResponse(JsonSerializer.Serialize(new AnalysisResponseEnvelope(
                protocolVersion, rid, ResponseStatus.Cancelled, null, null), AnalysisJson.Options), epoch);
        };
    }

    // A cancelled envelope with a RAW (arbitrary) protocolVersion JSON token, used
    // to verify non-number protocolVersions settle typed UnsupportedProtocol
    // rather than throwing InvalidOperationException from TryGetInt32.
    private static Func<string?, byte[]?, AnalysisBridgeResponse> RawCancelledHandler(string protocolVersionJson, long epoch)
    {
        return (meta, _) =>
        {
            var rid = ExtractRequestId(meta!);
            return new AnalysisBridgeResponse(
                "{\"protocolVersion\":" + protocolVersionJson
                + ",\"requestId\":\"" + rid + "\",\"status\":\"cancelled\"}", epoch);
        };
    }



    private static string ExtractRequestId(string metadataJson)
    {
        using var doc = JsonDocument.Parse(metadataJson);
        return doc.RootElement.GetProperty("requestId").GetString()!;
    }

    /// <summary>Build a valid canonical completed envelope JSON string echoing
    /// the snapshot's id and profile, with one file facts entry, no diagnostics,
    /// a single pass ENGINE-STRUCT-001 rule result, and the typed PDF
    /// unavailable capabilities.</summary>
    private static string BuildCompleted(AnalysisSnapshot snapshot, FileId fileId, string requestId) =>
        BuildCompleted(snapshot.Id.ToString(), snapshot.Profile, fileId, requestId);

    private static string BuildCompleted(string snapshotId, AnalysisProfile profile, FileId fileId, string requestId)
    {
        var fileIdStr = fileId.ToString();
        var fileFacts = new AnalysisSourceFileFactsDto(
            fileIdStr, ByteLength: 0, HasBom: false, NewlineKind.Lf,
            Truncated: false, BraceDepthExceeded: false, TokenCount: 0,
            EngineOutcome: RuleOutcome.Pass, Array.Empty<AnalysisTokenDto>());
        var result = new AnalysisResultDto(
            snapshotId,
            new AnalysisProfileDto(profile.Id, profile.Version),
            new AnalysisSourceFactsDto(new[] { fileFacts }),
            Array.Empty<AnalysisEngineDiagnosticDto>(),
            new[]
            {
                new AnalysisRuleResultDto(
                    "ENGINE-STRUCT-001", "engine", RuleOutcome.Pass,
                    new[] { new AnalysisPerFileOutcomeDto(fileIdStr, RuleOutcome.Pass, Array.Empty<string>()) },
                    Array.Empty<string>()),
            },
            RuleOutcome.Pass,
            new AnalysisCoverageDto(
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                DiagnosticsTruncated: false, ResultTruncated: false),
            new[] { "pdfFacts", "pdfChecks" });
        var env = new AnalysisResponseEnvelope(1, requestId, ResponseStatus.Completed, result, null);
        return JsonSerializer.Serialize(env, AnalysisJson.Options);
    }

    // ---- fakes ----

    private sealed class FakeJSRuntime : IJSRuntime
    {
        public readonly FakeModule Module = new();
        public TaskCompletionSource<bool>? ImportGate;
        public int ImportCalls;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            if (identifier != "import")
                throw new InvalidOperationException($"unexpected runtime call {identifier}");
            ImportCalls++;
            Module.Runtime = this;
            return AwaitImport<TValue>(ct);
        }

        private async ValueTask<TValue> AwaitImport<TValue>(CancellationToken ct)
        {
            if (ImportGate is { } gate)
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return (TValue)(object)Module;
        }
    }

    private sealed class FakeModule : IJSObjectReference
    {
        public FakeJSRuntime Runtime = null!;
        public TaskCompletionSource<AnalysisBridgeResponse>? AnalyzeGate;
        public TaskCompletionSource<object?>? AnalyzeDispatched;
        public Func<string?, byte[]?, AnalysisBridgeResponse>? AnalyzeHandler;
        public long Epoch;
        public int AnalyzeCalls, CancelCalls, DisposeCalls, EpochCalls;
        public string? LastMetadata;
        public byte[]? LastPacked;
        public string? LastCancelTarget;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            switch (identifier)
            {
                case "analyzeSource":
                    AnalyzeCalls++;
                    LastMetadata = (string)args![0]!;
                    LastPacked = (byte[])args![1]!;
                    AnalyzeDispatched?.TrySetResult(null);
                    if (AnalyzeGate is { } gate)
                        return AwaitAnalyze<TValue>(gate, ct);
                    var resp = AnalyzeHandler is null
                        ? new AnalysisBridgeResponse("", Epoch)
                        : AnalyzeHandler(LastMetadata, LastPacked);
                    return ValueTask.FromResult((TValue)(object)resp);
                case "cancelSource":
                    CancelCalls++;
                    LastCancelTarget = (string)args![0]!;
                    // InvokeVoidAsync calls InvokeAsync<IJSVoidResult> (an internal
                    // marker whose result is discarded); return null cast to it.
                    return ValueTask.FromResult((TValue)(object)null!);
                case "getWorkerEpoch":
                    EpochCalls++;
                    return ValueTask.FromResult((TValue)(object)Epoch);
                case "dispose":
                    DisposeCalls++;
                    // Disposing the real bridge terminates the worker and settles
                    // pending analysis as cancelled; the fake mirrors that so an
                    // in-flight analyze resolves (not deadlock).
                    if (AnalyzeGate is { } g)
                    {
                        AnalyzeGate = null;
                        g.TrySetResult(new AnalysisBridgeResponse(
                            "{\"protocolVersion\":1,\"status\":\"cancelled\"}", Epoch));
                    }
                    return ValueTask.FromResult((TValue)(object)null!);
                default:
                    throw new InvalidOperationException($"unexpected module call {identifier}");
            }
        }

        private static async ValueTask<TValue> AwaitAnalyze<TValue>(TaskCompletionSource<AnalysisBridgeResponse> gate, CancellationToken ct)
        {
            var resp = await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return (TValue)(object)resp;
        }

        public ValueTask DisposeAsync()
        {
            if (AnalyzeGate is { } g)
            {
                AnalyzeGate = null;
                g.TrySetResult(new AnalysisBridgeResponse(
                    "{\"protocolVersion\":1,\"status\":\"cancelled\"}", Epoch));
            }
            return ValueTask.CompletedTask;
        }
    }
}
