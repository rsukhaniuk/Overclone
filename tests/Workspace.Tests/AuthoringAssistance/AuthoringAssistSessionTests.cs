using System.Text;
using System.Text.Json;
using Client.Analysis;
using Client.Editing;
using Client.Editing.Assistance;
using Client.Workspace;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;

namespace Workspace.Tests.AuthoringAssistance;

// Real workspace/factory/span mapping; modeled DOM/provider boundaries only.
public sealed partial class AuthoringAssistSessionTests
{
    private static readonly TimeSpan Bound=TimeSpan.FromSeconds(5);
    private const string Text="Україна😀е\u0301\r\n\\se";
    private sealed class Provider : IAuthoringAssistProvider
    {
        internal int Calls;internal long Epoch;internal AuthoringAssistRequest? Last;
        internal bool Definition,Outline,PathCandidate;
        internal AuthoringAssistStatus Status=AuthoringAssistStatus.Completed;
        internal bool InvalidBody=false;
        internal TaskCompletionSource? Hold;
        internal readonly TaskCompletionSource Reached=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AuthoringAssistReply> EvaluateAsync(AuthoringAssistRequest r,CancellationToken ct=default)
        {Calls++;Last=r;Reached.TrySetResult();if(Hold is not null)await Hold.Task;return new(Status,r.RequestId,++Epoch,Status==AuthoringAssistStatus.Completed?(InvalidBody?"{}":Body(r,Definition,Outline,PathCandidate)):null);}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    internal static string Body(AuthoringAssistRequest r,bool definition=false,bool outline=false,bool pathCandidate=false)
    {
        var target=r.Snapshot.Files.FirstOrDefault(f=>f.RelativePath=="chapter.tex")??r.Snapshot.Files[0];
        object? def=definition?new{fileId=target.FileId.ToString(),span=new{startByte=0,endByte=7}}:null;
        object[] candidates=r.Context==AuthoringAssistContext.Outline?[]:[new
        {
            id="item-1",kind=definition?"macro":"command",label=definition?"chapter":"section",detail="Synthetic boundary fixture, not Rust execution",
            insertText=definition?"\\chapter":"\\section{}",replaceSpan=new{startByte=r.CaretByte-Encoding.UTF8.GetByteCount((r.Context==AuthoringAssistContext.Command?"\\":"")+r.Query),endByte=r.CaretByte},
            caretOffsetUtf16=definition?8:9,requiresReview=false,origin=definition?"projectDeclaration":"builtin",definition=def
        }];
        if(pathCandidate)
        {
            var binary=r.Snapshot.Excluded.Single(f=>f.MediaKind=="binary");
            candidates=[new{id="item-1",kind="path",label=binary.RelativePath,detail="Captured binary path metadata only; contents/provisioning unverified",
                insertText=binary.RelativePath,replaceSpan=new{startByte=r.CaretByte-Encoding.UTF8.GetByteCount(r.Query),endByte=r.CaretByte},
                caretOffsetUtf16=binary.RelativePath.Length,requiresReview=false,origin="projectPath",definition=(object?)null}];
        }
        object[] headings=outline?[new{kind="heading",label="chapter",fileId=target.FileId.ToString(),span=new{startByte=0,endByte=7},parentIndex=(int?)null}]:[];
        return JsonSerializer.Serialize(new{protocolVersion=1,requestId=r.RequestId,status="completed",payload=new
        {
            snapshotId=r.Snapshot.Id.ToString(),rootFileId=r.RootFileId,fileId=r.FileId,caretByte=r.CaretByte,catalogVersion="1.0",complete=false,
            limitations=new[]{"sourceIntentOnly","macroExpansionUnsupported","runtimeResourcesUnverified"},candidates,outline=headings,loadedPackages=Array.Empty<object>()
        }});
    }
    private sealed class Editor(WorkspaceCoordinator workspace) : IAuthoringAssistEditor
    {
        public long Intent{get;set;}=42;public bool IsComposing{get;set;}
        public bool BeginRequest()=>true; // The real page cancels competing navigation before capture.
        internal int Reads,Applies,Navigations;
        internal int HoldReadAt=1;internal TaskCompletionSource<AuthoringAssistDom?>? Hold;
        internal TaskCompletionSource? NavigationHold;
        internal readonly TaskCompletionSource NavigationReached=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal AuthoringAssistEditorRead? LastRead;internal AuthoringAssistInsertion? LastInsertion;
        internal Action? BeforeApply;internal SourceRange? Selection;
        public Task<AuthoringAssistDom?> ReadAsync(AuthoringAssistEditorRead request)
        {
            Reads++;LastRead=request;
            if(Hold is not null && Reads==HoldReadAt)return Hold.Task;
            return Task.FromResult<AuthoringAssistDom?>(Dom(request));
        }
        internal AuthoringAssistDom Dom(AuthoringAssistEditorRead request)=>new(request.FileId,request.ExpectedTextareaText,
            Selection??new(request.ExpectedTextareaText.Length,request.ExpectedTextareaText.Length),request.Intent);
        public bool TryApply(AuthoringAssistInsertion insertion)
        {Applies++;LastInsertion=insertion;BeforeApply?.Invoke();return insertion.IsCurrent() && workspace.EditText(insertion.Before.File,insertion.Edit.Text);}
        public async Task<bool> NavigateAsync(AuthoringAssistNavigation navigation)
        {
            Navigations++;if(!navigation.IsCurrent())return false;
            workspace.SelectFile(navigation.File.FileId);NavigationReached.TrySetResult();
            if(NavigationHold is not null)await NavigationHold.Task;
            return navigation.IsCurrent();
        }
    }
    private static void Prepare(CompilationTestWorkspace f,string text=Text)
    {
        // Synthetic source fixture setup, including read-only cases. Not a user
        // edit or bypass exercised by the feature. Actual feature mutations are
        // exclusively checked through IAuthoringAssistEditor.TryApply above.
        var file=f.Workspace.ActiveFile!;file.RelativePath="main.tex";file.Content=text;file.DirtyContent=true;
        file.ImportedBytes=null; // Fresh fixture has no reviewed PendingTextBytes.
    }
    private static AuthoringAssistSession Session(CompilationTestWorkspace f,AnalysisSnapshotFactory factory,Provider provider,Editor editor)
    {var session=new AuthoringAssistSession(f.Workspace,f.Lock,factory,provider,editor);session.SetRoot(f.Main);return session;}

