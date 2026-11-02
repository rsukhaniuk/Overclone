using Client.Analysis;
using Client.Review;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Patching;
using Xunit;
using static Workspace.Tests.TextPatchPreparationTests;

namespace Workspace.Tests.ReviewSelection;

internal sealed class SubsetHost : IAsyncDisposable
{
    internal WorkspaceTextPatchTests.Fixture F = null!;
    internal HeldRepo Repo = null!;
    internal AnalysisCoordinator Analysis = null!;
    internal ChangeReviewService Service = null!;
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    internal static async Task<SubsetHost> Open()
    {
        var h = new SubsetHost { F = await WorkspaceTextPatchTests.Fixture.Open() };
        await h.F.C.DisposeAsync(); h.Repo = new(h.F.Repo);
        h.F.C = new(h.Repo, h.F.Locks, new HistoryService(h.Repo)); await h.F.C.OpenAsync(h.F.Repo.Project.Id, default);
        var analyzer = new Port(); h.Analysis = new(h.F.C, analyzer, delayAsync: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        h.Service = new(h.F.C, h.Analysis, new SourceNavigator(h.Analysis, h.F.C, analyzer)); return h;
    }
    internal async Task<TextChangeSet> Proposal(TextPatchOrigin origin = TextPatchOrigin.User)
    {
        var files = new List<FilePatchInput>();
        foreach (var f in F.C.Files.Where(f => f.MediaKind == "text" && !f.Deleted))
        {
            var b = await F.C.GetFileBytesAsync(f, default); int start = f.HasBom ? 3 : 0;
            int width = b[start] >= 0xc0 ? 2 : 1;
            var edits = new List<TextEditInput> { new(start, start + width, b.AsMemory(start, width), B("Я🙂")) };
            int middle = Array.IndexOf(b, (byte)'l', start + width);
            if (middle >= 0) edits.Add(new(middle, middle + 1, b.AsMemory(middle, 1), B("MIDDLE")));
            edits.Add(new(b.Length - 1, b.Length, b.AsMemory(b.Length - 1), B("$1\\literal🙂\r\n")));
            files.Add(new(f.Id, f.RelativePath, Hash(b), b.Length, edits));
        }
        return TextChangeSet.Create(origin, "Synthetic exact multi-edit proposal", files).ChangeSet!;
    }
    internal async Task<ReviewPreview> Preview() => (await Service.PreviewAsync(await Proposal(), default)).Preview!;
    internal static ReviewSubsetSelection Select(ReviewPreview p, params (int File, int Edit)[] keys)
        => new(p.Proposal.Id, keys.Select(k => new ReviewSelectedEdit(p.Proposal.Files[k.File].FileId, k.Edit)).ToArray());
    public async ValueTask DisposeAsync()
    { Repo.Release.TrySetResult(); await Service.DisposeAsync(); await Analysis.DisposeAsync(); await F.DisposeAsync(); }
    internal sealed class Port : ISourceAnalyzer
    {
        public Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot s, CancellationToken ct) => Task.FromResult<AnalysisJobResult>(new AnalysisJobResult.Cancelled("synthetic-subset", 1));
        public Task<bool> IsCurrentWorkerEpochAsync(long epoch, CancellationToken ct) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    // Deliberately NONCOOPERATIVE actual read; cancellation cannot admit new
    // review work until this real task settles. Other calls use accepted ports.
    internal sealed class HeldRepo(WorkspaceTextPatchTests.Repo inner) : IProjectRepository
    {
        internal bool Hold;
        internal TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<byte[]> ReadContentAsync(SHA256 h, CancellationToken ct)
        { if (Hold) { Reached.TrySetResult(); await Release.Task; } return await inner.ReadContentAsync(h, CancellationToken.None); }
        public Task<ProjectSnapshot> OpenAsync(ProjectId id, CancellationToken ct) => inner.OpenAsync(id, ct);
        public Task<bool> VerifyHeadAsync(ProjectId id, RevisionId? head, CancellationToken ct) => inner.VerifyHeadAsync(id, head, ct);
        public Task<CommitResult> CommitAsync(ProjectCommit b, RevisionId? h, CancellationToken ct) => inner.CommitAsync(b, h, ct);
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId id, RevisionId r, CancellationToken ct) => inner.ReadRevisionAsync(id, r, ct);
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id, HistoryCursor? c, CancellationToken ct) => inner.ReadHistoryAsync(id, c, ct);
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => inner.ListProjectsAsync(ct);
        public Task<Project> CreateProjectAsync(string n, CancellationToken ct) => inner.CreateProjectAsync(n, ct);
        public Task DeleteProjectAsync(ProjectId id, CancellationToken ct) => inner.DeleteProjectAsync(id, ct);
        public Task<CommitResult> RepairHeadAsync(ProjectId id, RevisionId a, RevisionId b, CancellationToken ct) => inner.RepairHeadAsync(id, a, b, ct);
        public Task<ImportPublishResult> PublishImportAsync(StagedProject s, CancellationToken ct) => inner.PublishImportAsync(s, ct);
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId p, RevisionId h, ProjectRevision r, RevisionId s, string reason, CancellationToken ct) => inner.CreateRestoreRevisionAsync(p, h, r, s, reason, ct);
    }
}
