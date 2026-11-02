using System.Collections.Immutable;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;

namespace Client.Workspace.History;

/// <summary>
/// History listing, bounded read-only comparison and restore-preview policy.
/// C# owns all of it; the repository supplies verified revision manifests and
/// raw content bytes. Comparison views are read-only and render content as text
/// (never raw HTML); binary changes report sizes/hashes, never decoded text.
/// Diff computation is bounded and falls back to a summary when its budget is
/// exhausted. Restore itself is committed by the coordinator under the writer
/// lock; this service only computes the preview that the coordinator validates
/// again at acceptance.
/// </summary>
public sealed partial class HistoryService
{
    private readonly IProjectRepository _repo;

    public HistoryService(IProjectRepository repo) => _repo = repo;

    /// <summary>Read one page of checkpoint metadata, newest first, 50/page.</summary>
    public Task<HistoryPage> ReadHistoryAsync(ProjectId projectId, HistoryCursor? cursor, CancellationToken ct)
        => _repo.ReadHistoryAsync(projectId, cursor, ct);

    /// <summary>Compare a historical revision with a captured current durable
    /// head. Both are loaded as verified manifests; content is read on demand.
    /// Text differences get a bounded line diff; binary differences report
    /// sizes/hashes. Oversized or budget-exhausted files fall back to a summary.</summary>
    public async Task<ComparisonSummary> CompareAsync(
        ProjectId projectId,
        RevisionId baseRevisionId,
        RevisionId historicalRevisionId,
        CancellationToken ct)
    {
        var baseR = await _repo.ReadRevisionAsync(projectId, baseRevisionId, ct);
        var histR = await _repo.ReadRevisionAsync(projectId, historicalRevisionId, ct);
        if (!baseR.Verified || !histR.Verified)
            throw new InvalidOperationException(
                (baseR.Verified ? "" : ("Base revision is damaged: " + baseR.FailureReason + " ")) +
                (histR.Verified ? "" : ("Historical revision is damaged: " + histR.FailureReason)));

        return await CompareRevisions(baseR.Revision, histR.Revision, ct);
    }

    /// <summary>Compare two already-verified revisions. Exposed for the
    /// coordinator's restore preview, which compares the current working
    /// (in-memory) revision against a historical one.</summary>
    public async Task<ComparisonSummary> CompareRevisions(
        ProjectRevision baseRevision,
        ProjectRevision historicalRevision,
        CancellationToken ct)
    {
        var byIdBase = baseRevision.Files.ToDictionary(f => f.Id);
        var byIdHist = historicalRevision.Files.ToDictionary(f => f.Id);

        var changes = new List<FileChange>();
        var oversized = new List<string>();
        bool budgetExceeded = false;

        // Files present in historical (the "new" side of restore) and in base.
        foreach (var hf in historicalRevision.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (byIdBase.TryGetValue(hf.Id, out var bf))
            {
                // Same stable file ID.
                bool renamed = bf.RelativePath != hf.RelativePath;
                bool contentChanged = bf.ContentHash.Hex != hf.ContentHash.Hex;
                if (!renamed && !contentChanged)
                {
                    changes.Add(new FileChange(hf.Id, hf.RelativePath, FileChangeKind.Unchanged,
                        null, bf.ByteLength, hf.ByteLength, bf.ContentHash, hf.ContentHash, null));
                    continue;
                }
                if (contentChanged)
                {
                    bool isText = hf.MediaKind == "text" && bf.MediaKind == "text";
                    FileChangeKind kind = isText ? FileChangeKind.ContentChanged : FileChangeKind.BinaryChanged;
                    var diff = await MaybeDiff(bf, hf, isText, ct);
                    if (diff == null && isText) { oversized.Add(hf.RelativePath); budgetExceeded = true; }
                    changes.Add(new FileChange(hf.Id, hf.RelativePath, kind,
                        renamed ? bf.RelativePath : null,
                        bf.ByteLength, hf.ByteLength, bf.ContentHash, hf.ContentHash, diff,
                        diff == null && isText ? "Detailed diff skipped: size or budget exceeded." : null));
                }
                else // renamed only
                {
                    changes.Add(new FileChange(hf.Id, hf.RelativePath, FileChangeKind.Renamed,
                        bf.RelativePath, bf.ByteLength, hf.ByteLength, bf.ContentHash, hf.ContentHash, null));
                }
            }
            else
            {
                // Added in historical.
                changes.Add(new FileChange(hf.Id, hf.RelativePath, FileChangeKind.Added,
                    null, 0, hf.ByteLength, null, hf.ContentHash, null));
            }
        }

        // Files removed (present in base, not in historical).
        foreach (var bf in baseRevision.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (!byIdHist.ContainsKey(bf.Id))
            {
                changes.Add(new FileChange(bf.Id, bf.RelativePath, FileChangeKind.Removed,
                    null, bf.ByteLength, 0, bf.ContentHash, null, null));
            }
        }

        return new ComparisonSummary(changes, budgetExceeded, oversized);
    }

    private async Task<IReadOnlyList<DiffLine>?> MaybeDiff(
        ProjectFile oldFile, ProjectFile newFile, bool isText, CancellationToken ct)
    {
        if (!isText) return null;
        if (oldFile.ByteLength > ArchiveLimits.DiffByteBudget ||
            newFile.ByteLength > ArchiveLimits.DiffByteBudget)
            return null;

        var oldBytes = await _repo.ReadContentAsync(oldFile.ContentHash, ct);
        var newBytes = await _repo.ReadContentAsync(newFile.ContentHash, ct);

        var oldText = TextContent.DecodeStrict(stripBom(oldBytes));
        var newText = TextContent.DecodeStrict(stripBom(newBytes));
        var oldLines = TextContent.SplitLines(oldText);
        var newLines = TextContent.SplitLines(newText);
        if (oldLines.Count > ArchiveLimits.DiffLineBudget || newLines.Count > ArchiveLimits.DiffLineBudget)
            return null;

        return LineDiff.Compute(oldLines, newLines);
    }

    private static byte[] stripBom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            var r = new byte[bytes.Length - 3];
            Buffer.BlockCopy(bytes, 3, r, 0, r.Length);
            return r;
        }
        return bytes;
    }
}
