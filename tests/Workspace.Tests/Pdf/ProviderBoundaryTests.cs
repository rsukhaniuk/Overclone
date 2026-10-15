using System.Diagnostics;
using System.Text.Json;
using Client.Pdf;
using Microsoft.JSInterop;
using Xunit;
using Xunit.Abstractions;

namespace Workspace.Tests.Pdf;

// Finite managed boundary probes, NOT native/browser evidence. Every held
// provider operation is released/joined in finally, after pending assertions.
public sealed class ProviderBoundaryTests(ITestOutputHelper output)
{
    private static PdfInspectRequest Request(int deadline = 30000) => new(new PdfDocument([37, 80, 68, 70]), 0, new(DeadlineMs: deadline), raster: false);
    private void Record(object value) => output.WriteLine("N9C1|" + JsonSerializer.Serialize(value));
    private static PdfRuntimePackage Package()
    {
        byte[] loader = [47, 47, 32], wasm = [0, 97, 115, 109, 1, 0, 0, 0];
        return new(loader, wasm, PdfHashes.Of(loader), PdfHashes.Of(wasm));
    }

    [Fact]
    public async Task Deadline_does_not_release_actual_unfinished_provider_admission()
    {
        var provider = new HeldProvider(); var js = new NoJs(); var port = new JsPdfInspector(js, provider);
        var first = port.InspectAsync(Request(1000));
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var status = (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status;
            Assert.Equal(PdfInspectionStatus.TimedOut, status); Assert.True(provider.Token.IsCancellationRequested);
            var subsequent = new List<string>();
            for (int i = 0; i < 7; i++) subsequent.Add((await port.InspectAsync(Request())).Status.ToString());
            Assert.All(subsequent, s => Assert.Equal("Busy", s));
            Assert.Equal(1, provider.Calls); Assert.Equal(1, provider.Unfinished); Assert.Equal(0, js.Imports);
            int callsBefore = provider.Calls, unfinishedBefore = provider.Unfinished;
            // Admission was observed while actual work was still pending, not
            // after manual completion. Only now release the finite fixture.
            await provider.ReleaseAndJoinAsync(); provider.ReturnAbsent = true;
            var recovery = await port.InspectAsync(Request()); Assert.Equal(PdfInspectionStatus.Unavailable, recovery.Status); Assert.Equal("RuntimeAbsent", recovery.Code);
            Assert.Equal(2, provider.Calls); Assert.Equal(0, provider.Unfinished); Assert.Equal(0, js.Imports);
            Record(new { kind = "deadline-admission", first = status.ToString(), subsequent, callsBefore, unfinishedBefore, callsAfter = provider.Calls, unfinishedAfter = provider.Unfinished, imports = js.Imports, recovery = recovery.Status.ToString(), recoveryCode = recovery.Code });
        }
        finally { await provider.ReleaseAndJoinAsync(); await first.WaitAsync(TimeSpan.FromSeconds(3)); await port.DisposeAsync(); }
    }

