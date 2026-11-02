using System.Collections.Immutable;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;

namespace Client.Workspace.History;

public enum HistoryComparisonCode { Ready, Busy, NoProject, Stale, Cancelled, CorruptContent, LimitExceeded, Unavailable, Disposed, WholeProjectRequired, NoSelection, PendingReview }
public sealed record HistoryComparisonBinding(ProjectId ProjectId, Guid SessionId, long WorkingGeneration,
    RevisionId? ExpectedHead, RevisionId Left, RevisionId? Right, bool RightIsWorking);
public sealed record HistoryComparisonOutcome(HistoryComparisonCode Code, HistoryComparisonSnapshot? Comparison = null);

/// <summary>Immutable display data, not mutation authority. Direction is always
/// Left → Right. Exact bytes for restoration are freshly read, never recovered
/// from the normalized/possibly summary-only line presentation.</summary>
public sealed class HistoryComparisonSnapshot
{
    internal HistoryComparisonSnapshot(HistoryComparisonBinding binding, ComparisonSummary summary, HistoryWorkingCapture capture, ProjectRevision leftRevision, IReadOnlyList<ProjectFile> rightFiles)
    { Binding = binding; Summary = summary; Capture = capture; LeftRevision = leftRevision; RightFiles = rightFiles.ToImmutableArray(); }
    public HistoryComparisonBinding Binding { get; }
    public ComparisonSummary Summary { get; }
    internal HistoryWorkingCapture Capture { get; }
    internal ImmutableArray<ProjectFile> RightFiles { get; }
    internal ProjectRevision LeftRevision { get; }
    public bool CanSelectTextRestore(FileId id)
    {
        var l = LeftRevision.FindFile(id); var r = RightFiles.SingleOrDefault(f => f.Id == id);
        return Binding.RightIsWorking && l is not null && r is not null && l.MediaKind == "text" && r.MediaKind == "text"
            && l.RelativePath == r.RelativePath && l.HasBom == r.HasBom && l.ContentHash != r.ContentHash;
    }
    public IEnumerable<FileChange> Filter(string? path = null, FileChangeKind? kind = null) => Summary.Changes.Where(c =>
        (kind is null || c.Kind == kind) && (string.IsNullOrEmpty(path)
        || c.Path.Contains(path, StringComparison.OrdinalIgnoreCase) || c.OldPath?.Contains(path, StringComparison.OrdinalIgnoreCase) == true));
    public FileChange? DetailedFile(FileId file) => Summary.Changes.SingleOrDefault(c => c.FileId == file && c.Diff is not null);
}

