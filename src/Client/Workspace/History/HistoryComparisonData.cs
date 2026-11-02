using System.Collections.Immutable;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;

namespace Client.Workspace.History;

public sealed partial class HistoryService
{
    internal Task<bool> VerifyComparisonHeadAsync(ProjectId project, RevisionId? head, CancellationToken ct)
        => _repo.VerifyHeadAsync(project, head, ct);
    internal async Task<ProjectRevision> ReadVerifiedAsync(ProjectId project, RevisionId id, CancellationToken ct)
    {
        var read = await _repo.ReadRevisionAsync(project, id, ct);
        ct.ThrowIfCancellationRequested();
        if (!read.Verified || read.Revision.ProjectId != project || read.Revision.Id != id)
            throw new HistoryComparisonException(HistoryComparisonCode.CorruptContent);
        ValidateFiles(read.Revision.Files);
        return read.Revision;
    }

    internal async Task<byte[]> ReadExactAsync(ProjectFile file, CancellationToken ct)
    {
        var bytes = await _repo.ReadContentAsync(file.ContentHash, ct);
        ct.ThrowIfCancellationRequested();
        // Own the returned storage buffer before hashing or decoding it.
        if (bytes.LongLength != file.ByteLength) throw new HistoryComparisonException(HistoryComparisonCode.CorruptContent);
        bytes = bytes.ToArray();
        VerifyBytes(file, bytes);
        return bytes;
    }

    internal static SHA256 Hash(byte[] bytes) => SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes));

    internal static bool SameRevision(ProjectRevision a, ProjectRevision b) => a.Id == b.Id && a.ProjectId == b.ProjectId
        && a.ParentRevisionId == b.ParentRevisionId && a.CreatedAt == b.CreatedAt && a.Files.SequenceEqual(b.Files);

    internal static void VerifyBytes(ProjectFile file, byte[] bytes)
    {
        if (bytes.LongLength != file.ByteLength || Hash(bytes) != file.ContentHash
            || file.MediaKind == "text" && (!TextContent.TryDecode(bytes, out _, out var bom) || bom != file.HasBom))
            throw new HistoryComparisonException(HistoryComparisonCode.CorruptContent);
    }

    internal static void ValidateFiles(IReadOnlyList<ProjectFile> files)
    {
        if (files.Count > ArchiveLimits.MaxEntries || files.Select(f => f.Id).Distinct().Count() != files.Count
            || ProjectTreeValidator.FindPathConflicts(files.Select(f => f.RelativePath)) is { Count: > 0 })
            throw new HistoryComparisonException(HistoryComparisonCode.CorruptContent);
        long total = 0;
        foreach (var f in files)
        {
            if (f.ByteLength < 0 || f.ByteLength > ArchiveLimits.SingleFile || f.MediaKind is not ("text" or "binary")
                || f.MediaKind == "binary" && f.HasBom || (total += f.ByteLength) > ArchiveLimits.ExpandedSize)
                throw new HistoryComparisonException(HistoryComparisonCode.LimitExceeded);
        }
    }

    // Unlike the legacy restore preview this path also reads unchanged, binary,
    // added, deleted and summary-only content. A late corrupt object withholds
    // the ENTIRE result. Read one pair at a time; never retain both projects.
    internal async Task<ComparisonSummary> CompareExactAsync(
        IReadOnlyList<ProjectFile> left, IReadOnlyList<ProjectFile> right,
        Func<ProjectFile, CancellationToken, Task<byte[]>> readLeft,
        Func<ProjectFile, CancellationToken, Task<byte[]>> readRight,
        Action check, CancellationToken ct)
    {
        ValidateFiles(left); ValidateFiles(right);
        var leftById = left.ToDictionary(f => f.Id);
        var rightById = right.ToDictionary(f => f.Id);
        var changes = ImmutableArray.CreateBuilder<FileChange>();
        var skipped = ImmutableArray.CreateBuilder<string>();
        long remaining = ArchiveLimits.DiffByteBudget;
        foreach (var r in right)
        {
            check(); ct.ThrowIfCancellationRequested();
            var rb = await readRight(r, ct); check(); VerifyBytes(r, rb);
            if (!leftById.TryGetValue(r.Id, out var l))
                changes.Add(new(r.Id, r.RelativePath, FileChangeKind.Added, null, 0, r.ByteLength, null, r.ContentHash, null));
            else
            {
                var lb = await readLeft(l, ct); check(); VerifyBytes(l, lb);
                bool renamed = l.RelativePath != r.RelativePath;
                bool changed = l.ContentHash != r.ContentHash || l.ByteLength != r.ByteLength || l.MediaKind != r.MediaKind || l.HasBom != r.HasBom;
                bool text = l.MediaKind == "text" && r.MediaKind == "text";
                IReadOnlyList<DiffLine>? diff = null;
                string? note = null;
                if (changed && text)
                {
                    diff = BoundedDiff(lb, rb, ref remaining);
                    if (diff is null) { skipped.Add(r.RelativePath); note = "Summary only: detailed diff size or work budget exceeded."; }
                    else note = "Normalized line presentation only; hashes and lengths describe exact bytes, including BOM and newline changes.";
                }
                changes.Add(new(r.Id, r.RelativePath,
                    changed ? (text ? FileChangeKind.ContentChanged : FileChangeKind.BinaryChanged) : renamed ? FileChangeKind.Renamed : FileChangeKind.Unchanged,
                    renamed ? l.RelativePath : null, l.ByteLength, r.ByteLength, l.ContentHash, r.ContentHash, diff, note));
            }
            // Yield even when storage completes synchronously; cancellation can
            // run between files. The synchronous Myers work below is prebounded.
            await Task.Yield(); check();
        }
        foreach (var l in left)
        {
            check(); ct.ThrowIfCancellationRequested();
            if (rightById.ContainsKey(l.Id)) continue;
            var bytes = await readLeft(l, ct); check(); VerifyBytes(l, bytes);
            changes.Add(new(l.Id, l.RelativePath, FileChangeKind.Removed, null, l.ByteLength, 0, l.ContentHash, null, null));
            await Task.Yield(); check();
        }
        check();
        return new(changes.ToImmutable(), skipped.Count != 0, skipped.ToImmutable());
    }

    private static IReadOnlyList<DiffLine>? BoundedDiff(byte[] left, byte[] right, ref long remaining)
    {
        long size = (long)left.Length + right.Length;
        if (left.LongLength > ArchiveLimits.DiffByteBudget || right.LongLength > ArchiveLimits.DiffByteBudget || size > remaining) return null;
        var a = TextContent.DecodeStrict(left); var b = TextContent.DecodeStrict(right);
        int al = CountLines(a), bl = CountLines(b);
        long n = (long)al + bl;
        // Conservative worst-case Myers trace bound, before allocating line
        // lists/trace. Includes aggregate retained text across the whole report.
        long work = (n + 1) * (2 * n + 1) * sizeof(int) + size;
        if (al > ArchiveLimits.DiffLineBudget || bl > ArchiveLimits.DiffLineBudget || work > remaining) return null;
        remaining -= work;
        var diff = LineDiff.Compute(TextContent.SplitLines(a), TextContent.SplitLines(b));
        return diff is null ? null : diff.ToImmutableArray();
    }

    private static int CountLines(string text)
    {
        int count = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r') { count++; if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (text[i] == '\n') count++;
            if (count > ArchiveLimits.DiffLineBudget) return count;
        }
        return count;
    }
}

internal sealed class HistoryComparisonException(HistoryComparisonCode code) : Exception
{
    internal HistoryComparisonCode Code { get; } = code;
}
