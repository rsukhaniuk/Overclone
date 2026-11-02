using System.Collections.Immutable;
using System.Text;
using Client.Review;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Patching;
using Client.Workspace.Transfer;

namespace Client.Editing.Search;

public enum ProjectReplaceCode { Ready, Busy, NoSelection, Incomplete, InvalidInput, Stale, ReadOnly, Disposed, LimitExceeded }
public sealed record ProjectReplaceCreation(ProjectReplaceCode Code, ProjectReplaceRequest? Request = null);

/// <summary>Owned selection intent, not an accepted edit or a prepared preview.
/// Parent hands this to its ordinary review admission wrapper. Every invocation
/// freshly captures/prepares exact bytes under the original search authority.</summary>
public sealed class ProjectReplaceRequest
{
    internal ProjectReplaceRequest(ProjectReplaceSession owner, long intent, ProjectSearchReport report,
        ImmutableArray<ProjectSearchMatch> matches, string replacement, bool all)
    { Owner = owner; Intent = intent; Report = report; Matches = matches; Replacement = replacement; AllComplete = all; }
    private bool _started;
    internal bool TryStart() { if (_started) return false; _started = true; return true; }
    internal ProjectReplaceSession Owner { get; }
    internal long Intent { get; }
    internal ProjectSearchReport Report { get; }
    internal ImmutableArray<ProjectSearchMatch> Matches { get; }
    internal string Replacement { get; }
    public bool AllComplete { get; }
    public int MatchCount => Matches.Length;
    public bool FromIncompleteListing => Report.Status == ProjectSearchStatus.Truncated;
    public Task<ReviewResult> PreviewAsync(IChangeReviewService review, CancellationToken ct) => Owner.PreviewAsync(this, review, ct);
}