    [Fact] public void Suggest_never_mutates_and_explicit_insert_is_one_shot_with_unicode_CRLF_preserved()=>Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f);await using var factory=new AnalysisSnapshotFactory(f.Workspace);
        var p=new Provider();var editor=new Editor(f.Workspace);using var session=Session(f,factory,p,editor);string before=f.State();
        Assert.True(await session.SuggestAsync());Assert.Equal(before,f.State());Assert.Equal("se",p.Last!.Query);
        var report=Assert.IsType<AuthoringAssistReport>(session.Report);Assert.Equal(f.Main.ToString(),report.Request.RootFileId);
        Assert.True(await session.InsertAsync(report,"item-1"));Assert.Equal("Україна😀е\u0301\r\n\\section{}",f.Workspace.ActiveFile!.Content);
        Assert.Equal(1,editor.Applies);Assert.False(editor.LastInsertion!.IsCurrent());
        Assert.False(await session.InsertAsync(report,"item-1"));Assert.Equal(1,editor.Applies);
    });
    [Theory][InlineData("capture")][InlineData("DOM")][InlineData("reply")][InlineData("reread")]
    public void Cancel_retains_actual_capture_DOM_provider_and_reread_admission(string stage)=>Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f);await using var factory=new AnalysisSnapshotFactory(f.Workspace);
        var provider=new Provider();var editor=new Editor(f.Workspace);using var session=Session(f,factory,provider,editor);
        CompilationTestWorkspace.ReadBarrier? capture=null;
        if(stage=="capture")capture=f.Hold(f.Workspace.Files.Single(x=>x.RelativePath=="chapter.tex").Id);
        if(stage is "DOM" or "reread"){editor.Hold=new(TaskCreationOptions.RunContinuationsAsynchronously);editor.HoldReadAt=stage=="reread"?2:1;}
        if(stage=="reply")provider.Hold=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task=session.SuggestAsync();
        if(capture is not null)await capture.WaitReached();
        if(stage=="reply")await provider.Reached.Task.WaitAsync(Bound);
        session.Cancel();Assert.True(session.Busy);Assert.False(task.IsCompleted);Assert.False(await session.SuggestAsync());
        Assert.Null(session.Report);Assert.Equal(0,editor.Applies);
        if(capture is not null)capture.Release.TrySetResult(f.Repo.Content[capture.Hash]);
        if(editor.Hold is not null)editor.Hold.TrySetResult(editor.Dom(editor.LastRead!));
        provider.Hold?.TrySetResult();Assert.False(await task.WaitAsync(Bound));Assert.False(session.Busy);Assert.Null(session.Report);
        Assert.Equal(stage is "reply" or "reread"?1:0,provider.Calls);
    });
    [Theory][InlineData("root-ABA")][InlineData("file-ABA")][InlineData("text-ABA")][InlineData("context-ABA")]
    [InlineData("caret")][InlineData("head")][InlineData("dispose")][InlineData("remount")][InlineData("session-ABA")][InlineData("project-ABA")][InlineData("restore")]
    public void Held_reply_cannot_publish_after_identity_ABA_caret_save_or_disposal(string change)=>Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f);await using var factory=new AnalysisSnapshotFactory(f.Workspace);
        var provider=new Provider{Hold=new(TaskCreationOptions.RunContinuationsAsynchronously)};var editor=new Editor(f.Workspace);
        using var session=Session(f,factory,provider,editor);var task=session.SuggestAsync();await provider.Reached.Task.WaitAsync(Bound);
        var other=f.Workspace.Files.Single(x=>x.RelativePath=="chapter.tex").Id;
        switch(change)
        {
            case "root-ABA":session.SetRoot(other);session.SetRoot(f.Main);break;
            case "file-ABA":f.Workspace.SelectFile(other);editor.Intent++;session.Invalidate();f.Workspace.SelectFile(f.Main);editor.Intent++;session.Invalidate();break;
            case "text-ABA":f.Workspace.EditText(f.Main,"different");f.Workspace.EditText(f.Main,Text);break;
            case "context-ABA":session.SetContext(AuthoringAssistContext.Outline);session.SetContext(AuthoringAssistContext.Command);break;
            case "caret":editor.Intent++;break;
            case "head":await f.Workspace.SaveAsync(default);break;
            case "session-ABA":
                var project=f.Workspace.Project!.Id;await f.Workspace.CloseAsync(default);await f.Workspace.OpenAsync(project,default);break;
            case "project-ABA":
                var original=f.Workspace.Project!.Id;await f.Workspace.CloseAsync(default);
                var otherProject=new Project(ProjectId.New(),"B",null);f.Repo.Projects[otherProject.Id]=(otherProject,null);
                await f.Workspace.OpenAsync(otherProject.Id,default);await f.Workspace.CloseAsync(default);await f.Workspace.OpenAsync(original,default);break;
            case "restore":
                var historical=f.Repo.Revision!.Id;var preview=await f.Workspace.CaptureRestorePreviewAsync(historical,default);
                await f.Workspace.AcceptRestoreAsync(preview,default);break;
            case "dispose":session.Dispose();break;
            case "remount":session.Dispose();using(var next=Session(f,factory,provider,editor))Assert.Null(next.Report);break;
        }
        string before=f.State();provider.Hold.TrySetResult();Assert.False(await task.WaitAsync(Bound));Assert.Null(session.Report);Assert.Equal(before,f.State());
    });
    [Theory][InlineData("DOM")][InlineData("writer")][InlineData("recovery-intent")][InlineData("caret")][InlineData("context")]
    public void Insert_reread_refuses_lost_writer_changed_DOM_or_late_context_without_mutating(string change)=>Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f);await using var factory=new AnalysisSnapshotFactory(f.Workspace);
        var provider=new Provider();var editor=new Editor(f.Workspace);using var session=Session(f,factory,provider,editor);
        Assert.True(await session.SuggestAsync());var report=session.Report!;
        editor.Hold=new(TaskCreationOptions.RunContinuationsAsynchronously);editor.HoldReadAt=editor.Reads+1;
        var task=session.InsertAsync(report,"item-1");Assert.False(task.IsCompleted);
        if(change=="writer")f.Lock.AllowAcquire=false;
        if(change=="caret")editor.Intent++;
        if(change=="context")session.SetContext(AuthoringAssistContext.Outline);
        if(change=="recovery-intent")session.Invalidate();
        var dom=editor.Dom(editor.LastRead!);if(change=="DOM")dom=dom with{Value="different DOM"};
        string before=f.State();editor.Hold.TrySetResult(dom);Assert.False(await task.WaitAsync(Bound));Assert.Equal(before,f.State());Assert.Equal(0,editor.Applies);
    });
    [Fact] public void Lost_writer_between_emit_and_root_ordinary_edit_guard_cannot_mutate()=>Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f);await using var factory=new AnalysisSnapshotFactory(f.Workspace);
        var editor=new Editor(f.Workspace);using var session=Session(f,factory,new Provider(),editor);
        Assert.True(await session.SuggestAsync());var report=session.Report!;string before=f.State();editor.BeforeApply=()=>f.Lock.AllowAcquire=false;
        Assert.False(await session.InsertAsync(report,"item-1"));Assert.Equal(before,f.State());Assert.False(editor.LastInsertion!.IsCurrent());
    });
    [Fact] public void Readonly_suggestions_and_owned_cross_file_definition_navigation_work_but_insert_refuses()=>Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open(readOnly:true);Prepare(f,"\\cha");await using var factory=new AnalysisSnapshotFactory(f.Workspace);
        var provider=new Provider{Definition=true};var editor=new Editor(f.Workspace);using var session=Session(f,factory,provider,editor);
        string text=f.Workspace.ActiveFile!.Content;Assert.True(await session.SuggestAsync());var report=session.Report!;
        Assert.False(await session.InsertAsync(report,"item-1"));Assert.Equal(text,f.Workspace.ActiveFile!.Content);Assert.Equal(0,editor.Applies);
        Assert.True(await session.SuggestAsync());report=session.Report!;Assert.True(await session.NavigateAsync(report,"item-1"));
        Assert.Equal("chapter.tex",f.Workspace.ActiveFile!.RelativePath);Assert.Equal(1,editor.Navigations);Assert.Null(session.Report);
    });
    [Fact] public void Late_navigation_callback_cannot_reuse_invalidated_authority()=>Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f,"\\cha");await using var factory=new AnalysisSnapshotFactory(f.Workspace);
        var editor=new Editor(f.Workspace){NavigationHold=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        using var session=Session(f,factory,new Provider{Definition=true},editor);Assert.True(await session.SuggestAsync());
        var task=session.NavigateAsync(session.Report!,"item-1");await editor.NavigationReached.Task.WaitAsync(Bound);
        session.Invalidate();Assert.True(session.Busy);Assert.False(await session.SuggestAsync());
        editor.NavigationHold.TrySetResult();Assert.False(await task.WaitAsync(Bound));Assert.False(session.Busy);
    });
    [Fact] public void IME_and_unobserved_query_refuse_without_provider_or_mutation()=>Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Prepare(f);await using var factory=new AnalysisSnapshotFactory(f.Workspace);
        var provider=new Provider();var editor=new Editor(f.Workspace){IsComposing=true};using var session=Session(f,factory,provider,editor);
        Assert.False(await session.SuggestAsync());editor.IsComposing=false;Assert.False(await session.SuggestAsync("not-observed"));Assert.Equal(0,provider.Calls);
    });
}
