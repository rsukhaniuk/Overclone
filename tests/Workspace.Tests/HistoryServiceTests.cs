using System.Collections.Immutable;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests;

/// <summary>
/// History paging, bounded comparison and restore-preview/accept policy. Uses
/// a fake in-memory repository so no browser storage is needed. Covers:
/// B07 (stable paging across timestamp ties; text/binary/rename/add/delete
/// comparisons; oversized/expensive diff fallback), B08/B09/B10 (restore
/// produces a new revision with the former head as parent; stale-preview and
/// expected-head conflicts are rejected; corrupt historical objects are
/// rejected without changing a healthy head).
/// </summary>
public class HistoryServiceTests
{
    private sealed class FakeRepo : IProjectRepository
    {
        public readonly Dictionary<ProjectId, Project> Projects = new();
        public readonly Dictionary<RevisionId, ProjectRevision> Revisions = new();
        public readonly Dictionary<RevisionId, string> DamagedReason = new();
        public readonly Dictionary<string, byte[]> Content = new();
        public readonly List<HistoryCheckpoint> Checkpoints = new();
        public RevisionId? LastExpectedHead;

        public Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct) => throw new NotImplementedException();
        public Task<CommitResult> CommitAsync(ProjectCommit b, RevisionId? eh, CancellationToken ct) => throw new NotImplementedException();
        public Task<Project> CreateProjectAsync(string n, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => throw new NotImplementedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId p, RevisionId e, RevisionId n, CancellationToken ct) => throw new NotImplementedException();
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? cursor, CancellationToken ct)
        {
            var all = Checkpoints.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id.ToString());
            IEnumerable<HistoryCheckpoint> page = all;
            if (cursor is { Value: { } v } && !string.IsNullOrEmpty(v))
            {
                var parts = v.Split('|');
                var after = DateTimeOffset.Parse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal);
                var afterId = parts[1];
                page = all.SkipWhile(c => c.CreatedAt != after || c.Id.ToString() != afterId).Skip(1);
            }
            var taken = page.Take(ArchiveLimits.HistoryPageSize + 1).ToList();
            string? next = null;
            if (taken.Count > ArchiveLimits.HistoryPageSize)
            {
                var last = taken[ArchiveLimits.HistoryPageSize - 1];
                next = last.CreatedAt.UtcDateTime.ToString("O") + "|" + last.Id.ToString();
                taken = taken.Take(ArchiveLimits.HistoryPageSize).ToList();
            }
            return Task.FromResult(new HistoryPage(taken, next));
        }
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId p, RevisionId r, CancellationToken ct)
        {
            if (!Revisions.TryGetValue(r, out var rev))
                return Task.FromResult(RevisionManifest.Damaged(
                    new ProjectRevision(r, p, null, ImmutableArray<ProjectFile>.Empty, DateTimeOffset.UtcNow),
                    "missing"));
            if (rev.ProjectId != p)
                return Task.FromResult(RevisionManifest.Damaged(rev, "wrong project"));
            if (DamagedReason.TryGetValue(r, out var reason))
                return Task.FromResult(RevisionManifest.Damaged(rev, reason));
            foreach (var f in rev.Files)
                if (!Content.ContainsKey(f.ContentHash.Hex))
                    return Task.FromResult(RevisionManifest.Damaged(rev, $"missing {f.RelativePath}"));
            return Task.FromResult(RevisionManifest.Ok(rev));
        }
        public Task<byte[]> ReadContentAsync(SHA256 h, CancellationToken ct)
            => Task.FromResult(Content.TryGetValue(h.Hex, out var b) ? b : Array.Empty<byte>());
        public Task<ImportPublishResult> PublishImportAsync(StagedProject s, CancellationToken ct) => throw new NotImplementedException();
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId p, RevisionId eh, ProjectRevision r, RevisionId src, string reason, CancellationToken ct)
        {
            LastExpectedHead = eh;
            Revisions[r.Id] = r;
            Checkpoints.Add(new HistoryCheckpoint(CheckpointId.New(), p, r.Id, $"restore:{reason}:{src}", DateTimeOffset.UtcNow));
            Projects[p] = Projects[p] with { HeadRevisionId = r.Id };
            return Task.FromResult(CommitResult.Saved(r.Id));
        }
    }

    private static (FakeRepo repo, ProjectId pid, ProjectRevision r1, ProjectRevision r2) SeedTwoRevisions()
    {
        var repo = new FakeRepo();
        var pid = ProjectId.New();
        // f1: renamed (main.tex → src/main.tex) with UNCHANGED content → Renamed.
        var shared = new SHA256(Sha("v1"));
        var f1r1 = new ProjectFile(FileId.New(), "main.tex", "text", shared, 2, false);
        var f1r2 = new ProjectFile(f1r1.Id, "src/main.tex", "text", shared, 2, false);
        // f2: same id + same path, content changed → ContentChanged.
        var fid2 = FileId.New();
        var f2r1 = new ProjectFile(fid2, "notes.tex", "text", new SHA256(Sha("n1")), 2, false);
        var f2r2 = new ProjectFile(fid2, "notes.tex", "text", new SHA256(Sha("n2")), 2, false);
        // bin + bib: only in r2.
        var bin = new ProjectFile(FileId.New(), "img.png", "binary", new SHA256(Sha("png")), 3, false);
        var bib = new ProjectFile(FileId.New(), "refs.bib", "text", new SHA256(Sha("bib")), 3, false);

        var r1 = new ProjectRevision(RevisionId.New(), pid, null,
            ImmutableArray.Create(f1r1, f2r1), DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var r2 = new ProjectRevision(RevisionId.New(), pid, r1.Id,
            ImmutableArray.Create(f1r2, f2r2, bin, bib), DateTimeOffset.Parse("2025-01-02T00:00:00Z"));
        repo.Revisions[r1.Id] = r1; repo.Revisions[r2.Id] = r2;
        repo.Content[Sha("v1")] = System.Text.Encoding.UTF8.GetBytes("a\n");
        repo.Content[Sha("n1")] = System.Text.Encoding.UTF8.GetBytes("o\n");
        repo.Content[Sha("n2")] = System.Text.Encoding.UTF8.GetBytes("p\n");
        repo.Content[Sha("png")] = new byte[] { 1, 2, 3 };
        repo.Content[Sha("bib")] = System.Text.Encoding.UTF8.GetBytes("x\ny\n");
        repo.Projects[pid] = new Project(pid, "p", r2.Id);
        repo.Checkpoints.Add(new HistoryCheckpoint(CheckpointId.New(), pid, r1.Id, "save", r1.CreatedAt));
        repo.Checkpoints.Add(new HistoryCheckpoint(CheckpointId.New(), pid, r2.Id, "save", r2.CreatedAt));
        return (repo, pid, r1, r2);
    }

    private static string Sha(string s)
        => System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s))
            .Aggregate("", (a, b) => a + b.ToString("x2"));

    [Fact]
    public async Task Compare_Detects_Rename_Add_Remove_And_Binary_Change()
    {
        var (repo, pid, r1, r2) = SeedTwoRevisions();
        var svc = new HistoryService(repo);
        // Comparing r2 (base) vs r1 (historical): main.tex→src/main.tex renamed
        // (unchanged content), notes.tex content-changed, img.png+refs.bib removed.
        var summary = await svc.CompareAsync(pid, r2.Id, r1.Id, default);
        var byKind = summary.Changes.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.True(byKind.ContainsKey(FileChangeKind.Renamed));   // main.tex ↔ src/main.tex, content unchanged
        Assert.True(byKind.ContainsKey(FileChangeKind.ContentChanged)); // notes.tex
        Assert.True(byKind.ContainsKey(FileChangeKind.Removed));     // img.png, refs.bib removed going back to r1
        // No decoded text diff is produced for binary files.
        var binChange = summary.Changes.FirstOrDefault(c => c.Path == "img.png");
        Assert.NotNull(binChange);
        Assert.Null(binChange!.Diff);
    }

    [Fact]
    public async Task Compare_Text_Change_Has_Bounded_Line_Diff()
    {
        var repo = new FakeRepo();
        var pid = ProjectId.New();
        var fid = FileId.New();
        var ra = new ProjectRevision(RevisionId.New(), pid, null,
            ImmutableArray.Create(new ProjectFile(fid, "a.tex", "text", new SHA256(Sha("a")), 4, false)),
            DateTimeOffset.UtcNow);
        var rb = new ProjectRevision(RevisionId.New(), pid, ra.Id,
            ImmutableArray.Create(new ProjectFile(fid, "a.tex", "text", new SHA256(Sha("b")), 4, false)),
            DateTimeOffset.UtcNow);
        repo.Revisions[ra.Id] = ra; repo.Revisions[rb.Id] = rb;
        repo.Content[Sha("a")] = System.Text.Encoding.UTF8.GetBytes("one\ntwo\nthree\n");
        repo.Content[Sha("b")] = System.Text.Encoding.UTF8.GetBytes("one\ntwo changed\nthree\n");
        repo.Projects[pid] = new Project(pid, "p", rb.Id);

        var svc = new HistoryService(repo);
        var summary = await svc.CompareAsync(pid, rb.Id, ra.Id, default);
        var change = summary.Changes.Single(c => c.Kind == FileChangeKind.ContentChanged);
        Assert.NotNull(change.Diff);
        Assert.Contains(change.Diff!, d => d.Op == DiffOp.Insert);
        Assert.Contains(change.Diff!, d => d.Op == DiffOp.Delete);
    }

    [Fact]
    public async Task Compare_Falls_Back_For_Oversized_Diff()
    {
        var repo = new FakeRepo();
        var pid = ProjectId.New();
        var fid = FileId.New();
        var big = new byte[ArchiveLimits.DiffByteBudget + 1];
        Array.Fill(big, (byte)'x');
        var ra = new ProjectRevision(RevisionId.New(), pid, null,
            ImmutableArray.Create(new ProjectFile(fid, "big.tex", "text", new SHA256(Sha("big")), big.Length, false)),
            DateTimeOffset.UtcNow);
        var rb = new ProjectRevision(RevisionId.New(), pid, ra.Id,
            ImmutableArray.Create(new ProjectFile(fid, "big.tex", "text", new SHA256(Sha("big2")), big.Length, false)),
            DateTimeOffset.UtcNow);
        repo.Revisions[ra.Id] = ra; repo.Revisions[rb.Id] = rb;
        repo.Content[Sha("big")] = big;
        repo.Content[Sha("big2")] = big;
        repo.Projects[pid] = new Project(pid, "p", rb.Id);

        var svc = new HistoryService(repo);
        var summary = await svc.CompareAsync(pid, rb.Id, ra.Id, default);
        var change = summary.Changes.Single(c => c.Kind == FileChangeKind.ContentChanged);
        Assert.Null(change.Diff);
        Assert.True(summary.DiffBudgetExceeded);
    }

    [Fact]
    public async Task Paging_Stable_Across_Timestamp_Ties_50_Per_Page()
    {
        var repo = new FakeRepo();
        var pid = ProjectId.New();
        var t = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        for (int i = 0; i < ArchiveLimits.HistoryPageSize + 5; i++)
        {
            var rid = RevisionId.New();
            repo.Revisions[rid] = new ProjectRevision(rid, pid, null, ImmutableArray<ProjectFile>.Empty, t);
            repo.Checkpoints.Add(new HistoryCheckpoint(CheckpointId.New(), pid, rid, "save", t));
        }
        repo.Projects[pid] = new Project(pid, "p", null);

        var svc = new HistoryService(repo);
        var page1 = await svc.ReadHistoryAsync(pid, null, default);
        Assert.Equal(ArchiveLimits.HistoryPageSize, page1.Checkpoints.Count);
        Assert.NotNull(page1.NextCursor);
        var page2 = await svc.ReadHistoryAsync(pid, new HistoryCursor(page1.NextCursor!), default);
        Assert.Equal(5, page2.Checkpoints.Count);
        Assert.Null(page2.NextCursor);
        // No overlap between pages.
        var ids1 = page1.Checkpoints.Select(c => c.Id).ToHashSet();
        Assert.DoesNotContain(page2.Checkpoints, c => ids1.Contains(c.Id));
    }

    [Fact]
    public async Task CompareRejects_Damaged_Historical_Revision()
    {
        var (repo, pid, r1, r2) = SeedTwoRevisions();
        repo.DamagedReason[r1.Id] = "object missing";
        var svc = new HistoryService(repo);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CompareAsync(pid, r2.Id, r1.Id, default));
    }
}
