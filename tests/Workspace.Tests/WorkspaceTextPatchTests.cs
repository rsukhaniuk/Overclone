using System.Collections.Immutable;
using Client.Analysis;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Patching;
using Client.Workspace.Transfer;
using Xunit;
using static Workspace.Tests.TextPatchPreparationTests;

namespace Workspace.Tests;

public class WorkspaceTextPatchTests
{
    [Fact]
    public async Task Exact_multi_file_apply_has_one_coherent_event_then_save_reopen_analysis_export_and_manual_edit()
    {
        await using var f=await Fixture.Open();var batch=await f.Prepare();
        var generation=f.C.WorkingGeneration;var head=f.C.CurrentRevision!.Id;int events=0;
        Action observe=()=>{
            events++;
            Assert.Equal(generation+1,f.C.WorkingGeneration);Assert.Equal(SavePhase.Dirty,f.C.SavePhase);
            foreach(var p in batch.Files)Assert.Equal(p.After.ToArray(),f.C.Files.Single(w=>w.Id==p.FileId).PendingTextBytes!.ToArray());
        };
        f.C.WorkspaceChanged+=observe;
        var result=await f.C.ApplyTextBatchAsync(batch,CancellationToken.None);
        Assert.True(result.AppliedUnsaved);Assert.Null(result.Failure);Assert.Equal(1,events);Assert.Equal(0,f.Repo.Commits);
        Assert.Equal(head,f.C.CurrentRevision.Id);Assert.True(f.C.IsDirty);
        // Remove observer before unrelated manual edit/open events below.
        await VerifyCapture(f,batch);
        foreach(var p in batch.Files){
            var live=f.C.Files.Single(w=>w.Id==p.FileId);var clone=live.Clone();
            Assert.Same(live.PendingTextBytes,clone.PendingTextBytes);
            Assert.Equal(p.After.ToArray(),await f.C.GetFileBytesAsync(clone,CancellationToken.None));
        }
        f.C.WorkspaceChanged-=observe;
        // Export uses the existing save/flush queue, not a separate patch writer.
        var export=await f.C.CaptureExportSourceAsync(CancellationToken.None);
        foreach(var p in batch.Files){
            var manifest=export.Revision.Files.Single(x=>x.Id==p.FileId);
            Assert.Equal(p.After.ToArray(),await export.ReadContent(manifest.ContentHash,CancellationToken.None));
            Assert.Null(f.C.Files.Single(w=>w.Id==p.FileId).PendingTextBytes);
        }
        Assert.False(f.C.IsDirty); Assert.Equal(1,f.Repo.Commits);
        // Fresh coordinator simulates a reload without retaining pending buffers.
        await using var reopened=new WorkspaceCoordinator(f.Repo,new Locks(),new HistoryService(f.Repo));
        await reopened.OpenAsync(f.Repo.Project.Id,CancellationToken.None);
        foreach(var p in batch.Files)Assert.Equal(p.After.ToArray(),await reopened.GetFileBytesAsync(reopened.Files.Single(w=>w.Id==p.FileId),CancellationToken.None));
        var first=reopened.Files.First(w=>w.MediaKind=="text");
        Assert.True(reopened.EditText(first.Id,first.Content));
        Assert.Null(first.PendingTextBytes);
        Assert.Equal(TextContent.EncodeEdited(first.Content,first.HasBom),await reopened.GetFileBytesAsync(first,CancellationToken.None));
    }

