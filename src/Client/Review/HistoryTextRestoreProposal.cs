using System.Collections.Immutable;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Patching;

namespace Client.Review;

public sealed record HistoryTextRestoreCreation(HistoryComparisonCode Code, HistoryTextRestoreProposal? Proposal = null,
    ImmutableArray<FileId> WholeProjectFiles = default);
public sealed record HistoryTextRestorePreview(HistoryComparisonCode Code, ReviewResult? Review = null);

/// <summary>A user proposal, never trusted rule provenance. A selection is made
/// only from a saved-left → working-right comparison. Each subset is freshly
/// read/prepared against that original authority; no cherry-picking diff lines,
/// structural edits, fallback editor Undo, or private mutation/save path.</summary>
public sealed class HistoryTextRestoreProposal
{
    private readonly HistoryComparisonSession _session;
    private readonly HistoryComparisonSnapshot _comparison;
    private readonly TextChangeSet _set;
    private bool _consumed;
    private HistoryTextRestoreProposal(HistoryComparisonSession session, HistoryComparisonSnapshot comparison,
        TextChangeSet set, PreparedTextBatch exact)
    { _session = session; _comparison = comparison; _set = set; Exact = exact; }
    public PreparedTextBatch Exact { get; }
    public HistoryComparisonBinding Binding => _comparison.Binding;
    public bool IsCurrent => !_consumed && _session.IsCurrent(_comparison);

    public static Task<HistoryTextRestoreCreation> CreateAsync(HistoryComparisonSession session,
        IReadOnlyCollection<FileId> selectedFiles, CancellationToken ct)
    {
        // Own selection synchronously, before admission or the first await.
        if (selectedFiles is null || selectedFiles.Count == 0 || selectedFiles.Count > WorkspaceTextLimits.FileCount)
            return Task.FromResult(new HistoryTextRestoreCreation(HistoryComparisonCode.NoSelection));
        var selected = selectedFiles.ToImmutableArray();
        if (selected.Distinct().Count() != selected.Length)
            return Task.FromResult(new HistoryTextRestoreCreation(HistoryComparisonCode.NoSelection));
        return session.RunAsync(async token =>
        {
            if (session.Comparison is not { } comparison) return new HistoryTextRestoreCreation(HistoryComparisonCode.Stale);
            void Check() => session.RequireCurrent(comparison, token);
            Check();
            if (!comparison.Binding.RightIsWorking) return new HistoryTextRestoreCreation(HistoryComparisonCode.WholeProjectRequired, WholeProjectFiles: selected);
            var historical = await session.History.ReadVerifiedAsync(comparison.Binding.ProjectId, comparison.Binding.Left, token); Check();
            if (!HistoryService.SameRevision(historical, comparison.LeftRevision)) return new HistoryTextRestoreCreation(HistoryComparisonCode.Stale);
            var routed = selected.Where(id =>
            {
                var old = historical.FindFile(id); var current = comparison.RightFiles.SingleOrDefault(f => f.Id == id);
                return old is null || current is null || old.MediaKind != "text" || current.MediaKind != "text"
                    || old.RelativePath != current.RelativePath || old.HasBom != current.HasBom;
            }).ToImmutableArray();
            if (!routed.IsEmpty) return new HistoryTextRestoreCreation(HistoryComparisonCode.WholeProjectRequired, WholeProjectFiles: routed);

            // Preflight aggregate source/replacement budgets before any byte
            // copies. The existing preparer performs the complete final check.
            if (comparison.RightFiles.Length > WorkspaceTextLimits.FileCount
                || comparison.RightFiles.Sum(f => f.ByteLength) > WorkspaceTextLimits.ProjectBytes
                || comparison.RightFiles.Any(f => f.MediaKind == "text" && f.ByteLength > WorkspaceTextLimits.FileBytes)
                || selected.Sum(id => historical.FindFile(id)!.ByteLength) > WorkspaceTextLimits.ProjectBytes
                || selected.Any(id => historical.FindFile(id)!.ByteLength > WorkspaceTextLimits.FileBytes))
                return new HistoryTextRestoreCreation(HistoryComparisonCode.LimitExceeded);
            var patches = new List<FilePatchInput>();
            var beforeById = new Dictionary<FileId, byte[]>();
            foreach (var id in selected)
            {
                Check();
                var old = historical.FindFile(id)!; var current = comparison.RightFiles.Single(f => f.Id == id);
                var after = await session.History.ReadExactAsync(old, token); Check();
                var bytes = await session.Workspace.GetFileBytesAsync(comparison.Capture.Files.Single(f => f.Id == id), token); Check();
                if (bytes.LongLength != current.ByteLength) throw new HistoryComparisonException(HistoryComparisonCode.CorruptContent);
                var before = bytes.ToArray(); HistoryService.VerifyBytes(current, before);
                // Both BOM flags AND actual bytes were verified. Existing patch
                // rules prohibit touching BOM bytes; replace the complete body.
                if (!before.AsSpan().SequenceEqual(after))
                {
                    int start = current.HasBom ? 3 : 0;
                    patches.Add(new(id, current.RelativePath, current.ContentHash, before.LongLength,
                        [new(start, before.LongLength, before.AsMemory(start), after.AsMemory(start))]));
                    beforeById.Add(id, before);
                }
                await Task.Yield(); Check();
            }
            if (patches.Count == 0) return new HistoryTextRestoreCreation(HistoryComparisonCode.NoSelection);
            var set = TextChangeSet.Create(TextPatchOrigin.User, "Restore the explicitly selected text files from saved history", patches);
            if (set.ChangeSet is null) return new HistoryTextRestoreCreation(HistoryComparisonCode.Unavailable);
            var sources = comparison.RightFiles.Select(f => new PatchSourceInput(f.Id, f.RelativePath, f.MediaKind, f.ByteLength, f.HasBom,
                beforeById.TryGetValue(f.Id, out var before) ? before.AsMemory() : null)).ToArray();
            var binding = comparison.Binding;
            var prepared = PreparedTextBatch.Prepare(new(binding.ProjectId, binding.SessionId, binding.WorkingGeneration, binding.ExpectedHead),
                patches, sources, TextPatchOrigin.User, set.ChangeSet.Explanation);
            Check();
            var finalHistorical = await session.History.ReadVerifiedAsync(binding.ProjectId, binding.Left, token); Check();
            if (!HistoryService.SameRevision(historical, finalHistorical)) return new HistoryTextRestoreCreation(HistoryComparisonCode.Stale);
            var headOk = await session.History.VerifyComparisonHeadAsync(binding.ProjectId, binding.ExpectedHead, token); Check();
            if (!headOk) return new HistoryTextRestoreCreation(HistoryComparisonCode.Stale);
            return prepared.Batch is null ? new HistoryTextRestoreCreation(HistoryComparisonCode.Unavailable)
                : new HistoryTextRestoreCreation(HistoryComparisonCode.Ready, new(session, comparison, set.ChangeSet, prepared.Batch));
        }, code => new HistoryTextRestoreCreation(code), ct);
    }

