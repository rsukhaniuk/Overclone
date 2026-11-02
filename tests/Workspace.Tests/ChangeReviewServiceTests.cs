using System.Collections.Immutable;
using System.Text;
using Client.Analysis;
using Client.Review;
using Client.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Patching;
using Client.Workspace.Transfer;
using Xunit;
using static Workspace.Tests.TextPatchPreparationTests;

namespace Workspace.Tests;

public class ChangeReviewServiceTests
{
    private static readonly CancellationToken None=CancellationToken.None;

    [Fact]
    public async Task Canonical_native_F1_owned_preview_decline_apply_save_reanalysis_and_exact_undo()
    {
        await using var h=await Harness.Open(true);var before=h.F.Repo.Project.HeadRevisionId;var bytes=KnuFixture.Load("negative").Bytes;
        var preview=await h.Service.PreviewFixAsync(AnalysisCatalog.Fix,None);
        Assert.Equal(ReviewStatus.Ready,preview.Status);var p=preview.Preview!;
        Assert.Equal(TextPatchOrigin.Rule,p.Proposal.Origin);Assert.Equal(AnalysisCatalog.Indent,p.Citation!.RuleId);Assert.Equal("3.1",p.Citation.Section);
        Assert.Equal(new[]{18},p.Citation.PrintedPages.ToArray());Assert.Equal(bytes,p.Prepared.Files[0].Before.ToArray());
        Assert.Equal("10 mm",Encoding.UTF8.GetString(p.Prepared.Files[0].Edits[0].Expected.Span));
        Assert.Equal("1.27cm",Encoding.UTF8.GetString(p.Prepared.Files[0].Edits[0].Replacement.Span));
        var forged=h.Report!.Result.Knu!.FixDescriptors[0] with{Replacement="UNACCEPTED",Span=new(0,0),RelativePath="forged.tex"};
        // Public API accepts an ID only; a clone with the ID cannot supply edits.
        var canonical=(await h.Service.PreviewFixAsync(forged.Id,None)).Preview!;
        Assert.Equal("1.27cm",Encoding.UTF8.GetString(canonical.Prepared.Files[0].Edits[0].Replacement.Span));
        Assert.Equal(0,h.F.Repo.Commits);Assert.False(h.F.C.IsDirty);Assert.Equal(before,h.F.Repo.Project.HeadRevisionId);
        Assert.Null(h.Service.Decline(canonical.Id).Failure);Assert.Equal(ReviewCode.Consumed,(await h.Service.AcceptAsync(canonical.Id,None)).Failure!.Code);
        p=(await h.Service.PreviewFixAsync(AnalysisCatalog.Fix,None)).Preview!;
        int events=0;var generation=h.F.C.WorkingGeneration;
        h.F.C.WorkspaceChanged+=()=>{events++;Assert.NotNull(h.Service.State.Action);Assert.True(h.Service.State.Busy);};
        var applied=await h.Service.AcceptAsync(p.Id,None);
        Assert.True(applied.Changed);Assert.Equal(ReviewStatus.AppliedSaved,applied.Status);Assert.Equal(generation+1,h.F.C.WorkingGeneration);Assert.Equal(1,events);
        Assert.False(h.Analysis.State is AnalysisState.Completed);Assert.True(h.Service.State.Action!.CanUndo);Assert.True(h.Service.State.Action.Persisted);
        Assert.Equal(KnuFixture.Load("review-after").Bytes,await h.F.C.GetFileBytesAsync(h.F.C.Files[0],None));
        await h.Reanalyze();Assert.Empty(h.Report!.Result.Knu!.FixDescriptors);
        Assert.Equal(ReviewCode.UnknownFix,(await h.Service.PreviewFixAsync(AnalysisCatalog.Fix,None)).Failure!.Code);
        Assert.Equal(ReviewCode.UnknownFix,(await h.Service.PreviewFixAsync("heading-fix",None)).Failure!.Code);
        h.Analysis.SetSelection(AnalysisSelection.EngineStructural());
        var undo=await h.Service.UndoAsync(applied.ActionId!.Value,None);
        Assert.True(undo.Changed);Assert.Equal(ReviewStatus.UndoSaved,undo.Status);Assert.False(h.Service.State.Action!.CanUndo);
        Assert.Equal(bytes,await h.F.C.GetFileBytesAsync(h.F.C.Files[0],None));
        Assert.Equal(2,h.F.Repo.Commits);Assert.NotEqual(before,h.F.Repo.Project.HeadRevisionId);
        Assert.Equal(ReviewCode.Consumed,(await h.Service.UndoAsync(applied.ActionId.Value,None)).Failure!.Code);
        Assert.Equal(ReviewCode.Consumed,(await h.Service.AcceptAsync(p.Id,None)).Failure!.Code);
    }