    [Fact]
    public async Task Failed_save_retains_exact_pending_bytes_for_capture_retry_and_export()
    {
        await using var f=await Fixture.Open();var batch=await f.Prepare();
        Assert.True((await f.C.ApplyTextBatchAsync(batch,CancellationToken.None)).AppliedUnsaved);
        f.Repo.FailSave=true;var oldHead=f.Repo.Project.HeadRevisionId;
        Assert.Equal(CommitStatus.StorageFailure,(await f.C.SaveAsync(CancellationToken.None)).Status);
        Assert.Equal(SavePhase.Failed,f.C.SavePhase);Assert.True(f.C.IsDirty);Assert.Equal(oldHead,f.Repo.Project.HeadRevisionId);
        await VerifyCapture(f,batch);
        foreach(var p in batch.Files)Assert.Equal(p.After.ToArray(),await f.C.GetFileBytesAsync(f.C.Files.Single(w=>w.Id==p.FileId),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.C.CaptureExportSourceAsync(CancellationToken.None));
        f.Repo.FailSave=false;Assert.Equal(CommitStatus.Saved,(await f.C.SaveAsync(CancellationToken.None)).Status);
        var export=await f.C.CaptureExportSourceAsync(CancellationToken.None);
        foreach(var p in batch.Files)Assert.Equal(p.After.ToArray(),await export.ReadContent(p.After.Hash,CancellationToken.None));
    }

    private static async Task VerifyCapture(Fixture f,PreparedTextBatch batch)
    {
        await using var factory=new AnalysisSnapshotFactory(f.C);
        var captured=Assert.IsType<CaptureResult.Captured>(await factory.CaptureAsync(AnalysisProfile.EngineStructural(),CancellationToken.None));
        foreach(var p in batch.Files){
            var source=captured.Snapshot.Files.Single(x=>x.FileId==p.FileId);
            Assert.Equal(p.After.ToArray(),source.Bytes.ToArray());Assert.Equal(p.After.Hash,source.Sha256);Assert.Equal(p.After.HasBom,source.HasBom);
        }
        Assert.Equal(0,f.Repo.BinaryReads);
        var exactLength=batch.Files.Max(p=>p.After.Length);
        Assert.IsType<CaptureResult.QuotaExceeded>(await factory.CaptureAsync(AnalysisProfile.EngineStructural(),CancellationToken.None,AnalysisLimitsDto.None() with{MaxBytesPerFile=exactLength-1}));
    }

    [Fact]
    public async Task Pending_CRLF_length_is_rejected_in_preflight_before_any_read_or_copy_phase()
    {
        await using var f=await Fixture.Open();
        var first=f.C.Files[0];var bytes=await f.C.GetFileBytesAsync(first,CancellationToken.None);
        var single=PreparedTextBatch.Prepare(f.C.CurrentTextPatchIdentity!,
            [new(first.Id,first.RelativePath,Hash(bytes),bytes.Length,[new(bytes.Length-1,bytes.Length,bytes.AsMemory(bytes.Length-1),B("!"))])],
            f.C.Files.Select(w=>new PatchSourceInput(w.Id,w.RelativePath,w.MediaKind,w.DurableLength,w.HasBom,w.Id==first.Id?bytes:null)).ToArray()).Batch!;
        Assert.True((await f.C.ApplyTextBatchAsync(single,CancellationToken.None)).AppliedUnsaved);
        Assert.True(TextContent.TryCountEditedBytes(first.Content,first.HasBom,out var normalized));
        Assert.True(first.PendingTextBytes!.Length>normalized);
        f.C.Files.Reverse(); // put an unedited durable file ahead of the pending source
        var gate=f.Repo.ReadBarrier=new();await using var factory=new AnalysisSnapshotFactory(f.C);
        var capture=factory.CaptureAsync(AnalysisProfile.EngineStructural(),CancellationToken.None,AnalysisLimitsDto.None() with{MaxBytesPerFile=first.PendingTextBytes.Length-1});
        try{Assert.True(capture.IsCompletedSuccessfully,"preflight must finish before a durable read can block");Assert.False(gate.Reached.Task.IsCompleted);Assert.IsType<CaptureResult.QuotaExceeded>(await capture);}
        finally{gate.Release.TrySetResult();}
    }

    [Fact]
    public async Task Coordinator_rechecks_whole_live_project_budget_not_just_preparer_metadata()
    {
        await using var f=await Fixture.Open();var batch=await f.Prepare();
        f.C.Files.Single(w=>w.MediaKind=="binary").DurableLength=WorkspaceTextLimits.ProjectBytes;
        // BinaryContent is metadata-only here; a durable binary may not be loaded.
        f.C.Files.Single(w=>w.MediaKind=="binary").BinaryContent=null;
        var state=f.State();Assert.Equal(TextPatchError.QuotaExceeded,(await f.C.ApplyTextBatchAsync(batch,CancellationToken.None)).Failure!.Code);
        Assert.Equal(state,f.State());Assert.Equal(0,f.Repo.BinaryReads);
    }

    [Fact]
    public async Task Second_live_file_mismatch_and_read_failure_have_no_partial_mutation()
    {
        foreach(var damage in new[]{"path","content","bom","deleted","binary","read"}){
            await using var f=await Fixture.Open();var batch=await f.Prepare();var second=f.C.Files[1];
            switch(damage){case "path":second.RelativePath="changed.tex";break;case "content":second.Content="not the preimage";break;
                case "bom":second.HasBom=!second.HasBom;break;case "deleted":second.Deleted=true;break;case "binary":second.MediaKind="binary";break;
                case "read":f.Repo.ThrowReadHash=second.DurableHash;break;}
            var state=f.State();var r=await f.C.ApplyTextBatchAsync(batch,CancellationToken.None);
            Assert.False(r.AppliedUnsaved);Assert.NotNull(r.Failure);Assert.Equal(state,f.State());
            Assert.DoesNotContain("secret",r.Failure.SafeMessage);Assert.Equal(0,f.Repo.Commits);
        }
    }

    public static IEnumerable<object[]> Races()
    {
        foreach(var stage in new[]{"read","ownership","head"})
            foreach(var change in new[]{"edit","tree","create","open","head","cancel","lock","direct"})yield return [stage,change];
    }

    [Theory,MemberData(nameof(Races))]
    public async Task Awaited_validation_never_overwrites_new_work(string stage,string change)
    {
        await using var f=await Fixture.Open();var batch=await f.Prepare();var barrier=new Barrier();
        switch(stage){case "read":f.Repo.ReadBarrier=barrier;break;case "ownership":f.Locks.OwnershipBarrier=barrier;break;case "head":f.Repo.HeadBarrier=barrier;break;}
        using var cancel=new CancellationTokenSource();var applying=f.C.ApplyTextBatchAsync(batch,cancel.Token);
        await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch(change){case "edit":Assert.True(f.C.EditText(f.C.Files[0].Id,"new edit🙂"));break;
            case "tree":Assert.True(f.C.RenameFile(f.C.Files[0].Id,"renamed.tex"));break;
            case "create":Assert.True(f.C.CreateFile("new.tex","new"));break;
            case "open":await f.C.OpenAsync(f.Repo.Project.Id,CancellationToken.None);break;
            case "head":f.Repo.AdvanceHead();break;case "cancel":cancel.Cancel();break;
            case "lock":f.Locks.Owned=false;break;case "direct":f.C.Files[1].Content="direct mutation";break;}
        var state=f.State();barrier.Release.TrySetResult();
        var result=await applying.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.AppliedUnsaved);Assert.Equal(state,f.State());Assert.Equal(0,f.Repo.Commits);
        foreach(var w in f.C.Files)Assert.Null(w.PendingTextBytes);
    }

