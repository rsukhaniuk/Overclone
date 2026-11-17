using Client.Compilation.Lifecycle;

namespace Client.Compilation.Native;

public sealed record NativeFileTransfer(string Name, byte[] Bytes);
public sealed record NativeInputFact(string Name, int Length, string Sha256);
public sealed record NativePhaseTag(string Job, string Request, long Epoch, Guid Capture, string Digest, int Ordinal, string Tool, string Nonce,
    CompilationSnapshotIdentity? Snapshot = null, int Version = 1,
    NativeRuntimeProfile Profile = NativeRuntimeProfile.Legacy, string? RuntimeIdentity = null, string? EngineId = null);
public sealed record NativePhaseRequest(NativePhaseTag Tag, string MainEntry, NativeFileTransfer[] Files,
    string[] Outputs, byte[] Loader, byte[] Wasm, int MaxLogBytes, int MaxOutputBytes, string? SelectedRoot = null, string[]? RuntimeResourceNames = null, string[]? GeneratedInputNames = null, byte[]? BootstrapData = null, string? BibliographyTool = null);
public sealed record NativePhaseReply(NativePhaseTag Tag, string Outcome, int? NativeReturn, bool Entered,
    string Log, NativeFileTransfer[] Files, NativeInputFact[] Inputs, double? ElapsedMs = null, int? HeapBytes = null,
    string? WasmSha256 = null, string? LoaderSha256 = null, byte[]? SourceMapPayload = null)
{
    // Raw native schema1 sidecar only, not the correlated output wrapper.
    public byte[]? SourceMapGeometryPayload { get; init; }
    public string? BootstrapSha256 { get; init; }
}

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
    bool UseSelectionTools = false, NativeRuntimeProfile Profile = NativeRuntimeProfile.Legacy, int? PhaseProtocolVersion = null, bool DviOutput = false, NativeCompilerEngine Engine = NativeCompilerEngine.PdfLaTeX)
{
    public bool IsValid => MaxLatexPasses is >= 1 and <= 6 && JobDeadline > TimeSpan.Zero && JobDeadline <= TimeSpan.FromMinutes(2)
        && NativeRuntimeProfiles.Known(Profile)
        && (NativeRuntimeProfiles.IsSuite(Profile)
            ? NativeCompilerSuiteContract.Phase(Profile, Engine) != 0 && EffectivePhaseVersion == NativeCompilerSuiteContract.Phase(Profile, Engine)
            : Engine == NativeCompilerEngine.PdfLaTeX && (PhaseProtocolVersion is null || PhaseProtocolVersion == NativeRuntimeProfiles.ProtocolVersion(Profile)
                || PhaseProtocolVersion is 4 or 5 && Profile == NativeRuntimeProfile.LocalLatex2023Authoring
                || PhaseProtocolVersion == 7 && Profile == NativeRuntimeProfile.LocalLatex2023Dvi && DviOutput))
        && (DviOutput == (EffectivePhaseVersion == 7 || NativeNavigationContract.IsNavigationProfile(Profile) && Engine == NativeCompilerEngine.LaTeXDvi && EffectivePhaseVersion == NativeCompilerSuiteContract.Phase(Profile, Engine)))
        && MaxPhaseFiles >= 1 && MaxPhaseFiles <= (NativeRuntimeProfiles.IsSuite(Profile) ? NativeCompilerSuiteContract.Files + NativeRuntimeProfiles.PhaseFileAllowance(Profile)
            : EffectivePhaseVersion is 4 or 5 or 6 or 7 ? NativeAuthoringV4Contract.PhaseFiles : 512)
        && MaxInputBytes > 0 && MaxInputBytes <= NativeRuntimeProfiles.MaxTotalBytes(Profile)
        && MaxFileBytes > 0 && MaxFileBytes <= NativeRuntimeProfiles.MaxFileBytes(Profile);
    public int EffectivePhaseVersion => PhaseProtocolVersion ?? NativeRuntimeProfiles.ProtocolVersion(Profile);
    // Increasing runtime capacity cannot increase the modern project/aux limit.
    public int ProjectFileBytes => NativeRuntimeProfiles.Modern(Profile) ? 8 * 1024 * 1024 : MaxFileBytes;
    public int SourceFileBytes => Profile == NativeInputBudgetContract.Profile ? NativeRuntimeProfiles.ProjectFileBytes(Profile) : ProjectFileBytes;
    public int ProjectTotalBytes => NativeRuntimeProfiles.ProjectTotalBytes(Profile);
    public int ConverterProjectBytes => NativeInputBudgetContract.Enabled(Profile) ? NativeInputBudgetContract.ConverterProjectBytes : NativeLatexDviContract.ProjectBytes;
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