/// <summary>Borrowed owners only. No EditText, Save, rule provenance or parallel
/// undo log. Cancel/change retires ONLY this session's unaccepted preview; the
/// existing generic review retains accepted save-failure recovery and inverse.</summary>
public sealed class ProjectReplaceSession : IDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly ProjectSearchSession _search;
    private readonly IWorkspaceLocks _locks;
    private readonly IProjectRepository _repository;
    private CancellationTokenSource? _active;
    private long _intent;
    private bool _disposed;
    private Action? _retire;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public bool Busy => _active is not null;
    public bool CanRequest => !_disposed && !Busy && !_search.Busy && Writable && _search.IsCurrentReport(_search.Report);
    private bool Writable
    {
        get
        {
            var project = _workspace.Project;
            return project is not null && !_workspace.IsReadOnly && _workspace.Recovery is null
                && _workspace.Capabilities?.WebLocks == true && _workspace.LockId is { } id
                && _locks.IsOwnedLocally(id, "oop-project:" + project.Id);
        }
    }
    public ProjectReplaceSession(WorkspaceCoordinator workspace, ProjectSearchSession search, IWorkspaceLocks locks, IProjectRepository repository)
    {
        if (!ReferenceEquals(workspace, search.Workspace)) throw new ArgumentException("Search belongs to another workspace.", nameof(search));
        _workspace = workspace; _search = search; _locks = locks; _repository = repository; search.Changed += Invalidate;
    }
    private void Retire() { var retire = _retire; _retire = null; retire?.Invoke(); }
    public void Invalidate() { _intent++; _active?.Cancel(); Retire(); }
    public void Cancel() => Invalidate();
    public void Dispose() { if (_disposed) return; _disposed = true; _search.Changed -= Invalidate; Invalidate(); }

    public ProjectReplaceCreation CreateRequest(IReadOnlyCollection<ProjectSearchMatch> selected, string replacement, bool allComplete = false)
    {
        if (_disposed) return new(ProjectReplaceCode.Disposed);
        Invalidate();
        if (Busy || _search.Busy) return new(ProjectReplaceCode.Busy);
        if (!Writable) return new(ProjectReplaceCode.ReadOnly);
        var report = _search.Report;
        if (!_search.IsCurrentReport(report)) return new(ProjectReplaceCode.Stale);
        if (allComplete && report.Status != ProjectSearchStatus.Complete) return new(ProjectReplaceCode.Incomplete);
        if (replacement is null || !SourceTools.ValidText(replacement, out _) || selected is null) return new(ProjectReplaceCode.InvalidInput);
        if (selected.Count == 0) return new(ProjectReplaceCode.NoSelection);
        if (selected.Count > 500) return new(ProjectReplaceCode.LimitExceeded);
        var matches = selected.Take(501).ToImmutableArray();
        if (matches.Length != selected.Count || matches.Length > 500) return new(ProjectReplaceCode.InvalidInput);
        if (matches.Any(m => m is null) || matches.Distinct().Count() != matches.Length
            || matches.Any(m => !report.Matches.Any(issued => ReferenceEquals(issued, m)))) return new(ProjectReplaceCode.InvalidInput);
        if (allComplete && (matches.Length != report.Matches.Length || report.Matches.Any(m => !matches.Contains(m)))) return new(ProjectReplaceCode.Incomplete);
        if (matches.Select(m => m.FileId).Distinct().Count() > 100) return new(ProjectReplaceCode.LimitExceeded);
        return new(ProjectReplaceCode.Ready, new(this, _intent, report, matches, replacement, allComplete));
    }

    internal async Task<ReviewResult> PreviewAsync(ProjectReplaceRequest request, IChangeReviewService review, CancellationToken ct)
    {
        ReviewResult Fail(ReviewCode code, string notice) => new(review.State.Status, Failure: new(code, notice));
        if (_disposed) return Fail(ReviewCode.Disposed, "Replacement session is disposed.");
        if (Busy) return Fail(ReviewCode.Busy, "Previous replacement work has not settled.");
        if (!ReferenceEquals(request.Owner, this) || request.Intent != _intent || !_search.IsCurrentReport(request.Report))
            return Fail(ReviewCode.Stale, "The search or replacement selection changed; request a fresh preview.");
        if (!Writable) return Fail(ReviewCode.ReadOnly, "Replacement preview requires the current writer session.");
        if (review.State.Busy || review.State.Preview is not null || review.State.Action is { Persisted: false })
            return Fail(ReviewCode.PendingSave, "Resolve the existing preview or unsaved reviewed action first; recovery is retained.");
        if (!request.TryStart()) return Fail(ReviewCode.Stale, "This replacement request was already used; select and preview freshly.");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); _active = cts;
        var identity = _workspace.CurrentTextPatchIdentity!; var lockId = _workspace.LockId!;
        var exactClones = new Dictionary<FileId, WorkingFile>();
        bool Current() => !_disposed && !cts.IsCancellationRequested && request.Intent == _intent
            && _workspace.CurrentTextPatchIdentity == identity && _workspace.Project?.HeadRevisionId == identity.ExpectedHead
            && _workspace.LockId == lockId && Writable && _search.IsCurrentReport(request.Report)
            && exactClones.All(pair =>
            {
                var live = _workspace.Files.SingleOrDefault(f => f.Id == pair.Key && !f.Deleted); var clone = pair.Value;
                return live is not null && (clone.ImportedBytes is null ? live.ImportedBytes is null
                    : live.ImportedBytes is { } bytes && bytes.AsSpan().SequenceEqual(clone.ImportedBytes));
            });
        void Check() { cts.Token.ThrowIfCancellationRequested(); if (!Current()) throw new ReplaceStaleException(); }
        async Task VerifyAsync()
        {
            Check();
            bool head = await _repository.VerifyHeadAsync(identity.ProjectId, identity.ExpectedHead, cts.Token); Check();
            if (!head) throw new ReplaceStaleException();
            bool owns = await _locks.OwnsAsync(lockId, "oop-project:" + identity.ProjectId, cts.Token); Check();
            if (!owns) throw new ReplaceWriterException();
        }
        try
        {
            Check();
            if ((long)Utf8.GetByteCount(request.Replacement) * request.Matches.Length > WorkspaceTextLimits.ProjectBytes)
                return Fail(ReviewCode.InvalidInput, "Selected replacement bytes exceed the bounded text budget.");
            var replacement = Utf8.GetBytes(request.Replacement);
            var capture = _search.Capture!;
            var inputs = new List<FilePatchInput>(); var beforeById = new Dictionary<FileId, byte[]>();
            foreach (var group in request.Matches.GroupBy(m => m.FileId))
            {
                var frozen = capture.Files.Single(f => f.Frozen.Id == group.Key).Frozen.Clone();
                if (WorkingLength(frozen) > WorkspaceTextLimits.FileBytes) return Fail(ReviewCode.InvalidInput, "A selected text file exceeds the review byte budget.");
                frozen.ImportedBytes = frozen.ImportedBytes?.ToArray(); exactClones.Add(group.Key, frozen);
            }
            await VerifyAsync();
            foreach (var group in request.Matches.GroupBy(m => m.FileId))
            {
                Check(); var frozen = exactClones[group.Key];
                var raw = await _workspace.GetFileBytesAsync(frozen, cts.Token); Check();
                if (raw.LongLength > WorkspaceTextLimits.FileBytes || raw.LongLength != WorkingLength(frozen))
                    return Fail(ReviewCode.Conflict, "Exact source length differs from the captured search input.");
                var before = raw.ToArray();
                if (!TextContent.TryDecode(before, out var text, out var bom) || bom != frozen.HasBom)
                    return Fail(ReviewCode.InvalidInput, "Exact source is not valid byte-preserving text.");
                bool canonical = frozen.PendingTextBytes is null && !(frozen.IsNew && !frozen.DirtyContent && frozen.ImportedBytes is not null)
                    && (frozen.DirtyContent || frozen.IsNew && frozen.ImportedBytes is null);
                var expected = canonical ? SourceTools.TextareaText(frozen.Content) : frozen.Content;
                if (!string.Equals(expected, text, StringComparison.Ordinal)) return Fail(ReviewCode.Conflict, "Exact source preimage differs from the searched text.");
                var edits = new List<TextEditInput>(); int previousChar = 0, previousByte = bom ? 3 : 0;
                foreach (var match in group.OrderBy(m => m.Start))
                {
                    if (!SourceTools.ValidRange(frozen.Content, new(match.Start, match.End))) return Fail(ReviewCode.InvalidInput, "A selected match has an invalid source boundary.");
                    var mapped = canonical ? SourceTools.ToTextarea(frozen.Content, new(match.Start, match.End)) : new SourceRange(match.Start, match.End);
                    if (mapped is not { } range || range.Start < previousChar) return Fail(ReviewCode.InvalidInput, "Selected replacements overlap or have an invalid source boundary.");
                    int start = checked(previousByte + Utf8.GetByteCount(text.AsSpan(previousChar, range.Start - previousChar)));
                    int end = checked(start + Utf8.GetByteCount(text.AsSpan(range.Start, range.End - range.Start)));
                    var original = before.AsMemory(start, end - start);
                    if (!original.Span.SequenceEqual(replacement)) edits.Add(new(start, end, original, replacement));
                    previousChar = range.End; previousByte = end;
                }
                if (edits.Count != 0)
                {
                    beforeById.Add(group.Key, before);
                    inputs.Add(new(group.Key, frozen.RelativePath, Hash(before), before.LongLength, edits));
                }
                // Preparation itself also remains cancellable between files.
                await Task.Delay(1, cts.Token); Check();
            }
            if (inputs.Count == 0) return Fail(ReviewCode.InvalidInput, "Selected replacements do not change any exact bytes.");
            var explanation = request.AllComplete ? "Replace all complete current literal search matches"
                : request.FromIncompleteListing ? "Replace explicitly selected listed matches from an incomplete search; this is not Replace all"
                : "Replace the explicitly selected current literal search matches";
            var set = TextChangeSet.Create(TextPatchOrigin.User, explanation, inputs);
            if (set.ChangeSet is null) return Fail(ReviewCode.InvalidInput, "The selected literal replacement exceeds the bounded review limits.");
            // Existing preparer is the only patch grammar, including UTF8/BOM,
            // overlap/no-op/project budget rules. Review freshly prepares again.
            var sources = capture.Files.Select(f => new PatchSourceInput(f.Frozen.Id, f.Frozen.RelativePath, f.Frozen.MediaKind,
                WorkingLength(f.Frozen), f.Frozen.HasBom, beforeById.TryGetValue(f.Frozen.Id, out var b) ? b.AsMemory() : null)).ToArray();
            if (PreparedTextBatch.Prepare(identity, inputs, sources, TextPatchOrigin.User, explanation).Batch is null)
                return Fail(ReviewCode.InvalidInput, "The selected literal replacement could not be prepared safely.");
            await VerifyAsync();
            if (review.State.Busy || review.State.Preview is not null || review.State.Action is { Persisted: false })
                return Fail(ReviewCode.PendingSave, "Resolve the existing preview or unsaved action first; recovery is retained.");
            var result = await review.PreviewAsync(set.ChangeSet, cts.Token);
            if (result.Preview is { } owned)
                _retire = () => { if (review.State.Preview?.Id != owned.Id) return; if (review.State.Busy) review.Cancel(); else review.Decline(owned.Id); };
            Check(); await VerifyAsync();
            if (result.Preview is { } issued && review.State.Preview?.Id != issued.Id)
            { Retire(); return Fail(ReviewCode.Stale, "A newer review intent replaced this preview; inspect the current review state."); }
            return result;
        }
        catch (OperationCanceledException) { Retire(); return Fail(ReviewCode.Cancelled, "Replacement preview cancelled. Inspect the current review/action state; any accepted recovery is retained."); }
        catch (ReplaceWriterException) { Retire(); return Fail(ReviewCode.ReadOnly, "The writer lock is no longer owned by this session."); }
        catch (ReplaceStaleException) { Retire(); return Fail(ReviewCode.Stale, "The search, selection, source or head changed; request a fresh preview."); }
        catch (Exception e) when (e is not OutOfMemoryException) { Retire(); return Fail(ReviewCode.Unavailable, "Replacement preview could not finish safely; recovery is retained."); }
        finally { if (ReferenceEquals(_active, cts)) _active = null; cts.Dispose(); }
    }
    private static SHA256 Hash(byte[] bytes) => SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes));
    private static long WorkingLength(WorkingFile f)
    {
        if (f.MediaKind != "text") return f.DirtyBinary || f.DurableHash is null ? f.BinaryContent?.LongLength ?? 0 : f.DurableLength;
        if (f.PendingTextBytes is { } pending) return pending.Length;
        if (f.IsNew && !f.DirtyContent && f.ImportedBytes is { } imported) return imported.LongLength;
        if (!f.DirtyContent && !f.IsNew && f.DurableHash is not null) return f.DurableLength;
        if (!TextContent.TryCountEditedBytes(f.Content, f.HasBom, out long length)) throw new ReplaceStaleException();
        return length;
    }
    private sealed class ReplaceStaleException : Exception { }
    private sealed class ReplaceWriterException : Exception { }
}
