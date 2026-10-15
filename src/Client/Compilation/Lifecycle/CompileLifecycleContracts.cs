using Client.Compilation.Presentation;

namespace Client.Compilation.Lifecycle;

public readonly record struct CompileJobId(Guid Value);

public enum CompileLifecycleReason
{
    Succeeded, Superseded, Cancelled, TimedOut, Disposed, InvalidOptions,
    NoProject, NoSelection, CaptureInvalid, CaptureQuota, CaptureUnavailable,
    CaptureCancelled, CaptureStale, TransportError, InvalidOutput,
    CompilerFailed, WorkerCancelled, WorkerTimedOut, Unsupported,
    ResourceLimit, EpochExhausted
}

/// <summary>Explicit finite limits. Deadline starts at dispatch and covers capture
/// and transport. Pending captures are deferred until dispatch. Timer ceiling is
/// the .NET timer representation limit, not a product performance policy.</summary>
public sealed record CompileLifecycleOptions(
    CompilationCaptureBudget CaptureBudget,
    CompileOutputBudget OutputBudget,
    int MaxDescriptorMetadataBytes,
    TimeSpan Deadline,
    CompileOutputDescriptor? OutputFormat = null,
    CompileOutputDescriptor? OutputAdapter = null)
{
    public bool IsValid => CaptureBudget is { IsValid: true } && OutputBudget is { IsValid: true }
        && MaxDescriptorMetadataBytes > 0 && MaxDescriptorMetadataBytes <= Array.MaxLength
        && Deadline > TimeSpan.Zero && Deadline <= TimeSpan.FromMilliseconds(uint.MaxValue - 1);
}

/// <summary>Bounds all unfinished managed operation leases, including abandoned
/// capture/execute tasks and asynchronous cancellation callbacks. Once exhausted,
/// new requests settle ResourceLimit rather than accumulate orphan operations.
/// EpochLimit permits deterministic exhaustion checks without accepting an epoch
/// or identity from a response. No limit is a native memory/process guarantee.</summary>
public sealed record CompileLifecycleLimits(int MaxUnsettledJobs, long EpochLimit)
{
    public bool IsValid => MaxUnsettledJobs > 0 && MaxUnsettledJobs <= Array.MaxLength && EpochLimit > 0;
}

/// <summary>Factory must return promptly; synchronous arbitrary blocking code is
/// outside the managed port contract. Each object represents one worker lifetime.
/// It is never shared with another job. No implementation is registered by B3.</summary>
public interface ICompilationWorkerFactory
{
    ICompilationWorker? Create(CompileJobId jobId, long epoch);
}

public interface ICompilationWorker
{
    /// <summary>Must return a task promptly (it may ignore cancellation or never
    /// finish). Inputs are the actual privately owned B1/B2a objects. Raw output
    /// is untrusted; service applies B2a creation AND TryAccept itself.</summary>
    Task<CompileOutputEnvelopeInput?>? ExecuteAsync(CompileJobId jobId,
        CompileRequestContext context, CompileOutputBudget limits, CancellationToken cancellationToken);

    /// <summary>Nonblocking, idempotent lifetime request. May throw; service
    /// isolates faults. C# calling this is NOT evidence native execution stopped.
    /// Implementations own eventual native teardown and must not synchronously
    /// wait for process exit or call arbitrary blocking native code here.</summary>
    void RequestTermination(CompileJobId jobId, long epoch, CompileLifecycleReason reason);
}

public sealed record CompileLifecycleResult(CompileJobId JobId, CompileLifecycleReason Reason,
    CompilationInputSnapshot? Snapshot, AcceptedCompileOutput? Output = null,
    CompileOutputIssue? OutputIssue = null, ValidatedCompileAttempt? Attempt = null)
{
    public string SafeMessage => Reason == CompileLifecycleReason.Succeeded
        ? "An opaque compilation output was accepted."
        : "Compilation did not publish a new artifact.";
}

/// <summary>Completion and owned recoverable snapshot are safe to read across
/// threads. All service/workspace entry points use the single owner UI context.
/// Snapshot may become available while cancellation races capture completion;
/// it is retained before transfer, never detached or reconstructed from a reply.</summary>
public sealed class CompileLifecycleTicket
{
    private readonly TaskCompletionSource<CompileLifecycleResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource StopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _registration;
    private CompilationInputSnapshot? _snapshot;
    internal CompileLifecycleTicket(CompileLifecycleOptions? options, CancellationToken caller)
    {
        JobId = new(Guid.NewGuid()); Options = options; Caller = caller;
        _registration = caller.Register(static state => ((CompileLifecycleTicket)state!).Finish(CompileLifecycleReason.Cancelled), this);
        if (IsSettled) _registration.Unregister();
    }
    public CompileJobId JobId { get; }
    public Task<CompileLifecycleResult> Completion => _completion.Task;
    public CompilationInputSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    internal void Record(CompilationInputSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
    internal CompileLifecycleOptions? Options { get; }
    internal CancellationToken Caller { get; }
    internal bool IsSettled => _completion.Task.IsCompleted;
    internal ICompilationWorker? Worker;
    internal long Epoch;
    internal bool TerminationRequested;
    internal CompileLifecycleReason Reason => _completion.Task.Result.Reason;
    internal bool Finish(CompileLifecycleReason reason, AcceptedCompileOutput? output = null, CompileOutputIssue? issue = null,
        ValidatedCompileAttempt? attempt = null)
    {
        // The single atomic terminal result includes metadata. A foreign caller
        // cancellation winning first cannot leave metadata on a losing result.
        if (!_completion.TrySetResult(new(JobId, reason, Snapshot, output, issue, attempt))) return false;
        _registration.Unregister();
        StopSignal.TrySetResult();
        return true;
    }
}

/// <summary>LastSuccessful is explicitly historical unless IsCurrent is true
/// at this read. It owns no durable revision/history link and proves no PDF
/// validity. A later edit, A-B-A selection or session/head change invalidates it.</summary>
public sealed record CompileLifecycleView(CompileJobId? Active, CompileJobId? Pending,
    AcceptedCompileOutput? LastSuccessful, bool IsCurrent, bool Disposed, int UnsettledJobs,
    ValidatedCompileAttempt? LastAttempt = null, bool IsAttemptCurrent = false);
