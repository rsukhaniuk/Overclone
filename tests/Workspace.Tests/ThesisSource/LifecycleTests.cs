using Client.Analysis;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.ThesisSource.ThesisTestHost;

namespace Workspace.Tests.ThesisSource;

public sealed class LifecycleTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Explicit_root_readonly_unicode_and_empty_caption_navigation_never_mutates(bool readOnly)
    {
        await using var h=await ThesisTestHost.Open(readOnly);
        var report=await h.Fresh(()=>h.Analysis.SetSelection(h.Selection()));
        h.Workspace.SelectFile(h.B);
        var navigator=new ThesisSourceNavigation(new SourceNavigator(h.Analysis,h.Workspace,h.Analyzer),h.Workspace);
        var head=h.Workspace.CurrentRevision!.Id;var generation=h.Workspace.WorkingGeneration;
        var thesis=report.Result.Thesis!;
        foreach(var index in Enumerable.Range(0,thesis.Events.Length).Where(i=>thesis.Events[i].Kind is "cite" or "bibitem" or "captionTitle" or "appendix"))
        {
            var e=thesis.Events[index];var selected=Assert.IsType<NavigationResult.Selected>(await navigator.NavigateAsync(new(report,index),default));
            Assert.Equal(h.A,selected.FileId);
            Assert.Equal(e.Kind=="captionTitle"?"":e.Text,selected.ExpectedNormalizedSubstring);
            if(e.Kind=="captionTitle")Assert.Equal(selected.TextareaStart,selected.TextareaEnd);
        }
        Assert.Equal(head,h.Workspace.CurrentRevision!.Id);Assert.Equal(generation,h.Workspace.WorkingGeneration);Assert.Equal(0,h.Commits);
        Assert.IsType<NavigationResult.Rejected>(await navigator.NavigateAsync(new(report,-1),default));
        Assert.IsType<NavigationResult.Rejected>(await navigator.NavigateAsync(new(report with{WorkerEpoch=99},0),default));
    }

    [Theory]
    [InlineData("capture","root")][InlineData("import","root")][InlineData("response","root")][InlineData("epoch","root")]
    [InlineData("capture","workType")][InlineData("response","workType")][InlineData("epoch","workType")]
    [InlineData("response","profile")][InlineData("epoch","profile")]
    public async Task Held_dependencies_and_ABA_only_publish_latest_authority(string stage,string change)
    {
        await using var h=await ThesisTestHost.Open();
        if(stage=="capture")h.HoldRead();
        if(stage=="import")h.Js.ImportGate=Gate();
        if(stage=="response")h.Js.ResponseGate=Gate();
        if(stage=="epoch")h.Js.EpochGate=Gate();
        var reports=new List<AnalysisReport>();h.Analysis.Changed+=s=>{if(s is AnalysisState.Completed c)reports.Add(c.Report);};
        var pending=h.Fresh(()=>h.Analysis.SetSelection(h.Selection()));
        await (stage switch{"capture"=>h.ReadReached,"import"=>h.Js.ImportReached.Task,"response"=>h.Js.ResponseReached.Task,_=>h.Js.EpochReached.Task}).WaitAsync(Bound);
        var old=h.Analyzer.Captured.FirstOrDefault();
        h.Analysis.SetSelection(change switch {
            "root"=>h.Selection(h.B), "workType"=>h.Selection(type:AnalysisWorkType.MasterQualification),
            _=>AnalysisSelection.KnuWithFormatting(AnalysisWorkType.Unspecified),
        });
        h.Analysis.SetSelection(h.Selection());
        Assert.False(h.Analysis.State is AnalysisState.Completed);
        h.Release();var current=await pending.WaitAsync(Bound);
        Assert.Equal(3,current.Snapshot.SelectionVersion);Assert.Equal(h.A.ToString(),current.Result.Thesis!.RootFileId);
        Assert.Single(reports);Assert.Equal(0,h.Commits);
        if(old is not null)Assert.NotEqual(old.Id,current.Snapshot.Id);
    }

    [Theory]
    [InlineData("source")][InlineData("root")][InlineData("workType")][InlineData("profile")]
    [InlineData("head")][InlineData("session")][InlineData("project")][InlineData("worker")]
    public async Task Authority_loss_while_navigation_awaits_never_selects(string change)
    {
        await using var h=await ThesisTestHost.Open();var report=await h.Fresh(()=>h.Analysis.SetSelection(h.Selection()));
        var nav=new ThesisSourceNavigation(new SourceNavigator(h.Analysis,h.Workspace,h.Analyzer),h.Workspace);
        h.Js.EpochReached=Gate();h.Js.EpochGate=Gate();var pending=nav.NavigateAsync(new(report,0),default);
        await h.Js.EpochReached.Task.WaitAsync(Bound);
        switch(change) {
            case "source":Assert.True(h.Workspace.EditText(h.A,"new source"));break;
            case "root":h.Analysis.SetSelection(h.Selection(h.B));break;
            case "workType":h.Analysis.SetSelection(h.Selection(type:AnalysisWorkType.Coursework));break;
            case "profile":h.Analysis.SetSelection(AnalysisSelection.KnuWithFormatting(AnalysisWorkType.Unspecified));break;
            case "head":Assert.True(h.Workspace.EditText(h.B,"different saved body"));await h.Workspace.SaveAsync(default);break;
            case "session":await h.Workspace.OpenAsync(h.Workspace.Project!.Id,default);break;
            case "project":await h.Workspace.CloseAsync(default);break;
            case "worker":h.Js.CurrentEpoch=8;break;
        }
        h.Release();Assert.IsType<NavigationResult.Rejected>(await pending.WaitAsync(Bound));
    }

    [Theory]
    [InlineData("missing")][InlineData("nontex")][InlineData("deleted")][InlineData("binary")]
    public async Task Invalid_root_is_not_retargeted_and_never_dispatched(string kind)
    {
        await using var h=await ThesisTestHost.Open();var root=kind=="missing"?null:h.A.ToString();
        if(kind=="nontex")Assert.True(h.Workspace.RenameFile(h.A,"notes.txt"));
        if(kind=="deleted")Assert.True(h.Workspace.DeleteFile(h.A));
        if(kind=="binary") { Assert.True(h.Workspace.CreateFileFromBytes("asset.bin",new byte[]{0,255,128}));root=h.Workspace.ActiveFileId!.Value.ToString(); }
        h.Analysis.SetSelection(AnalysisSelection.KnuWithThesis(AnalysisWorkType.Unspecified,root));
        Assert.Equal(AnalysisCatalog.ThesisVersion,h.Analysis.CurrentProfile.Version);
        await using var factory=new AnalysisSnapshotFactory(h.Workspace);
        Assert.IsType<CaptureResult.InvalidInput>(await factory.CaptureSelectionAsync(h.Analysis.CurrentSelection,default));
        Assert.Empty(h.Js.Calls);
    }
    [Fact]
    public async Task Cancellation_invalidates_authority_but_keeps_actual_capture_and_epoch_admission()
    {
        await using var h=await ThesisTestHost.Open();h.HoldRead();
        var pending=h.Fresh(()=>h.Analysis.SetSelection(h.Selection()));
        await h.ReadReached.WaitAsync(Bound);
        h.Analysis.Cancel();h.Analysis.RequestReanalyze();
        Assert.Empty(h.Analyzer.Captured);Assert.False(pending.IsCompleted);
        h.Release();var report=await pending.WaitAsync(Bound);Assert.Single(h.Analyzer.Captured);
        var nav=new ThesisSourceNavigation(new SourceNavigator(h.Analysis,h.Workspace,h.Analyzer),h.Workspace);
        h.Js.EpochReached=Gate();h.Js.EpochGate=Gate();using var cancel=new CancellationTokenSource();
        var selection=nav.NavigateAsync(new(report,0),cancel.Token);await h.Js.EpochReached.Task.WaitAsync(Bound);
        cancel.Cancel();h.Release();Assert.IsType<NavigationResult.Rejected>(await selection.WaitAsync(Bound));
    }
    [Fact]
    public async Task Worker_epoch_loss_during_publish_retains_no_completed_report()
    {
        await using var h=await ThesisTestHost.Open();h.Js.EpochGate=Gate();
        var superseded=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Analysis.Changed+=s=>{if(s is AnalysisState.Superseded)superseded.TrySetResult(true);};
        h.Analysis.SetSelection(h.Selection());await h.Js.EpochReached.Task.WaitAsync(Bound);
        h.Js.CurrentEpoch=8;h.Release();await superseded.Task.WaitAsync(Bound);Assert.IsType<AnalysisState.Superseded>(h.Analysis.State);
    }
}