    [Fact]
    public async Task Caller_cancellation_keeps_actual_provider_slot_until_settlement()
    {
        var provider = new HeldProvider(); var js = new NoJs(); var port = new JsPdfInspector(js, provider); using var caller = new CancellationTokenSource();
        var first = port.InspectAsync(Request(), caller.Token);
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); caller.Cancel();
            var status = (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status;
            Assert.Equal(PdfInspectionStatus.Cancelled, status); Assert.True(caller.IsCancellationRequested);
            var blocked = await port.InspectAsync(Request()); Assert.Equal(PdfInspectionStatus.Busy, blocked.Status);
            Assert.Equal(1, provider.Unfinished); Assert.Equal(1, provider.Calls); Assert.Equal(0, js.Imports);
            int unfinishedBefore = provider.Unfinished;
            await provider.ReleaseAndJoinAsync(); provider.ReturnAbsent = true;
            Assert.Equal(PdfInspectionStatus.Unavailable, (await port.InspectAsync(Request())).Status); Assert.Equal(2, provider.Calls);
            Record(new { kind = "caller-admission", first = status.ToString(), blocked = blocked.Status.ToString(), unfinishedBefore, callsAfter = provider.Calls, unfinishedAfter = provider.Unfinished, imports = js.Imports });
        }
        finally { await provider.ReleaseAndJoinAsync(); await first.WaitAsync(TimeSpan.FromSeconds(3)); await port.DisposeAsync(); }
    }

    [Fact]
    public async Task Dispose_is_prompt_while_provider_pending_and_never_revives()
    {
        var provider = new HeldProvider(); var js = new NoJs(); var port = new JsPdfInspector(js, provider); var first = port.InspectAsync(Request());
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await port.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            var status = (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status;
            Assert.Equal(PdfInspectionStatus.Disposed, status); Assert.Equal(1, provider.Unfinished);
            int unfinishedAtDispose = provider.Unfinished;
            Assert.Equal(PdfInspectionStatus.Disposed, (await port.InspectAsync(Request())).Status); Assert.Equal(1, provider.Calls); Assert.Equal(0, js.Imports);
            await provider.ReleaseAndJoinAsync();
            Assert.Equal(PdfInspectionStatus.Disposed, (await port.InspectAsync(Request())).Status); Assert.Equal(1, provider.Calls); Assert.Equal(0, js.Imports);
            Record(new { kind = "dispose-admission", first = status.ToString(), unfinishedAtDispose, callsAfterLateCompletion = provider.Calls, unfinishedAfter = provider.Unfinished, imports = js.Imports });
        }
        finally { await provider.ReleaseAndJoinAsync(); await first.WaitAsync(TimeSpan.FromSeconds(3)); await port.DisposeAsync(); }
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("faulted")]
    [InlineData("canceled")]
    public async Task Foreign_provider_cancellation_is_not_an_unexpired_deadline(string mode)
    {
        var provider = new ForeignProvider(mode); var js = new NoJs(); using var caller = new CancellationTokenSource(); await using var port = new JsPdfInspector(js, provider);
        var clock = Stopwatch.StartNew(); var result = await port.InspectAsync(Request(), caller.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PdfInspectionStatus.TransportError, result.Status); Assert.Equal("ForeignCancellation", result.Code);
        Assert.False(caller.IsCancellationRequested); Assert.False(provider.Token.IsCancellationRequested); Assert.True(clock.ElapsedMilliseconds < 30000);
        Assert.Null(result.NativeStatus); Assert.Null(result.EnteredNative); Assert.Null(result.Page); Assert.Equal(0, js.Imports);
        Assert.Equal(PdfInspectionStatus.Unavailable, (await port.InspectAsync(Request())).Status); Assert.Equal(2, provider.Calls);
        Record(new { kind = "foreign-provider", mode, status = result.Status.ToString(), result.Code, callerCanceled = caller.IsCancellationRequested, linkedCanceled = provider.FirstTokenCanceled, deadlineMs = 30000, elapsedMs = clock.ElapsedMilliseconds, providerCalls = provider.Calls, imports = js.Imports });
    }

    [Fact]
    public async Task Foreign_import_cancellation_is_transport_failure_and_retryable()
    {
        var js = new NoJs { ForeignImport = true }; await using var port = new JsPdfInspector(js, new ConstantProvider(Package())); using var caller = new CancellationTokenSource();
        var clock = Stopwatch.StartNew(); var result = await port.InspectAsync(Request(), caller.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PdfInspectionStatus.TransportError, result.Status); Assert.Equal("ForeignCancellation", result.Code); Assert.False(caller.IsCancellationRequested); Assert.True(clock.ElapsedMilliseconds < 30000);
        Assert.Equal(PdfInspectionStatus.TransportError, (await port.InspectAsync(Request())).Status); Assert.Equal(2, js.Imports); Assert.Null(result.Page); Assert.Null(result.EnteredNative);
        Record(new { kind = "foreign-import", status = result.Status.ToString(), result.Code, callerCanceled = caller.IsCancellationRequested, deadlineMs = 30000, elapsedMs = clock.ElapsedMilliseconds, imports = js.Imports });
    }

    [Theory]
    [InlineData("loader")]
    [InlineData("wasm")]
    [InlineData("both")]
    public void Oversize_runtime_preflight_rejects_before_either_owned_clone(string mode)
    {
        byte[] loader = new byte[mode is "loader" or "both" ? 1048577 : 1048576];
        byte[] wasm = new byte[mode is "wasm" or "both" ? 16777217 : 8]; string digest = new('0', 64);
        ArgumentException? failure = null; long start = GC.GetAllocatedBytesForCurrentThread();
        try { _ = new PdfRuntimePackage(loader, wasm, digest, digest); }
        catch (ArgumentException error) { failure = error; }
        long allocation = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.NotNull(failure); Assert.Equal(mode == "wasm" ? "wasm" : "loader", failure.ParamName); Assert.InRange(allocation, 0, 65535);
        Record(new { kind = "runtime-preflight", mode, rejected = true, parameter = failure.ParamName, callerLoaderBytes = loader.Length, callerWasmBytes = wasm.Length, allocationBytes = allocation });
    }

    private sealed class HeldProvider : IPdfRuntimeProvider
    {
        private readonly object _gate = new(); private readonly List<TaskCompletionSource<PdfRuntimePackage?>> _pending = []; private int _calls;
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token; public bool ReturnAbsent; public int Calls => Volatile.Read(ref _calls);
        public int Unfinished { get { lock (_gate) return _pending.Count(t => !t.Task.IsCompleted); } }
        public ValueTask<PdfRuntimePackage?> GetAsync(CancellationToken token)
        {
            Interlocked.Increment(ref _calls); Token = token;
            if (ReturnAbsent) return ValueTask.FromResult<PdfRuntimePackage?>(null);
            var task = new TaskCompletionSource<PdfRuntimePackage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _pending.Add(task); Entered.TrySetResult(true); return new(task.Task);
        }
        public async Task ReleaseAndJoinAsync()
        {
            TaskCompletionSource<PdfRuntimePackage?>[] all; lock (_gate) all = _pending.ToArray();
            foreach (var task in all) task.TrySetResult(null);
            await Task.WhenAll(all.Select(t => t.Task)).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }
    private sealed class ConstantProvider(PdfRuntimePackage package) : IPdfRuntimeProvider
    { public ValueTask<PdfRuntimePackage?> GetAsync(CancellationToken token) => ValueTask.FromResult<PdfRuntimePackage?>(package); }
    private sealed class ForeignProvider(string mode) : IPdfRuntimeProvider
    {
        public CancellationToken Token; public int Calls; public bool FirstTokenCanceled;
        public ValueTask<PdfRuntimePackage?> GetAsync(CancellationToken token)
        {
            if (++Calls > 1) return ValueTask.FromResult<PdfRuntimePackage?>(null);
            Token = token; FirstTokenCanceled = token.IsCancellationRequested;
            var foreign = new CancellationToken(true);
            if (mode == "sync") throw new OperationCanceledException("foreign TEST token", foreign);
            return mode == "canceled" ? ValueTask.FromCanceled<PdfRuntimePackage?>(foreign) : ValueTask.FromException<PdfRuntimePackage?>(new OperationCanceledException("foreign TEST token", foreign));
        }
    }
    private sealed class NoJs : IJSRuntime
    {
        public int Imports; public bool ForeignImport;
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args)
        {
            Imports++;
            if (ForeignImport && Imports == 1) return ValueTask.FromException<T>(new OperationCanceledException("foreign TEST import", new CancellationToken(true)));
            return ValueTask.FromException<T>(new JSException("No native JS expected in provider boundary test"));
        }
    }
}