    [Fact]
    public async Task Generic_multi_file_exact_apply_save_undo_preserves_unrelated_tree_edits_and_parent_chain()
    {
        await using var h=await Harness.Open();var old=h.F.Repo.Revision;
        var p=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview!;int events=0;
        h.F.C.WorkspaceChanged+=()=>{events++;if(events==1)foreach(var f in p.Prepared.Files)Assert.Equal(f.After.ToArray(),h.F.C.Files.Single(w=>w.Id==f.FileId).PendingTextBytes!.ToArray());};
        var apply=await h.Service.AcceptAsync(p.Id,None);Assert.True(apply.Changed);Assert.Equal(1,events);
        var appliedHead=h.F.Repo.Revision;Assert.Equal(old.Id,appliedHead.ParentRevisionId);
        Assert.True(h.F.C.CreateFile("unrelated.txt","new🙂\r\n"));var unrelated=h.F.C.ActiveFile!.Id;
        Assert.True(h.F.C.EditText(unrelated,"unrelated latest"));Assert.True(h.F.C.RenameFile(unrelated,"renamed.txt"));
        var binary=h.F.C.Files.Single(w=>w.MediaKind=="binary");Assert.True(h.F.C.DeleteFile(binary.Id));
        await h.F.C.SaveAsync(None);var middle=h.F.Repo.Revision;
        h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.MasterQualification));h.Analysis.Cancel();
        var undo=await h.Service.UndoAsync(apply.ActionId!.Value,None);Assert.Equal(ReviewStatus.UndoSaved,undo.Status);
        Assert.Equal(middle.Id,h.F.Repo.Revision.ParentRevisionId);Assert.NotEqual(old.Id,h.F.Repo.Revision.Id);
        foreach(var f in p.Prepared.Files)Assert.Equal(f.Before.ToArray(),await h.F.C.GetFileBytesAsync(h.F.C.Files.Single(w=>w.Id==f.FileId),None));
        Assert.Equal("unrelated latest",h.F.C.Files.Single(w=>w.Id==unrelated).Content);Assert.Equal("renamed.txt",h.F.C.Files.Single(w=>w.Id==unrelated).RelativePath);
        Assert.DoesNotContain(h.F.C.Files,w=>w.Id==binary.Id);Assert.Equal(0,h.F.Repo.BinaryReads);
    }

    [Theory]
    [InlineData("hash")][InlineData("length")][InlineData("path")][InlineData("span")][InlineData("expected")][InlineData("overlap")][InlineData("size")]
    public async Task Generic_bad_second_file_never_mutates_or_saves(string mode)
    {
        await using var h=await Harness.Open();var input=await h.Inputs();var second=input[1];
        second=mode switch{
            "hash"=>second with{ExpectedHash=new(new string('0',64))},"length"=>second with{ExpectedLength=1},"path"=>second with{RelativePath="missing.tex"},
            "span"=>second with{Edits=[new(-1,0,Array.Empty<byte>(),B("x"))]},"expected"=>second with{Edits=[new(0,1,B("X"),B("Y"))]},
            "overlap"=>second with{Edits=[new(0,1,B("s"),B("x")),new(0,0,Array.Empty<byte>(),B("y"))]},
            _=>second with{Edits=[new(0,1,B("s"),new byte[(int)WorkspaceTextLimits.FileBytes+1])]},
        };input[1]=second;var set=TextChangeSet.Create(TextPatchOrigin.Ai,"Synthetic generic proposal, not rule proof",input);
        var state=h.F.State();if(set.ChangeSet is{} proposal)Assert.Null((await h.Service.PreviewAsync(proposal,None)).Preview);else Assert.NotNull(set.Failure);
        Assert.Equal(state,h.F.State());Assert.Equal(0,h.F.Repo.Commits);
    }

    [Fact]
    public async Task Proposal_ownership_metadata_bounds_and_diff_summary_never_truncate_the_edit()
    {
        await using var h=await Harness.Open();var input=await h.Inputs();var expected=input[0].Edits[0].Expected.ToArray();var replacement=B("<script>plain🙂</script>");
        input[0]=input[0] with{Edits=[new(input[0].Edits[0].StartByte,input[0].Edits[0].EndByte,expected,replacement)]};
        var creation=TextChangeSet.Create(TextPatchOrigin.Ai,"<b>literal explanation</b>",input);var owned=creation.ChangeSet!;
        Array.Fill(expected,(byte)0);Array.Fill(replacement,(byte)0);input.Clear();
        var p=(await h.Service.PreviewAsync(owned,None)).Preview!;Assert.Contains("<script>",p.Prepared.Files[0].After.Content);Assert.Contains("<b>",p.Proposal.Explanation);
        Assert.False(p.Files[0].SummaryOnly);Assert.Contains("exact byte",p.Files[0].DiffScope);
        Assert.Null(TextChangeSet.Create(TextPatchOrigin.Rule,"not canonical",await h.Inputs()).ChangeSet);
        Assert.Null(TextChangeSet.Create(TextPatchOrigin.User,new string('x',4097),await h.Inputs()).ChangeSet);
        var metadataBomb=new FilePatchInput(FileId.New(),"bounded.tex",new(new string('a',64)),200000,
            Enumerable.Repeat(new TextEditInput(0,0,Array.Empty<byte>(),B("x")),200000).ToArray());
        Assert.Null(TextChangeSet.Create(TextPatchOrigin.User,"Bound metadata before ownership copies",[metadataBomb]).ChangeSet);
        h.Service.Decline(p.Id);
        var big=new string('x',3000)+"\n";h.F.C.EditText(h.F.C.Files[0].Id,string.Concat(Enumerable.Repeat(big,400)));
        var large=(await h.Service.PreviewAsync(await h.Generic(false),None)).Preview!;
        Assert.True(large.Files[0].SummaryOnly);Assert.True(large.Prepared.Files[0].After.Length>ArchiveLimits.DiffByteBudget);
        Assert.Equal(0,h.F.Repo.Commits);
    }

    public static IEnumerable<object[]> PreviewRaces()=>from stage in new[]{"read","epoch"} from change in new[]{"edit","tree","head","open","work","profile","worker","cancel","dispose","new-preview","decline"} select new object[]{stage,change};
    [Theory,MemberData(nameof(PreviewRaces))]
    public async Task Held_preview_does_not_resurrect_or_replace_newer_intent(string stage,string change)
    {
        await using var h=await Harness.Open(stage=="epoch");var gate=new WorkspaceTextPatchTests.Barrier();
        if(stage=="read")h.F.Repo.ReadBarrier=gate;else h.Analyzer.EpochBarrier=gate;
        if(stage=="epoch"&&change=="new-preview")h.Analyzer.IgnoreEpochCancellation=true;
        var pending=stage=="epoch"?h.Service.PreviewFixAsync(AnalysisCatalog.Fix,None):h.Service.PreviewAsync(await h.GenericBeforeGate(),None);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ReviewPreview? newer=null;
        switch(change){
            case "edit":h.F.C.EditText(h.F.C.Files[0].Id,"new edit");break;case "tree":h.F.C.RenameFile(h.F.C.Files[0].Id,"new.tex");break;
            case "head":h.F.Repo.AdvanceHead();await h.F.C.OpenAsync(h.F.Repo.Project.Id,None);break;case "open":await h.F.C.OpenAsync(h.F.Repo.Project.Id,None);break;
            case "work":h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.Coursework));h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification));break;
            case "profile":h.Analysis.SetSelection(AnalysisSelection.EngineStructural());h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification));break;
            case "worker":h.Analyzer.Epoch++;break;case "dispose":await h.Service.DisposeAsync();break;
            case "new-preview":
                h.Service.Cancel();h.F.Repo.ReadBarrier=null;h.Analyzer.EpochBarrier=null;
                if(stage=="read")
                {
                    // This repository read cooperates with cancellation. Await
                    // its ACTUAL settlement before admitting a fresh preview.
                    Assert.Null((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Preview);
                    Assert.False(h.Service.State.Busy);
                    newer=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview;
                    Assert.NotNull(newer);
                }
                else
                {
                    // The epoch probe deliberately ignores cancellation here.
                    // Clearing the fixture hook does not settle that old call.
                    Assert.True(h.Service.State.Busy);Assert.False(pending.IsCompleted);
                    var replacement=await h.Service.PreviewAsync(await h.Generic(),None);
                    Assert.Equal(ReviewCode.Busy,replacement.Failure?.Code);
                }
                break;
            default:h.Service.Cancel();break;
        }
        gate.Release.TrySetResult();var result=await pending.WaitAsync(TimeSpan.FromSeconds(5));
        // Generic proposals have no worker authority; a worker-only reset is irrelevant.
        if(stage=="read"&&change=="worker")Assert.NotNull(result.Preview);else Assert.Null(result.Preview);
        if(change=="new-preview"&&stage=="epoch")
        {
            Assert.False(h.Service.State.Busy);
            newer=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview;
            Assert.NotNull(newer);
        }
        if(newer is not null)Assert.Same(newer,h.Service.State.Preview);
        Assert.Equal(0,h.F.Repo.Commits);
    }

    [Theory]
    [InlineData("read")][InlineData("ownership")][InlineData("head")][InlineData("epoch")]
    public async Task Final_acceptance_guards_reject_canonical_report_cancelled_during_validation(string stage)
    {
        await using var h=await Harness.Open(true);var p=(await h.Service.PreviewFixAsync(AnalysisCatalog.Fix,None)).Preview!;
        var gate=new WorkspaceTextPatchTests.Barrier();switch(stage){case "read":h.F.Repo.ReadBarrier=gate;break;case "ownership":h.F.Locks.OwnershipBarrier=gate;break;case "head":h.F.Repo.HeadBarrier=gate;break;default:h.Analyzer.EpochBarrier=gate;break;}
        var before=h.F.State();var accepting=h.Service.AcceptAsync(p.Id,None);await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));h.Analysis.Cancel();gate.Release.SetResult();
        var result=await accepting;Assert.False(result.Changed);Assert.NotNull(result.Failure);Assert.Equal(before,h.F.State());Assert.Null(h.Service.State.Action);
    }

    [Fact]
    public async Task Commit_installs_inverse_before_analysis_invalidation_reentrant_accept_and_cancel()
    {
        await using var h=await Harness.Open(true);var p=(await h.Service.PreviewFixAsync(AnalysisCatalog.Fix,None)).Preview!;Task<ReviewResult>? second=null;
        h.F.C.WorkspaceChanged+=()=>{Assert.NotNull(h.Service.State.Action);second=h.Service.AcceptAsync(p.Id,None);h.Service.Cancel();};
        var applied=await h.Service.AcceptAsync(p.Id,None);Assert.True(applied.Changed);Assert.Equal(ReviewStatus.AppliedUnsaved,applied.Status);
        Assert.Equal(ReviewCode.Busy,(await second!).Failure!.Code);Assert.Equal(0,h.F.Repo.Commits);Assert.True(h.Service.State.Action!.CanUndo);
        Assert.True(h.F.C.IsDirty);Assert.False((await h.Service.AcceptAsync(p.Id,None)).Changed);
    }

    [Theory]
    [InlineData("failure")][InlineData("cancel")][InlineData("conflict")][InlineData("later-edit")]
    public async Task Save_failure_cancel_conflict_or_newer_edits_never_reapply_and_retain_recovery(string mode)
    {
        await using var h=await Harness.Open();var p=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview!;
        var barrier=h.F.Repo.CommitBarrier=new();using var cts=new CancellationTokenSource();
        var applying=h.Service.AcceptAsync(p.Id,cts.Token);await barrier.Reached.Task;
        var generation=h.F.C.WorkingGeneration;
        if(mode=="failure")h.F.Repo.FailSave=true;
        if(mode=="cancel")cts.Cancel();
        if(mode=="conflict")h.F.Repo.AdvanceHead();
        if(mode=="later-edit")h.F.C.EditText(h.F.C.Files[0].Id,"manual later🙂");
        barrier.Release.SetResult();var result=await applying;Assert.True(result.Changed);Assert.NotNull(h.Service.State.Action);
        if(mode=="conflict"){
            Assert.Equal(ReviewCode.Conflict,result.Failure!.Code);
            Assert.DoesNotContain("no inverse/edit was applied",result.Failure.SafeMessage);
            Assert.Contains("applied/save state",result.Failure.SafeMessage);
        }
        if(mode!="later-edit")Assert.Equal(generation,h.F.C.WorkingGeneration);
        Assert.Equal(ReviewCode.Consumed,(await h.Service.AcceptAsync(p.Id,None)).Failure?.Code??ReviewCode.Consumed);
        if(mode=="failure"){
            Assert.False(h.Service.State.Action!.Persisted);var another=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview!;
            Assert.Equal(ReviewCode.PendingSave,(await h.Service.AcceptAsync(another.Id,None)).Failure!.Code);
            h.F.Repo.FailSave=false;var saved=await h.Service.RetrySaveAsync(result.ActionId!.Value,None);Assert.Equal(ReviewStatus.AppliedSaved,saved.Status);Assert.Equal(generation,h.F.C.WorkingGeneration);
        }
        if(mode=="later-edit"){Assert.Equal("manual later🙂",h.F.C.Files[0].Content);Assert.True(h.Service.State.Action!.Persisted);Assert.True(h.Service.State.WorkspaceDirty);Assert.Equal(ReviewCode.Conflict,(await h.Service.UndoAsync(result.ActionId!.Value,None)).Failure!.Code);}
    }

    [Theory]
    [InlineData("edit")][InlineData("rename")][InlineData("delete")]
    public async Task Affected_file_changes_conflict_without_losing_undo(string change)
    {
        await using var h=await Harness.Open();var p=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview!;var applied=await h.Service.AcceptAsync(p.Id,None);var first=h.F.C.Files[0];
        if(change=="edit")h.F.C.EditText(first.Id,"later");if(change=="rename")h.F.C.RenameFile(first.Id,"later.tex");if(change=="delete")h.F.C.DeleteFile(first.Id);
        var state=h.F.State();var result=await h.Service.UndoAsync(applied.ActionId!.Value,None);
        Assert.False(result.Changed);Assert.Equal(ReviewCode.Conflict,result.Failure!.Code);Assert.Equal(state,h.F.State());Assert.True(h.Service.State.Action!.CanUndo);
    }

    [Fact]
    public async Task Undo_of_unsaved_action_and_undo_save_failure_are_single_apply_with_persistence_retry()
    {
        await using var h=await Harness.Open();h.F.Repo.FailSave=true;var p=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview!;
        var applied=await h.Service.AcceptAsync(p.Id,None);Assert.True(applied.Changed);Assert.False(h.Service.State.Action!.Persisted);
        var undo=await h.Service.UndoAsync(applied.ActionId!.Value,None);Assert.True(undo.Changed);Assert.False(h.Service.State.Action!.Persisted);Assert.True(h.Service.State.Action.UndoApplied);
        var generation=h.F.C.WorkingGeneration;Assert.Equal(ReviewCode.Consumed,(await h.Service.UndoAsync(applied.ActionId.Value,None)).Failure!.Code);
        h.F.Repo.FailSave=false;var retry=await h.Service.RetrySaveAsync(applied.ActionId.Value,None);Assert.Equal(ReviewStatus.UndoSaved,retry.Status);Assert.Equal(generation,h.F.C.WorkingGeneration);
        foreach(var f in p.Prepared.Files)Assert.Equal(f.Before.ToArray(),await h.F.C.GetFileBytesAsync(h.F.C.Files.Single(w=>w.Id==f.FileId),None));
    }

    [Fact]
    public async Task Independent_save_receipt_tracks_only_actual_images_and_newer_dirty_work_is_explicit()
    {
        await using var h=await Harness.Open();h.F.Repo.FailSave=true;var p=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview!;
        var result=await h.Service.AcceptAsync(p.Id,None);h.F.Repo.FailSave=false;await h.F.C.SaveAsync(None);
        Assert.True(h.Service.State.Action!.Persisted);
        h.F.C.CreateFile("notes.txt","later draft");Assert.True(h.Service.State.WorkspaceDirty);Assert.True(h.Service.State.Action.Persisted);
        Assert.Equal(ReviewStatus.AppliedSaved,h.Service.State.Status);Assert.NotNull(result.ActionId);
    }

    [Theory]
    [InlineData("head")][InlineData("lock")][InlineData("open")][InlineData("cancel")][InlineData("double")][InlineData("selection")]
    public async Task Held_inverse_validation_never_overwrites_or_applies_twice(string change)
    {
        await using var h=await Harness.Open();var p=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview!;var action=await h.Service.AcceptAsync(p.Id,None);
        var gate=h.F.Repo.HeadBarrier=new();using var cts=new CancellationTokenSource();var undo=h.Service.UndoAsync(action.ActionId!.Value,cts.Token);await gate.Reached.Task;
        Task<ReviewResult>? second=null;
        if(change=="head")h.F.Repo.AdvanceHead();if(change=="lock")h.F.Locks.Owned=false;if(change=="open")await h.F.C.OpenAsync(h.F.Repo.Project.Id,None);if(change=="cancel")cts.Cancel();if(change=="double")second=h.Service.UndoAsync(action.ActionId.Value,None);
        if(change=="selection"){h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.MasterQualification));h.Analyzer.Epoch++;}
        var gen=h.F.C.WorkingGeneration;gate.Release.SetResult();var r=await undo;
        if(change=="double"){Assert.True(r.Changed);Assert.Equal(ReviewCode.Busy,(await second!).Failure!.Code);}
        else if(change=="selection")Assert.True(r.Changed);
        else{Assert.False(r.Changed);Assert.Equal(gen,h.F.C.WorkingGeneration);}
        if(change=="open")Assert.Null(h.Service.State.Action);else Assert.NotNull(h.Service.State.Action);
    }

    [Fact]
    public async Task Disposing_after_commit_retains_truth_and_old_save_cannot_update_a_reopened_session()
    {
        await using var h=await Harness.Open();var p=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview!;var gate=h.F.Repo.CommitBarrier=new();
        h.F.Repo.IgnoreCommitCancellation=true; // late durable completion, not a cooperative early cancellation
        var applied=h.Service.AcceptAsync(p.Id,None);await gate.Reached.Task;await h.Service.DisposeAsync();await h.F.C.OpenAsync(h.F.Repo.Project.Id,None);
        var session=h.F.C.SessionId;var revision=h.F.C.CurrentRevision!.Id;gate.Release.SetResult();var r=await applied;
        Assert.True(r.Changed);Assert.Equal(session,h.F.C.SessionId);Assert.Equal(revision,h.F.C.CurrentRevision.Id);Assert.Null(h.Service.State.Action);Assert.Equal(ReviewStatus.Disposed,h.Service.State.Status);
    }

    public static IEnumerable<object[]> NetZeroInverseCases()
    {
        foreach(var kind in new[]{"lead-replacement","delete-insert","long-replacement","unicode-shift"})
            foreach(var bom in new[]{false,true})
                foreach(var retryUndoSave in new[]{false,true})yield return [kind,bom,retryUndoSave];
    }

    [Theory,MemberData(nameof(NetZeroInverseCases))]
    public async Task Net_zero_inverse_cluster_is_omitted_without_losing_real_edits_or_other_files(string kind,bool bom,bool retryUndoSave)
    {
        await using var h=await Harness.Open();
        var body=kind switch{"delete-insert"=>"a_X","long-replacement"=>"abcd_X","unicode-shift"=>"Q_Ж🙂_\r\nX\rΩ\n",_=>"ab_X"};
        WorkingFile target;
        if(kind=="lead-replacement"&&bom)
        {
            // Exact lead reproducer: first text file, retained existing BOM.
            target=h.F.C.Files[0];Assert.True(target.HasBom);Assert.True(h.F.C.EditText(target.Id,body));
        }
        else
        {
            // Raw imported controls preserve CRLF/lone CR/LF rather than invoking
            // the intentionally different manual LF encoding policy.
            byte[] imported=bom?[239,187,191,..B(body)]:B(body);
            Assert.True(h.F.C.CreateFileFromBytes("inverse-edge.tex",imported));target=h.F.C.ActiveFile!;
        }
        var before=await h.F.C.GetFileBytesAsync(target,None);int at=target.HasBom?3:0;
        TextEditInput E(int start,int end,string expected,string replacement)=>new(at+start,at+end,B(expected),B(replacement));
        TextEditInput[] edits=kind switch{
            "delete-insert"=>[E(0,1,"a",""),E(1,1,"","a"),E(2,3,"X","Y")],
            "long-replacement"=>[E(0,1,"a","abcd"),E(1,4,"bcd",""),E(5,6,"X","YZ")],
            "unicode-shift"=>[E(0,1,"Q","QQ🙂"),E(2,4,"Ж","Ж🙂"),E(4,8,"🙂",""),E(11,12,"X","Ї🙂")],
            _=>[E(0,1,"a","ab"),E(1,2,"b",""),E(3,4,"X","Y")],
        };
        var wantedBody=kind switch{"delete-insert"=>"a_Y","long-replacement"=>"abcd_YZ","unicode-shift"=>"QQ🙂_Ж🙂_\r\nЇ🙂\rΩ\n",_=>"ab_Y"};
        byte[] wanted=bom?[239,187,191,..B(wantedBody)]:B(wantedBody);
        var other=h.F.C.Files.Single(w=>w.RelativePath=="b.tex");var otherBefore=await h.F.C.GetFileBytesAsync(other,None);
        var otherAfter=otherBefore.ToArray();otherAfter[0]=(byte)'S';
        // Keep the retained-BOM lead case exactly single-file/three-edits;
        // other cases also prove that another genuinely changed file is kept.
        bool patchOther=kind!="lead-replacement"||!bom;
        var patches=new List<FilePatchInput>{new(target.Id,target.RelativePath,Hash(before),before.Length,edits)};
        if(patchOther)patches.Add(new(other.Id,other.RelativePath,Hash(otherBefore),otherBefore.Length,[new(0,1,B("s"),B("S"))]));
        var proposal=TextChangeSet.Create(TextPatchOrigin.User,"Synthetic net-zero inverse cluster plus separated real edits",patches).ChangeSet!;
        var generation=h.F.C.WorkingGeneration;var baseHead=h.F.C.CurrentRevision!.Id;
        var preview=(await h.Service.PreviewAsync(proposal,None)).Preview!;
        Assert.Equal(patchOther?2:1,preview.Prepared.Files.Length);Assert.Equal(wanted,preview.Prepared.Files.Single(f=>f.FileId==target.Id).After.ToArray());
        var applied=await h.Service.AcceptAsync(preview.Id,None);
        Assert.True(applied.Changed);Assert.Null(applied.Failure);Assert.Equal(ReviewStatus.AppliedSaved,applied.Status);
        var appliedHead=h.F.C.CurrentRevision!.Id;Assert.Equal(baseHead,h.F.C.CurrentRevision.ParentRevisionId);
        Assert.Equal(wanted,await h.F.C.GetFileBytesAsync(target,None));Assert.Equal(patchOther?otherAfter:otherBefore,await h.F.C.GetFileBytesAsync(other,None));
        Assert.Equal(generation+1,h.F.C.WorkingGeneration);Assert.True(h.Service.State.Action!.CanUndo);
        // No intervening work: the complete postimages still match exactly.
        h.F.Repo.FailSave=retryUndoSave;
        var undone=await h.Service.UndoAsync(applied.ActionId!.Value,None);
        Assert.True(undone.Changed);Assert.NotEqual(ReviewCode.Conflict,undone.Failure?.Code);
        Assert.Equal(before,await h.F.C.GetFileBytesAsync(target,None));Assert.Equal(otherBefore,await h.F.C.GetFileBytesAsync(other,None));
        Assert.Equal(generation+2,h.F.C.WorkingGeneration);Assert.False(h.Service.State.Action!.CanUndo);
        Assert.Equal(ReviewCode.Consumed,(await h.Service.UndoAsync(applied.ActionId.Value,None)).Failure!.Code);
        if(retryUndoSave)
        {
            Assert.Equal(ReviewStatus.UndoAppliedUnsaved,undone.Status);Assert.False(h.Service.State.Action.Persisted);
            h.F.Repo.FailSave=false;var retry=await h.Service.RetrySaveAsync(applied.ActionId.Value,None);
            Assert.Equal(ReviewStatus.UndoSaved,retry.Status);Assert.False(retry.Changed);
        }
        else{Assert.Equal(ReviewStatus.UndoSaved,undone.Status);Assert.Null(undone.Failure);}
        Assert.Equal(generation+2,h.F.C.WorkingGeneration);Assert.Equal(2,h.F.Repo.Commits);
        var saved=h.F.C.CurrentRevision!;Assert.NotEqual(appliedHead,saved.Id);Assert.Equal(appliedHead,saved.ParentRevisionId);
        Assert.Equal(before,h.F.Repo.Content[saved.FindFile(target.Id)!.ContentHash]);
        Assert.Equal(otherBefore,h.F.Repo.Content[saved.FindFile(other.Id)!.ContentHash]);
        Assert.False(h.F.C.IsDirty);Assert.True(h.Service.State.Action.Persisted);
    }

    [Fact]
    public async Task Adjacent_deletions_and_insertion_form_one_valid_exact_inverse()
    {
        await using var h=await Harness.Open();var w=h.F.C.Files[0];h.F.C.EditText(w.Id,"abcd\r\n");var b=await h.F.C.GetFileBytesAsync(w,None);int at=w.HasBom?3:0;
        var input=new FilePatchInput(w.Id,w.RelativePath,Hash(b),b.Length,[new(at,at+1,B("a"),Array.Empty<byte>()),new(at+1,at+2,B("b"),Array.Empty<byte>()),new(at+2,at+2,Array.Empty<byte>(),B("🙂"))]);
        var proposal=TextChangeSet.Create(TextPatchOrigin.User,"Adjacent synthetic edits",[input]).ChangeSet!;
        var p=(await h.Service.PreviewAsync(proposal,None)).Preview!;var result=await h.Service.AcceptAsync(p.Id,None);
        Assert.Equal(ReviewStatus.AppliedSaved,result.Status);Assert.Equal(ReviewStatus.UndoSaved,(await h.Service.UndoAsync(result.ActionId!.Value,None)).Status);
        Assert.Equal(b,await h.F.C.GetFileBytesAsync(w,None));
    }

    [Fact]
    public async Task Affected_edit_in_commit_notification_is_not_mislabeled_as_persisted_review()
    {
        await using var h=await Harness.Open();var p=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview!;bool once=true;
        h.F.C.WorkspaceChanged+=()=>{if(once){once=false;h.F.C.EditText(h.F.C.Files[0].Id,"changed before save capture");}};
        var r=await h.Service.AcceptAsync(p.Id,None);Assert.True(r.Changed);Assert.False(h.Service.State.Action!.Persisted);Assert.Equal(ReviewCode.PendingSave,r.Failure!.Code);
        Assert.Equal("changed before save capture",h.F.C.Files[0].Content);Assert.Equal(ReviewCode.Conflict,(await h.Service.UndoAsync(r.ActionId!.Value,None)).Failure!.Code);
    }

    [Fact]
    public async Task Throwing_post_commit_observer_cannot_erase_inverse_or_report_no_change()
    {
        await using var h=await Harness.Open();var p=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview!;
        void Broken()=>throw new IOException("secret observer failure");h.F.C.WorkspaceChanged+=Broken;
        var result=await h.Service.AcceptAsync(p.Id,None);h.F.C.WorkspaceChanged-=Broken;
        Assert.True(result.Changed);Assert.NotNull(h.Service.State.Action);Assert.DoesNotContain("secret",result.Failure!.SafeMessage);
        Assert.True((await h.Service.UndoAsync(result.ActionId!.Value,None)).Changed);
    }

    [Fact]
    public async Task Readonly_preview_is_valid_but_acceptance_does_not_grant_writer_permission()
    {
        await using var h=await Harness.Open(true);h.F.Locks.Acquire=false;await h.F.C.OpenAsync(h.F.Repo.Project.Id,None);await h.Reanalyze();
        var p=(await h.Service.PreviewFixAsync(AnalysisCatalog.Fix,None)).Preview!;Assert.NotNull(p);var state=h.F.State();
        var result=await h.Service.AcceptAsync(p.Id,None);Assert.False(result.Changed);Assert.Equal(ReviewCode.ReadOnly,result.Failure!.Code);Assert.Equal(state,h.F.State());
    }

    [Theory]
    [InlineData("cancel")][InlineData("worker")][InlineData("work-type")][InlineData("profile")]
    public async Task Final_worker_query_is_authoritative_and_selection_switch_back_cannot_authorize_accept(string change)
    {
        await using var h=await Harness.Open(true);var p=(await h.Service.PreviewFixAsync(AnalysisCatalog.Fix,None)).Preview!;
        var gate=h.Analyzer.EpochBarrier=new();using var cts=new CancellationTokenSource();var state=h.F.State();var accepting=h.Service.AcceptAsync(p.Id,cts.Token);await gate.Reached.Task;
        if(change=="cancel")cts.Cancel();if(change=="worker")h.Analyzer.Epoch++;
        if(change=="work-type"){h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.MasterQualification));h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification));}
        if(change=="profile"){h.Analysis.SetSelection(AnalysisSelection.EngineStructural());h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification));}
        gate.Release.SetResult();Assert.False((await accepting).Changed);Assert.Equal(state,h.F.State());Assert.Null(h.Service.State.Action);
    }

    [Fact]
    public async Task Explicit_later_action_replaces_only_the_persisted_single_level_entry()
    {
        await using var h=await Harness.Open();var first=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview!;var a=await h.Service.AcceptAsync(first.Id,None);
        var second=(await h.Service.PreviewAsync(await h.Generic(),None)).Preview!;var b=await h.Service.AcceptAsync(second.Id,None);
        Assert.True(b.Changed);Assert.NotEqual(a.ActionId,b.ActionId);Assert.Equal(ReviewCode.Consumed,(await h.Service.UndoAsync(a.ActionId!.Value,None)).Failure!.Code);
        Assert.Equal(ReviewStatus.UndoSaved,(await h.Service.UndoAsync(b.ActionId!.Value,None)).Status);
        foreach(var f in first.Prepared.Files)Assert.Equal(f.After.ToArray(),await h.F.C.GetFileBytesAsync(h.F.C.Files.Single(w=>w.Id==f.FileId),None));
    }

    [Fact]
    public async Task Razor_review_component_escapes_explanations_and_bounds_large_previews_without_changing_owned_edits()
    {
        await using var h=await Harness.Open();var file=h.F.C.Files[0];
        Assert.True(h.F.C.EditText(file.Id,"<img src=x onerror=alert(1)>"+new string('x',1200000)));
        var inputs=await h.Inputs();var set=TextChangeSet.Create(TextPatchOrigin.User,"<script>untrusted explanation</script>",inputs.Take(1).ToArray()).ChangeSet!;
        var preview=(await h.Service.PreviewAsync(set,None)).Preview!;
        using var services=new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        var html=await renderer.Dispatcher.InvokeAsync(async()=>{
            var rendered=await renderer.RenderComponentAsync<ReviewPanel>(ParameterView.FromDictionary(new Dictionary<string,object?>{{"State",h.Service.State},{"Writable",true}}));
            return rendered.ToHtmlString();
        });
        Assert.Contains("&lt;script&gt;untrusted explanation&lt;/script&gt;",html);Assert.Contains("&lt;img src=x",html);
        Assert.DoesNotContain("<script>",html);Assert.DoesNotContain("<img ",html);Assert.Contains("bounded excerpt",html);Assert.Contains("not a complete diff",html);
        Assert.True(html.Length<20000);Assert.True(preview.Prepared.Files[0].Before.Length>1000000);
        Assert.Equal(0,h.F.Repo.Commits);Assert.Equal(preview.Id,h.Service.State.Preview!.Id);
    }

    private sealed class Harness:IAsyncDisposable
    {
        internal WorkspaceTextPatchTests.Fixture F=null!;internal AnalysisCoordinator Analysis=null!;internal Analyzer Analyzer=null!;internal ChangeReviewService Service=null!;
        internal AnalysisReport? Report=> (Analysis.State as AnalysisState.Completed)?.Report;
        internal List<FilePatchInput>? CachedInputs;
        internal static async Task<Harness> Open(bool f1=false)
        {
            var h=new Harness{F=await WorkspaceTextPatchTests.Fixture.Open()};
            if(f1){await h.F.C.DisposeAsync();var data=KnuFixture.Load("negative").Bytes;var id=h.F.Repo.Revision.Files[0].Id;var file=new ProjectFile(id,"main.tex","text",Hash(data),data.Length,true);
                h.F.Repo.Content[file.ContentHash]=data;h.F.Repo.Revision=h.F.Repo.Revision with{Files=ImmutableArray.Create(file)};
                h.F.C=new(h.F.Repo,h.F.Locks,new HistoryService(h.F.Repo));await h.F.C.OpenAsync(h.F.Repo.Project.Id,None);}
            h.Analyzer=new Analyzer(f1);h.Analysis=new(h.F.C,h.Analyzer,delayAsync:(_,ct)=>Task.Delay(Timeout.Infinite,ct));
            h.Service=new(h.F.C,h.Analysis,new SourceNavigator(h.Analysis,h.F.C,h.Analyzer));
            if(f1){var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);h.Analysis.Changed+=s=>{if(s is AnalysisState.Completed)completed.TrySetResult();};h.Analysis.SetSelection(AnalysisSelection.Knu(AnalysisWorkType.BachelorQualification));await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));}
            h.CachedInputs=await h.Inputs();return h;
        }
        internal async Task Reanalyze(){var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);void Done(AnalysisState s){if(s is AnalysisState.Completed)reached.TrySetResult();}Analysis.Changed+=Done;Analysis.RequestReanalyze();await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));Analysis.Changed-=Done;}
        internal async Task<List<FilePatchInput>> Inputs()
        {
            var result=new List<FilePatchInput>();foreach(var w in F.C.Files.Where(w=>!w.Deleted&&w.MediaKind=="text")){
                var b=await F.C.GetFileBytesAsync(w,None);int bom=w.HasBom?3:0;int width=(b[bom]&0x80)==0?1:2;
                result.Add(new(w.Id,w.RelativePath,Hash(b),b.Length,[new(bom,bom+width,b.AsMemory(bom,width),B("Ю🙂")),new(b.Length-1,b.Length,b.AsMemory(b.Length-1),B("end\n"))]));
            }return result;
        }
        internal async Task<TextChangeSet> Generic(bool all=true){var inputs=await Inputs();return TextChangeSet.Create(TextPatchOrigin.User,"Synthetic generic multi-file review",all?inputs:inputs.Take(1).ToArray()).ChangeSet!;}
        internal Task<TextChangeSet> GenericBeforeGate()=>Task.FromResult(TextChangeSet.Create(TextPatchOrigin.User,"Synthetic held review",CachedInputs!).ChangeSet!);
        public async ValueTask DisposeAsync(){F.Repo.ReadBarrier?.Release.TrySetResult();F.Repo.HeadBarrier?.Release.TrySetResult();F.Repo.CommitBarrier?.Release.TrySetResult();F.Locks.OwnershipBarrier?.Release.TrySetResult();Analyzer.EpochBarrier?.Release.TrySetResult();await Service.DisposeAsync();await Analysis.DisposeAsync();await Analyzer.DisposeAsync();await F.DisposeAsync();}
    }
    private sealed class Analyzer:ISourceAnalyzer
    {
        internal long Epoch=7;internal WorkspaceTextPatchTests.Barrier? EpochBarrier;internal bool IgnoreEpochCancellation;
        private RustSourceAnalyzer? _native;private AnalysisSnapshot? _snapshot;
        internal Analyzer(bool native){if(native){var js=new KnuProtocolTests.FixtureJs((_,rid)=>{
            var snap=_snapshot!;var name=snap.Files[0].Bytes.AsSpan().SequenceEqual(KnuFixture.Load("negative").Bytes)?"negative":"review-after";return KnuFixture.Load(name).Reply(snap,rid);
        });_native=new(js);}}
        public Task<AnalysisJobResult> AnalyzeAsync(AnalysisSnapshot snapshot,CancellationToken ct){_snapshot=snapshot;return _native?.AnalyzeAsync(snapshot,ct)??Task.FromResult<AnalysisJobResult>(new AnalysisJobResult.Cancelled("synthetic-generic",Epoch));}
        public async Task<bool> IsCurrentWorkerEpochAsync(long epoch,CancellationToken ct){if(EpochBarrier is{} gate)await gate.Wait(IgnoreEpochCancellation?CancellationToken.None:ct);return Epoch==epoch;}
        public async ValueTask DisposeAsync(){if(_native is not null)await _native.DisposeAsync();}
    }
}