/// <summary>UI-owner-context scoped admission. Cancel invalidates immediately but
/// never releases Busy early: actual repository/materialization/view callbacks
/// must settle. No Save, active-file selection, writer requirement or tree edits
/// occur on the comparison path. Dispose invalidates without blocking the UI.</summary>
public sealed class HistoryComparisonSession : IDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly HistoryService _history;
    private readonly Func<Task>? _render;
    internal WorkspaceCoordinator Workspace => _workspace;
    internal HistoryService History => _history;
    private CancellationTokenSource? _active;
    private long _epoch;
    private bool _disposed;
    private Action? _retirePreview;
    internal void RetireOnInvalidation(Action retire) { RetirePreview(); _retirePreview = retire; }
    private void RetirePreview() { var retire = _retirePreview; _retirePreview = null; retire?.Invoke(); }
    public bool Busy { get; private set; }
    public HistoryComparisonSnapshot? Comparison { get; private set; }
    public HistoryComparisonCode Code { get; private set; } = HistoryComparisonCode.NoProject;
    public bool IsStale => Comparison is { } c && !IsCurrent(c);

    public HistoryComparisonSession(WorkspaceCoordinator workspace, HistoryService history, Func<Task>? render = null)
    { _workspace = workspace; _history = history; _render = render; workspace.WorkspaceChanged += Changed; }

    private void Changed() { _epoch++; _active?.Cancel(); RetirePreview(); }
    public void Cancel() { _epoch++; _active?.Cancel(); RetirePreview(); Code = HistoryComparisonCode.Cancelled; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Cancel(); _workspace.WorkspaceChanged -= Changed; Code = HistoryComparisonCode.Disposed;
        // The owning operation disposes its CTS only after actual settlement.
    }

    public bool IsCurrent(HistoryComparisonSnapshot comparison) => !_disposed && ReferenceEquals(Comparison, comparison)
        && comparison.Capture.Matches(_workspace, _epoch);

    internal void RequireCurrent(HistoryComparisonSnapshot comparison, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsCurrent(comparison)) throw new HistoryComparisonException(HistoryComparisonCode.Stale);
    }

    internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> body, Func<HistoryComparisonCode, T> fail, CancellationToken ct)
    {
        if (_disposed) return fail(HistoryComparisonCode.Disposed);
        if (Busy) return fail(HistoryComparisonCode.Busy);
        Busy = true;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _active = cts;
        try
        {
            cts.Token.ThrowIfCancellationRequested();
            var result = await body(cts.Token); // no WaitAsync: retain actual task admission
            if (_render is not null) await _render(); // actual dispatched render settlement, no cancellation proxy
            cts.Token.ThrowIfCancellationRequested();
            if (Comparison is { } comparison && !IsCurrent(comparison)) throw new HistoryComparisonException(HistoryComparisonCode.Stale);
            return result;
        }
        catch (OperationCanceledException) { RetirePreview(); return fail(_disposed ? HistoryComparisonCode.Disposed : HistoryComparisonCode.Cancelled); }
        catch (HistoryComparisonException e) { RetirePreview(); return fail(e.Code); }
        catch (Exception e) when (e is not OutOfMemoryException) { RetirePreview(); return fail(HistoryComparisonCode.Unavailable); }
        finally
        {
            // No newer operation can have entered while Busy. Reference guard
            // also makes this invariant explicit for future lifetime changes.
            if (ReferenceEquals(_active, cts)) { _active = null; Busy = false; }
            cts.Dispose();
        }
    }

    public Task<HistoryComparisonOutcome> CompareSavedAsync(RevisionId left, RevisionId right, CancellationToken ct)
        => CompareAsync(left, right, false, ct);
    public Task<HistoryComparisonOutcome> CompareWorkingAsync(RevisionId saved, CancellationToken ct)
        => CompareAsync(saved, null, true, ct);

    private Task<HistoryComparisonOutcome> CompareAsync(RevisionId left, RevisionId? right, bool working, CancellationToken ct)
        => RunAsync(async token =>
        {
            RetirePreview(); Comparison = null;
            var capture = HistoryWorkingCapture.Take(_workspace, _epoch);
            void Check() { token.ThrowIfCancellationRequested(); if (!capture.Matches(_workspace, _epoch)) throw new HistoryComparisonException(HistoryComparisonCode.Stale); }
            Check();
            var headOk = await _history.VerifyComparisonHeadAsync(capture.Project, capture.Head, token); Check();
            if (!headOk) throw new HistoryComparisonException(HistoryComparisonCode.Stale);
            var l = await _history.ReadVerifiedAsync(capture.Project, left, token); Check();
            IReadOnlyList<ProjectFile> rfiles;
            ProjectRevision? rightRevision = null;
            Func<ProjectFile, CancellationToken, Task<byte[]>> readRight;
            if (!working)
            {
                var r = await _history.ReadVerifiedAsync(capture.Project, right!.Value, token); Check();
                rightRevision = r; rfiles = r.Files; readRight = _history.ReadExactAsync;
            }
            else
            {
                var descriptors = ImmutableArray.CreateBuilder<ProjectFile>();
                foreach (var file in capture.Files.Where(f => !f.Deleted))
                {
                    Check();
                    var bytes = await _workspace.GetFileBytesAsync(file, token); Check();
                    HistoryWorkingCapture.CheckLength(bytes.LongLength);
                    descriptors.Add(new(file.Id, file.RelativePath, file.MediaKind, HistoryService.Hash(bytes), bytes.LongLength, file.HasBom));
                    await Task.Yield(); Check();
                }
                rfiles = descriptors.ToImmutable();
                readRight = async (file, cancellation) =>
                {
                    Check();
                    var bytes = await _workspace.GetFileBytesAsync(capture.Files.Single(f => f.Id == file.Id), cancellation); Check();
                    // Shared binary/import buffers are cloned by the capture;
                    // durable buffers are owned and verified on every read.
                    if (bytes.LongLength != file.ByteLength) throw new HistoryComparisonException(HistoryComparisonCode.CorruptContent);
                    var owned = bytes.ToArray(); HistoryService.VerifyBytes(file, owned); return owned;
                };
            }
            var summary = await _history.CompareExactAsync(l.Files, rfiles, _history.ReadExactAsync, readRight, Check, token); Check();
            var lVerified = await _history.ReadVerifiedAsync(capture.Project, left, token); Check();
            if (!HistoryService.SameRevision(l, lVerified)) throw new HistoryComparisonException(HistoryComparisonCode.Stale);
            if (rightRevision is not null)
            {
                var rVerified = await _history.ReadVerifiedAsync(capture.Project, rightRevision.Id, token); Check();
                if (!HistoryService.SameRevision(rightRevision, rVerified)) throw new HistoryComparisonException(HistoryComparisonCode.Stale);
            }
            headOk = await _history.VerifyComparisonHeadAsync(capture.Project, capture.Head, token); Check();
            if (!headOk) throw new HistoryComparisonException(HistoryComparisonCode.Stale);
            var binding = new HistoryComparisonBinding(capture.Project, capture.Session, capture.Generation, capture.Head, left, right, working);
            var result = new HistoryComparisonSnapshot(binding, summary, capture, l, rfiles);
            Check(); Comparison = result; Code = HistoryComparisonCode.Ready;
            return new HistoryComparisonOutcome(Code, result);
        }, code => { if (code != HistoryComparisonCode.Busy) { Comparison = null; Code = code; } return new(code); }, ct);
}