    [Fact]
    public async Task Gate_wait_crossed_by_save_and_manual_edit_is_stale_and_cancel_is_prompt()
    {
        await using var f=await Fixture.Open();Assert.True(f.C.EditText(f.C.Files[0].Id,"draft\r\n"));
        var batch=await f.Prepare();var barrier=f.Repo.CommitBarrier=new();
        var save=f.C.SaveAsync(CancellationToken.None);await barrier.Reached.Task;
        using var cts=new CancellationTokenSource();var cancelled=f.C.ApplyTextBatchAsync(batch,cts.Token);cts.Cancel();
        Assert.Equal(TextPatchError.Cancelled,(await cancelled).Failure!.Code);
        var queued=f.C.ApplyTextBatchAsync(batch,CancellationToken.None);
        Assert.True(f.C.EditText(f.C.Files[0].Id,"later"));var generation=f.C.WorkingGeneration;
        barrier.Release.SetResult();await save;
        Assert.False((await queued).AppliedUnsaved);Assert.Equal("later",f.C.Files[0].Content);Assert.Equal(generation,f.C.WorkingGeneration);Assert.True(f.C.IsDirty);
    }

    [Fact]
    public async Task Older_pending_save_does_not_clear_later_equal_text_manual_representation_or_BOM()
    {
        await using var f=await Fixture.Open();var batch=await f.Prepare();Assert.True((await f.C.ApplyTextBatchAsync(batch,CancellationToken.None)).AppliedUnsaved);
        var first=f.C.Files[0];var oldBytes=first.PendingTextBytes!.ToArray();var barrier=f.Repo.CommitBarrier=new();
        var save=f.C.SaveAsync(CancellationToken.None);await barrier.Reached.Task;
        // Equal decoded text is NOT byte stability: manual encoding will use LF.
        Assert.True(f.C.EditText(first.Id,first.Content));first.HasBom=false;
        barrier.Release.SetResult();await save;
        Assert.True(first.DirtyContent);Assert.Null(first.PendingTextBytes);Assert.False(first.HasBom);
        Assert.Equal(oldBytes,f.Repo.Content[batch.Files[0].After.Hash]);
        var newBytes=TextContent.EncodeEdited(first.Content,false);
        Assert.NotEqual(oldBytes,newBytes);Assert.Equal(newBytes,await f.C.GetFileBytesAsync(first,CancellationToken.None));
        Assert.Equal(CommitStatus.Saved,(await f.C.SaveAsync(CancellationToken.None)).Status);
        Assert.Equal(newBytes,f.Repo.Content[first.DurableHash!.Value]);
    }

