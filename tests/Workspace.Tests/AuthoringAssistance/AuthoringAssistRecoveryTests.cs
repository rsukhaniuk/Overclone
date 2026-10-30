using System.Text;
using Client.Analysis;
using Client.Editing.Assistance;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.AuthoringAssistance;

public sealed partial class AuthoringAssistSessionTests
{
    [Fact] public void Recovery_source_can_be_observed_but_never_inserted_into()=>Run(async()=>
    {
        var id=ProjectId.New();var fileId=FileId.New();byte[] bytes=Encoding.UTF8.GetBytes("\\se");
        var file=new ProjectFile(fileId,"main.tex","text",CompilationTestWorkspace.Hash(bytes),bytes.Length,false);
        var revision=new ProjectRevision(RevisionId.New(),id,null,[file],DateTimeOffset.UtcNow);
        var recovery=new RecoveryReport(RevisionId.New(),[]);
        var snapshot=new ProjectSnapshot(new Project(id,"recovery",revision.Id),revision,
            new Dictionary<FileId,string>{{fileId,"\\se"}},new Dictionary<FileId,byte[]>(),new Dictionary<FileId,bool>{{fileId,false}},false,recovery);
        var repository=new RecoveryRepository(snapshot,bytes);var locks=new CompilationTestWorkspace.Locks();
        await using var workspace=new WorkspaceCoordinator(repository,locks,new HistoryService(repository));await workspace.OpenAsync(id,default);
        await using var factory=new AnalysisSnapshotFactory(workspace);var editor=new Editor(workspace);using var session=new AuthoringAssistSession(workspace,locks,factory,new Provider(),editor);
        session.SetRoot(fileId);Assert.True(await session.SuggestAsync());var report=session.Report!;Assert.False(session.CanInsert(report));
        Assert.False(await session.InsertAsync(report,"item-1"));Assert.Equal("\\se",workspace.ActiveFile!.Content);Assert.Same(recovery,workspace.Recovery);Assert.Equal(0,editor.Applies);
    });
    private sealed class RecoveryRepository(ProjectSnapshot snapshot,byte[] bytes) : IProjectRepository
    {
        public Task<ProjectSnapshot> OpenAsync(ProjectId id,CancellationToken ct)=>Task.FromResult(snapshot);
        public Task<byte[]> ReadContentAsync(SHA256 hash,CancellationToken ct)=>Task.FromResult(bytes);
        public Task<CommitResult> CommitAsync(ProjectCommit batch,RevisionId? expectedHead,CancellationToken ct)=>throw new NotSupportedException();
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id,HistoryCursor? cursor,CancellationToken ct)=>throw new NotSupportedException();
        public Task<Project> CreateProjectAsync(string name,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task DeleteProjectAsync(ProjectId id,CancellationToken ct)=>throw new NotSupportedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId id,RevisionId expected,RevisionId newHead,CancellationToken ct)=>throw new NotSupportedException();
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId id,RevisionId revision,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ImportPublishResult> PublishImportAsync(StagedProject staged,CancellationToken ct)=>throw new NotSupportedException();
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId id,RevisionId expected,ProjectRevision revision,RevisionId source,string reason,CancellationToken ct)=>throw new NotSupportedException();
    }
}
