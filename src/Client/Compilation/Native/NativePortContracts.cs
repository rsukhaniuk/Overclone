using Client.Compilation.Lifecycle;

namespace Client.Compilation.Native;

public sealed record NativeFileTransfer(string Name, byte[] Bytes);
public sealed record NativeInputFact(string Name, int Length, string Sha256);
public sealed record NativePhaseTag(string Job, string Request, long Epoch, Guid Capture, string Digest, int Ordinal, string Tool, string Nonce,
    CompilationSnapshotIdentity? Snapshot = null, int Version = 1);
public sealed record NativePhaseRequest(NativePhaseTag Tag, string MainEntry, NativeFileTransfer[] Files,
    string[] Outputs, byte[] Loader, byte[] Wasm, int MaxLogBytes, int MaxOutputBytes);
public sealed record NativePhaseReply(NativePhaseTag Tag, string Outcome, int? NativeReturn, bool Entered,
    string Log, NativeFileTransfer[] Files, NativeInputFact[] Inputs, double? ElapsedMs = null, int? HeapBytes = null,
    string? WasmSha256 = null, string? LoaderSha256 = null);

/// <summary>Prompt asynchronous byte/FFI port. Not a second lifecycle. A completed
/// wait is not proof the foreign task completed. Terminate is prompt, idempotent,
/// correlated and synchronous (normal browser Worker.terminate transport only).</summary>
public interface INativePhaseTransport
{
    Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest request);
    void RequestTermination(string job, long epoch);
}
public sealed record NativeCompilePlan(bool BibtexRequired, int MaxLatexPasses, TimeSpan JobDeadline,
    int MaxPhaseFiles = 512, int MaxInputBytes = 16 * 1024 * 1024, int MaxFileBytes = 8 * 1024 * 1024,
    bool UseSelectionTools = false)
{
    public bool IsValid => MaxLatexPasses is >= 1 and <= 6 && JobDeadline > TimeSpan.Zero && JobDeadline <= TimeSpan.FromMinutes(2)
        && MaxPhaseFiles is >= 1 and <= 512 && MaxInputBytes is > 0 and <= 32 * 1024 * 1024 && MaxFileBytes is > 0 and <= 16 * 1024 * 1024;
}
public sealed record NativePhaseObservation(NativePhaseTag Tag, NativeInputFact[] Sent, NativePhaseReply Reply);

public sealed class NativeResourceLimitException : Exception { }

/// <summary>Bounds ACTUAL unfinished calls, not WaitAsync wrappers. Abandoned
/// operations retain slots until underlying settlement, including foreign OCE.
/// No queue, timer/sleep retry or owner-context continuation in retirement.</summary>
public sealed class NativeForeignOperations(int maximum)
{
    private readonly object _gate = new();
    private int _active;
    private TaskCompletionSource _idle = Completed();
    public int Active { get { lock (_gate) return _active; } }
    public Task WhenIdle { get { lock (_gate) return _idle.Task; } }
    private static TaskCompletionSource Completed() { var s = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); s.SetResult(); return s; }
    public async Task<T> RunAsync<T>(Func<Task<T>> start, CancellationToken wait)
    {
        wait.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (maximum <= 0 || _active >= maximum) throw new NativeResourceLimitException();
            if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Task<T> actual;
        try { actual = start() ?? throw new InvalidOperationException("Missing foreign operation."); }
        catch { Release(); throw; }
        _ = actual.ContinueWith(t => { _ = t.Exception; Release(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await actual.WaitAsync(wait).ConfigureAwait(false);
    }
    private void Release() { lock (_gate) if (--_active == 0) _idle.TrySetResult(); }
}