    [Fact]
    public async Task Old_save_cannot_install_a_newer_batch_waiting_on_the_same_gate()
    {
        await using var f=await Fixture.Open();var batch=await f.Prepare();Assert.True((await f.C.ApplyTextBatchAsync(batch,CancellationToken.None)).AppliedUnsaved);
        var next=await f.Prepare();var barrier=f.Repo.CommitBarrier=new();var save=f.C.SaveAsync(CancellationToken.None);await barrier.Reached.Task;
        var queued=f.C.ApplyTextBatchAsync(next,CancellationToken.None);barrier.Release.SetResult();await save;
        Assert.Equal(TextPatchError.Stale,(await queued).Failure!.Code);
        // Reprepare explicitly against the new head, no stale automatic retry.
        next=await f.Prepare();Assert.True((await f.C.ApplyTextBatchAsync(next,CancellationToken.None)).AppliedUnsaved);
        foreach(var p in next.Files)Assert.Equal(p.After.ToArray(),f.C.Files.Single(w=>w.Id==p.FileId).PendingTextBytes!.ToArray());
    }

    [Theory]
    [InlineData("readonly")][InlineData("ownership")][InlineData("head")][InlineData("eligibility")][InlineData("throwing-policy")]
    public async Task Failed_capabilities_or_final_policy_leave_every_state_field_unchanged(string kind)
    {
        await using var f=await Fixture.Open();var batch=await f.Prepare();
        if(kind=="readonly"){f.Locks.Acquire=false;await f.C.OpenAsync(f.Repo.Project.Id,CancellationToken.None);batch=await f.Prepare();}
        if(kind=="ownership")f.Locks.Owned=false; // IsHeld(name) still true: another client's lock is not ours
        if(kind=="head")f.Repo.HeadVerified=false;
        var state=f.State();Func<bool>? check=kind switch{"eligibility"=>()=>false,"throwing-policy"=>()=>throw new InvalidOperationException("secret"),_=>null};
        var r=await f.C.ApplyTextBatchAsync(batch,CancellationToken.None,check);
        Assert.False(r.AppliedUnsaved);Assert.Equal(state,f.State());Assert.DoesNotContain("secret",r.Failure!.SafeMessage);
    }