    /// <summary>Must be called explicitly after selection. Parent displays the
    /// returned ordinary review preview; accept/save/Undo/retry/export stay in
    /// the existing review workflow. Declining/retrying requires fresh creation.
    /// Shared review actual-settlement patch is an integration prerequisite.</summary>
    public Task<HistoryTextRestorePreview> PreviewAsync(IChangeReviewService review, CancellationToken ct, Func<ReviewPreview, Task>? present = null)
        => _session.RunAsync(async token =>
        {
            _session.RequireCurrent(_comparison, token);
            if (_consumed) return new HistoryTextRestorePreview(HistoryComparisonCode.Stale);
            var state = review.State;
            if (state.Busy || state.Preview is not null || state.Action is { Persisted: false })
                return new HistoryTextRestorePreview(HistoryComparisonCode.PendingReview);
            _consumed = true;
            await VerifyHistoricalAuthorityAsync(token);
            var result = await review.PreviewAsync(_set, token); // actual owned review task, not WaitAsync
            if (!_session.IsCurrent(_comparison) || token.IsCancellationRequested)
            {
                // Retire only the preview we created. Never cancel/decline a
                // newer independent action or discard an accepted recovery.
                if (result.Preview is { } p && review.State.Preview?.Id == p.Id) review.Decline(p.Id);
                token.ThrowIfCancellationRequested();
                return new HistoryTextRestorePreview(HistoryComparisonCode.Stale);
            }
            if (result.Preview is { } owned)
            {
                _session.RetireOnInvalidation(() =>
                {
                    if (review.State.Preview?.Id != owned.Id) return;
                    if (review.State.Busy) review.Cancel(); else review.Decline(owned.Id);
                });
                await VerifyHistoricalAuthorityAsync(token);
                if (present is not null) await present(owned);
                _session.RequireCurrent(_comparison, token);
                if (review.State.Preview?.Id != owned.Id) return new HistoryTextRestorePreview(HistoryComparisonCode.Stale);
            }
            return new HistoryTextRestorePreview(result.Preview is null ? HistoryComparisonCode.Unavailable : HistoryComparisonCode.Ready, result);
        }, code => new HistoryTextRestorePreview(code), ct);

    private async Task VerifyHistoricalAuthorityAsync(CancellationToken ct)
    {
        _session.RequireCurrent(_comparison, ct);
        var historical = await _session.History.ReadVerifiedAsync(Binding.ProjectId, Binding.Left, ct);
        _session.RequireCurrent(_comparison, ct);
        if (!HistoryService.SameRevision(historical, _comparison.LeftRevision)) throw new HistoryComparisonException(HistoryComparisonCode.Stale);
        var headOk = await _session.History.VerifyComparisonHeadAsync(Binding.ProjectId, Binding.ExpectedHead, ct);
        _session.RequireCurrent(_comparison, ct);
        if (!headOk) throw new HistoryComparisonException(HistoryComparisonCode.Stale);
    }
}