// Captures public mutable working models defensively, including exact binary /
// imported buffers. Full preimages (not just generation or hash) are checked
// around awaits, so direct model alias mutation cannot resurrect authority.
internal sealed class HistoryWorkingCapture
{
    internal ProjectId Project { get; private init; }
    internal Guid Session { get; private init; }
    internal long Generation { get; private init; }
    internal RevisionId? Head { get; private init; }
    private ProjectRevision? Revision { get; init; }
    private long Epoch { get; init; }
    internal ImmutableArray<WorkingFile> Files { get; private init; }
    internal static void CheckLength(long length)
    { if (length < 0 || length > ArchiveLimits.SingleFile) throw new HistoryComparisonException(HistoryComparisonCode.LimitExceeded); }
    internal static HistoryWorkingCapture Take(WorkspaceCoordinator workspace, long epoch)
    {
        if (workspace.Project is not { } p) throw new HistoryComparisonException(HistoryComparisonCode.NoProject);
        if (workspace.Files.Count > ArchiveLimits.MaxEntries) throw new HistoryComparisonException(HistoryComparisonCode.LimitExceeded);
        long retained = 0, materialized = 0;
        foreach (var f in workspace.Files)
        {
            // Preflight ALL alias buffers (even inactive buffers) before copying.
            foreach (var bytes in new[] { f.BinaryContent, f.ImportedBytes })
                if (bytes is not null) { CheckLength(bytes.LongLength); retained += bytes.LongLength; }
            long length;
            if (f.MediaKind == "binary") length = f.DirtyBinary ? f.BinaryContent?.LongLength ?? f.DurableLength : f.DurableHash is null ? f.BinaryContent?.LongLength ?? 0 : f.DurableLength;
            else if (f.PendingTextBytes is { } pending) length = pending.Length;
            else if (f.IsNew && !f.DirtyContent && f.ImportedBytes is { } imported) length = imported.LongLength;
            else if (!f.DirtyContent && !f.IsNew && f.DurableHash is not null) length = f.DurableLength;
            else if (!TextContent.TryCountEditedBytes(f.Content, f.HasBom, out length)) throw new HistoryComparisonException(HistoryComparisonCode.CorruptContent);
            CheckLength(length); materialized += length;
            if (retained > ArchiveLimits.ExpandedSize || materialized > ArchiveLimits.ExpandedSize)
                throw new HistoryComparisonException(HistoryComparisonCode.LimitExceeded);
        }
        var files = workspace.Files.Select(f => { var clone = f.Clone(); clone.BinaryContent = f.BinaryContent?.ToArray(); clone.ImportedBytes = f.ImportedBytes?.ToArray(); return clone; }).ToImmutableArray();
        return new() { Project = p.Id, Session = workspace.SessionId, Generation = workspace.WorkingGeneration,
            Head = p.HeadRevisionId, Revision = workspace.CurrentRevision, Epoch = epoch, Files = files };
    }
    internal bool Matches(WorkspaceCoordinator w, long epoch) => Epoch == epoch && w.Project?.Id == Project && w.SessionId == Session
        && w.WorkingGeneration == Generation && w.Project.HeadRevisionId == Head && ReferenceEquals(w.CurrentRevision, Revision)
        && w.Files.Count == Files.Length && w.Files.Zip(Files).All(pair => Same(pair.First, pair.Second));
    private static bool Bytes(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    private static bool Same(WorkingFile a, WorkingFile b) => a.Id == b.Id && a.RelativePath == b.RelativePath && a.MediaKind == b.MediaKind
        && a.Content == b.Content && a.HasBom == b.HasBom && a.DirtyContent == b.DirtyContent && a.DirtyBinary == b.DirtyBinary
        && a.TreeChanged == b.TreeChanged && a.IsNew == b.IsNew && a.Deleted == b.Deleted && a.DurableHash == b.DurableHash
        && a.DurableLength == b.DurableLength && ReferenceEquals(a.PendingTextBytes, b.PendingTextBytes)
        && Bytes(a.BinaryContent, b.BinaryContent) && Bytes(a.ImportedBytes, b.ImportedBytes);
}