    [Fact]
    public async Task Close_queued_before_patch_resets_session_and_rejects_the_old_batch()
    {
        await using var f=await Fixture.Open();Assert.True(f.C.EditText(f.C.Files[0].Id,"dirty"));var batch=await f.Prepare();
        var barrier=f.Repo.CommitBarrier=new();var save=f.C.SaveAsync(CancellationToken.None);await barrier.Reached.Task;
        var close=f.C.CloseAsync(CancellationToken.None);var patch=f.C.ApplyTextBatchAsync(batch,CancellationToken.None);
        barrier.Release.SetResult();await save;await close;Assert.False((await patch).AppliedUnsaved);Assert.False(f.C.IsOpen);
    }

    internal sealed class Barrier
    {
        internal TaskCompletionSource Reached=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task Wait(CancellationToken ct){Reached.TrySetResult();await Release.Task.WaitAsync(ct);}
    }
    internal sealed class Locks:IWorkspaceLocks
    {
        internal bool Owned=true,Acquire=true;internal Barrier? OwnershipBarrier;
        public Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken ct)=>Task.FromResult(new WorkspaceCapabilities(true,true,true,true));
        public Task<string?> TryAcquireAsync(string n,CancellationToken ct){Owned=Acquire;return Task.FromResult<string?>(Acquire?"ours":null);}
        public Task<string> WaitForAsync(string n,CancellationToken ct){Owned=true;return Task.FromResult("ours");}
        public Task<bool> ReleaseAsync(string id,CancellationToken ct){Owned=false;return Task.FromResult(true);}
        public Task<bool> IsHeldAsync(string n,CancellationToken ct)=>Task.FromResult(true);
        public bool IsOwnedLocally(string id,string name)=>Owned&&id=="ours";
        public async Task<bool> OwnsAsync(string id,string name,CancellationToken ct){if(OwnershipBarrier is{} b)await b.Wait(ct);return IsOwnedLocally(id,name);}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    internal sealed class Fixture:IAsyncDisposable
    {
        internal Repo Repo=new();internal Locks Locks=new();internal WorkspaceCoordinator C=null!;
        internal static async Task<Fixture> Open(){var f=new Fixture();f.C=new(f.Repo,f.Locks,new HistoryService(f.Repo));await f.C.OpenAsync(f.Repo.Project.Id,CancellationToken.None);return f;}
        internal async Task<PreparedTextBatch> Prepare(){
            var sources=new List<PatchSourceInput>();var patches=new List<FilePatchInput>();
            foreach(var w in C.Files.Where(w=>!w.Deleted)){
                if(w.MediaKind=="binary"){sources.Add(new(w.Id,w.RelativePath,"binary",w.DurableLength,false));continue;}
                var bytes=await C.GetFileBytesAsync(w,CancellationToken.None);
                sources.Add(new(w.Id,w.RelativePath,"text",bytes.Length,w.HasBom,bytes));
                byte replacement=bytes[^1]==(byte)'!'?(byte)'?':(byte)'!';
                patches.Add(new(w.Id,w.RelativePath,Hash(bytes),bytes.Length,[new(bytes.Length-1,bytes.Length,bytes.AsMemory(bytes.Length-1),new byte[]{replacement})]));
            }
            return Assert.IsType<PreparedTextBatch>(PreparedTextBatch.Prepare(C.CurrentTextPatchIdentity!,patches,sources).Batch);
        }
        internal string State()=>string.Join("|",C.Files.Select(w=>$"{w.Id}:{w.RelativePath}:{w.Content}:{w.HasBom}:{w.DirtyContent}:{w.TreeChanged}:{w.Deleted}:{w.PendingTextBytes?.Hash}"))+ $"/{C.SessionId}/{C.WorkingGeneration}/{C.CurrentRevision?.Id}/{C.SavePhase}/{C.LastError}/{C.IsReadOnly}";
        public ValueTask DisposeAsync()=>C.DisposeAsync();
    }
    internal sealed class Repo:IProjectRepository
    {
        internal Project Project=new(ProjectId.New(),"patch-test",null);
        internal ProjectRevision Revision;
        internal Dictionary<SHA256,byte[]> Content=new();internal int Commits,BinaryReads;
        internal bool FailSave,HeadVerified=true;internal SHA256? ThrowReadHash;
        internal Barrier? ReadBarrier,HeadBarrier,CommitBarrier;
        internal Repo(){
            byte[] one=[239,187,191,..B("Привіт🙂\r\nleft\rend\n")];byte[] two=B("second\r\n");byte[] binary=[255,0,1];
            var all=new[]{("main.tex",one,"text"),("b.tex",two,"text"),("asset.bin",binary,"binary")};
            Revision=new(RevisionId.New(),Project.Id,null,all.Select(x=>new ProjectFile(FileId.New(),x.Item1,x.Item3,Hash(x.Item2),x.Item2.Length,TextContent.HasBom(x.Item2))).ToImmutableArray(),DateTimeOffset.UtcNow);
            foreach(var x in all)Content[Hash(x.Item2)]=x.Item2;Project=Project with{HeadRevisionId=Revision.Id};
        }
        internal void AdvanceHead(){Revision=Revision with{Id=RevisionId.New()};Project=Project with{HeadRevisionId=Revision.Id};}
        public Task<ProjectSnapshot> OpenAsync(ProjectId id,CancellationToken ct)=>Task.FromResult(new ProjectSnapshot(Project,Revision,
            Revision.Files.Where(x=>x.MediaKind=="text").ToDictionary(x=>x.Id,x=>TextContent.DecodeStrict(Content[x.ContentHash])),
            Revision.Files.Where(x=>x.MediaKind=="binary").ToDictionary(x=>x.Id,x=>Content[x.ContentHash]),
            Revision.Files.Where(x=>x.MediaKind=="text").ToDictionary(x=>x.Id,x=>x.HasBom),false));
        public async Task<byte[]> ReadContentAsync(SHA256 h,CancellationToken ct){
            if(ReadBarrier is{} b)await b.Wait(ct);if(ThrowReadHash==h)throw new IOException("secret failure");
            if(Revision.Files.Any(x=>x.ContentHash==h&&x.MediaKind=="binary")){BinaryReads++;throw new InvalidOperationException("binary read not allowed in text capture");}
            return Content[h].ToArray();
        }
        public async Task<bool> VerifyHeadAsync(ProjectId id,RevisionId? head,CancellationToken ct){if(HeadBarrier is{} b)await b.Wait(ct);return HeadVerified&&Project.Id==id&&Project.HeadRevisionId==head;}
        public async Task<CommitResult> CommitAsync(ProjectCommit batch,RevisionId? head,CancellationToken ct){
            if(CommitBarrier is{} b)await b.Wait(ct);if(FailSave)return CommitResult.Failure("QuotaExceeded","Synthetic save failure");
            if(head!=Project.HeadRevisionId)return CommitResult.Conflict(Project.HeadRevisionId);
            foreach(var c in batch.Content)Content[c.Hash]=c.Bytes.ToArray();Revision=batch.SelectedHead;Project=Project with{HeadRevisionId=Revision.Id};Commits++;return CommitResult.Saved(Revision.Id);
        }
        public Task<RevisionManifest> ReadRevisionAsync(ProjectId id,RevisionId r,CancellationToken ct)=>Task.FromResult(RevisionManifest.Ok(Revision));
        public Task<HistoryPage> ReadHistoryAsync(ProjectId id,HistoryCursor? c,CancellationToken ct)=>Task.FromResult(new HistoryPage([]));
        public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<Project>>([Project]);
        public Task<Project> CreateProjectAsync(string n,CancellationToken ct)=>throw new NotSupportedException();
        public Task DeleteProjectAsync(ProjectId id,CancellationToken ct)=>throw new NotSupportedException();
        public Task<CommitResult> RepairHeadAsync(ProjectId id,RevisionId a,RevisionId b,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ImportPublishResult> PublishImportAsync(StagedProject s,CancellationToken ct)=>throw new NotSupportedException();
        public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId p,RevisionId h,ProjectRevision r,RevisionId s,string reason,CancellationToken ct)=>throw new NotSupportedException();
    }
}
