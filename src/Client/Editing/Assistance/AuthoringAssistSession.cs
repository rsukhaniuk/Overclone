using System.Text;
using Client.Analysis;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Patching;
using Client.Workspace.Transfer;

namespace Client.Editing.Assistance;

/// <summary>Per-mount authoring authority. Owns no coordinator/storage/parser.
/// All callbacks return ACTUAL tasks; admission survives cancellation until those
/// tasks settle. Root must invalidate every editor/caret/file/IME intent, including ABA.</summary>
public sealed class AuthoringAssistSession : IDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly IWorkspaceLocks _locks;
    private readonly AnalysisSnapshotFactory _factory;
    private readonly IAuthoringAssistProvider _provider;
    private readonly IAuthoringAssistEditor _editor;
    private FileId? _root;
    private long _version;
    private bool _disposed;
    private CancellationTokenSource? _operation;
    private Lease? _lease;
    public AuthoringAssistSession(WorkspaceCoordinator workspace,IWorkspaceLocks locks,AnalysisSnapshotFactory factory,
        IAuthoringAssistProvider provider,IAuthoringAssistEditor editor)
    {
        _workspace=workspace;_locks=locks;_factory=factory;_provider=provider;_editor=editor;
        _workspace.WorkspaceChanged+=Invalidate;
    }
    public event Action? Changed;
    public AuthoringAssistContext Context {get;private set;}=AuthoringAssistContext.Command;
    public AuthoringAssistReport? Report {get;private set;}
    public bool Busy=>_operation is not null;
    public string Notice {get;private set;}="Explicit local source observations. Nothing is inserted automatically.";
    public void SetRoot(FileId? root){if(_root==root)return;Invalidate();_root=root;}
    public void SetContext(AuthoringAssistContext context)
    {if(!Enum.IsDefined(context))throw new ArgumentException("Unknown context.");if(Context==context)return;Invalidate();Context=context;}
    public void Invalidate()
    {
        _version++;Report=null;_lease=null;_operation?.Cancel();
        if(!_disposed){Notice="Source, selection or context changed; suggest again.";Changed?.Invoke();}
    }
    public void Cancel(){Invalidate();if(!_disposed){Notice=Busy?"Cancelled; waiting for local work to settle.":"Cancelled.";Changed?.Invoke();}}
    private CancellationTokenSource? Begin(CancellationToken ct)
    {
        if(_disposed || Busy)return null;
        var stop=CancellationTokenSource.CreateLinkedTokenSource(ct);_operation=stop;return stop;
    }
    private void End(CancellationTokenSource stop)
    {
        if(ReferenceEquals(_operation,stop))_operation=null;
        // An accepted ordinary edit invalidates its own old-source lease. Do
        // not relabel that already-completed explicit mutation as cancellation.
        if(!_disposed && stop.IsCancellationRequested && Report is null && Notice!="Suggestion inserted through the ordinary editor action.")Notice="Source request cancelled or invalidated; suggest again.";
        else if(!_disposed && Report is null && Notice=="Reading the captured local source…")Notice="Source or selection changed; suggest again.";
        stop.Dispose();if(!_disposed)Changed?.Invoke();
    }
    public async Task<bool> SuggestAsync(string? query=null,CancellationToken cancellationToken=default)
    {
        if(_disposed || Busy || cancellationToken.IsCancellationRequested || !_editor.BeginRequest())return false;
        var stop=Begin(cancellationToken);if(stop is null)return false;
        Report=null;_lease=null;
        try
        {
            var lease=Capture();if(lease is null){Notice="Select a current .tex root and text editor.";return false;}
            _lease=lease;Notice="Reading the captured local source…";Changed?.Invoke();
            if(!Current(lease,stop.Token,false))return false;
            var selection=new AnalysisSelection(new(AnalysisCatalog.ReferenceId,AnalysisCatalog.ReferenceVersion),null,
                new AnalysisReferenceConfigurationDto(lease.Root.Id.ToString()));
            // None is essential: factory uses WaitAsync(ct). A cancellation proxy
            // must not release admission while its underlying storage read runs.
            var capture=await _factory.CaptureSelectionAsync(selection,CancellationToken.None);
            if(!Current(lease,stop.Token,false))return false;
            if(capture is not CaptureResult.Captured captured){Notice="Local source capture is unavailable or over budget.";return false;}
            var snapshot=captured.Snapshot;
            if(snapshot.Excluded.Length!=lease.BinaryMetadata.Count || snapshot.Excluded.Any(f=>f.MediaKind!="binary"
                || !lease.BinaryMetadata.TryGetValue(f.FileId,out var meta) || meta.Path!=f.RelativePath || meta.Media!=f.MediaKind))return false;
            if(snapshot.ProjectId!=lease.Project || snapshot.SessionId!=lease.Session || snapshot.WorkingGeneration!=lease.Generation
                || snapshot.BaseHeadRevisionId!=lease.Head || snapshot.LocalEpoch!=lease.Epoch)return false;
            var source=snapshot.Files.SingleOrDefault(f=>f.FileId==lease.File.Id);
            if(source is null || !TextContent.TryDecode(source.Bytes.ToArray(),out var text,out var bom)
                || SourceTools.TextareaText(text)!=SourceTools.TextareaText(lease.Text) || bom!=lease.HasBom)return false;
            var dom=await _editor.ReadAsync(new(lease.File.Id,SourceTools.TextareaText(lease.Text),lease.Intent));
            if(!Current(lease,stop.Token,false))return false;
            if(!Dom(lease,dom) || !SourceTools.TryFromTextarea(text,dom!.Selection,out var caret)
                || caret.Start!=caret.End){Notice="A current collapsed editor caret is required.";return false;}
            // Wire offsets use the factory's exact captured bytes. For edited
            // source the existing factory uses ordinary LF encoding; the live
            // full-source preimage is retained separately, never rewritten here.
            query??=Prefix(text,caret.End,lease.Context);
            if(!AuthoringAssistJson.Text(query,1024) || caret.End<query.Length
                || !SourceTools.ValidRange(text,new(caret.End-query.Length,caret.End))
                || !text.AsSpan(caret.End-query.Length,query.Length).SequenceEqual(query.AsSpan()))
            {Notice="Query must be the exact source prefix ending at this caret.";return false;}
            long caretByte=Encoding.UTF8.GetByteCount(text.AsSpan(0,caret.End))+(bom?3:0);
            var request=new AuthoringAssistRequest(snapshot,Guid.NewGuid().ToString("N"),lease.Root.Id.ToString(),lease.File.Id.ToString(),caretByte,lease.Context,query);
            lease.Dom=dom;lease.Request=request;
            if(!Current(lease,stop.Token,false))return false;
            var reply=await _provider.EvaluateAsync(request,stop.Token);
            if(!Current(lease,stop.Token,false))return false;
            if(reply.Status!=AuthoringAssistStatus.Completed || reply.RequestId!=request.RequestId || !AuthoringAssistJson.Epoch(reply.WorkerEpoch))
            {Notice=reply.Status==AuthoringAssistStatus.TimedOut?"Local suggestions reached the time limit.":"Local suggestions are unavailable; editing and saving remain usable.";return false;}
            var parsed=AuthoringAssistJson.Parse(reply.ResponseJson,AuthoringAssistJson.Expect(request));
            if(parsed.Result is not { } result){Notice="Local source observations were refused (invalid response or request limit).";return false;}
            // The user can move the caret without editing during worker work.
            var freshDom=await _editor.ReadAsync(new(lease.File.Id,dom!.Value,lease.Intent));
            if(!Current(lease,stop.Token,false))return false;
            if(!Dom(lease,freshDom) || freshDom!=dom){Notice="The editor caret changed; suggest again.";return false;}
            Report=new(request,result,reply.WorkerEpoch);
            Notice=result.Complete?"Literal source observations only; package resources and macro execution are not verified.":
                "Partial literal source observations; review the limitations. This is not compiler or compliance evidence.";
            Changed?.Invoke();return true;
        }
        catch(Exception e)when(e is OperationCanceledException or ObjectDisposedException or Microsoft.JSInterop.JSException or System.Text.Json.JsonException)
        {if(!_disposed && !stop.IsCancellationRequested)Notice="Local assistance is unavailable; no changes were made.";return false;}
        finally{End(stop);}
    }
    public bool IsCurrent(AuthoringAssistReport report)=>ReferenceEquals(Report,report) && _lease is { } lease && Current(lease,CancellationToken.None,false);
    public bool CanInsert(AuthoringAssistReport report)=>IsCurrent(report) && _lease is { } lease && Current(lease,CancellationToken.None,true)
        && report.Request.Context is not (AuthoringAssistContext.Definition or AuthoringAssistContext.Outline);
    public async Task<bool> InsertAsync(AuthoringAssistReport report,string candidateId)
    {
        if(!IsCurrent(report) || report.Request.Context is AuthoringAssistContext.Definition or AuthoringAssistContext.Outline)return false;
        var candidate=report.Result.Candidates.SingleOrDefault(c=>c.Id==candidateId);
        if(candidate is null || candidate.RequiresReview || _lease is not { } lease)return false;
        var stop=Begin(default);if(stop is null)return false;
        try
        {
            if(!Current(lease,stop.Token,true))return false;
            var dom=await _editor.ReadAsync(new(lease.File.Id,lease.Dom!.Value,lease.Intent));
            if(!Current(lease,stop.Token,true) || !Dom(lease,dom) || dom!=lease.Dom)return false;
            var captured=report.Request.Snapshot.Files.Single(f=>f.FileId==lease.File.Id);
            if(SpanMapping.Map(captured,captured.FileId.ToString(),candidate.ReplaceSpan.StartByte,candidate.ReplaceSpan.EndByte) is not SpanMappingResult.Exact map)return false;
            if(!SourceTools.TryFromTextarea(lease.Text,new(map.TextareaStart,map.TextareaEnd),out var range))return false;
            int start=range.Start,end=range.End;
            long budget=ByteBudget(report.Request.Snapshot,lease);
            long projected=Encoding.UTF8.GetByteCount(lease.Text)-Encoding.UTF8.GetByteCount(lease.Text.AsSpan(start,end-start))
                +Encoding.UTF8.GetByteCount(candidate.InsertText);
            if(projected>budget || budget<0)return false;
            string after=lease.Text[..start]+candidate.InsertText+lease.Text[end..];
            int caret=start+candidate.CaretOffsetUtf16;
            if(!SourceTools.ValidText(after,out _) || !SourceTools.ValidRange(after,new(caret,caret)))return false;
            var before=new SourceToolSnapshot(lease.Session,lease.Generation,lease.File.Id,lease.Text,lease.Intent);
            bool emitted=false;
            var insertion=new AuthoringAssistInsertion(before,new(after,new(caret,caret)),()=>emitted && Current(lease,stop.Token,true));
            if(!Current(lease,stop.Token,true))return false;
            Report=null;emitted=true; // One-shot even if the ordinary root edit refuses.
            bool applied;
            try{applied=_editor.TryApply(insertion);}finally{emitted=false;}
            Notice=applied?"Suggestion inserted through the ordinary editor action.":"Insertion refused; no further action was applied.";
            return applied;
        }
        finally{Report=null;_lease=null;End(stop);}
    }
    public async Task<bool> NavigateAsync(AuthoringAssistReport report,string? candidateId=null,int? outlineIndex=null)
    {
        if(!IsCurrent(report) || _lease is not { } lease || (candidateId is null)==(outlineIndex is null))return false;
        AuthoringAssistDefinition? target=null;
        if(candidateId is not null)target=report.Result.Candidates.SingleOrDefault(c=>c.Id==candidateId)?.Definition;
        if(outlineIndex is { } index && index>=0 && index<report.Result.Outline.Length)
        {var o=report.Result.Outline[index];target=new(o.FileId,o.Span);}
        if(target is null)return false;
        var file=report.Request.Snapshot.Files.SingleOrDefault(f=>f.FileId.ToString()==target.FileId);
        if(file is null)return false;
        var map=SpanMapping.Map(file,target.FileId,target.Span.StartByte,target.Span.EndByte);
        if(map is SpanMappingResult.InvalidSpan)return false;
        var stop=Begin(default);if(stop is null)return false;
        try
        {
            if(!Current(lease,stop.Token,false))return false;
            var dom=await _editor.ReadAsync(new(lease.File.Id,lease.Dom!.Value,lease.Intent));
            if(!Current(lease,stop.Token,false) || dom!=lease.Dom)return false;
            // The root may make one owned read-only switch to this captured target
            // without advancing its source intent until navigation settles. All
            // unrelated editor/caret/file events still invalidate this lease.
            bool NavigationCurrent()=>Core(lease,stop.Token,false) && TargetCurrent(file,lease)
                && (_workspace.ActiveFile==lease.File || _workspace.ActiveFile?.Id==file.FileId);
            var navigation=new AuthoringAssistNavigation(file,target.Span,map,NavigationCurrent);
            Report=null; // Old report cannot insert into the newly selected file.
            if(!NavigationCurrent())return false;
            bool selected=await _editor.NavigateAsync(navigation);
            if(!NavigationCurrent())return false;
            Notice=selected?"Captured source location selected (read-only action).":"Source location could not be selected.";return selected;
        }
        finally{Report=null;_lease=null;End(stop);}
    }
    private bool TargetCurrent(CapturedFile target,Lease lease)
    {
        var live=_workspace.Files.SingleOrDefault(f=>f.Id==target.FileId && !f.Deleted);
        return lease.Targets.TryGetValue(target.FileId,out var before) && ReferenceEquals(live,before.File)
            && live is {MediaKind:"text"} && live.RelativePath==before.Path && live.Content==before.Text && live.HasBom==before.HasBom
            && live.RelativePath==target.RelativePath && live.HasBom==target.HasBom
            && TextContent.TryDecode(target.Bytes.ToArray(),out var text,out _)
            && SourceTools.TextareaText(text)==SourceTools.TextareaText(before.Text);
    }
    private Lease? Capture()
    {
        if(_disposed || _workspace.Files.Count(f=>!f.Deleted)>AnalysisLimits.DMaxFiles || _editor.IsComposing || _workspace.Project is not { } project || _workspace.ActiveFile is not {Deleted:false,MediaKind:"text"} file
            || _root is not { } rootId || _workspace.Files.SingleOrDefault(f=>f.Id==rootId && !f.Deleted) is not { } root
            || !ReferenceChecks.CanBeRoot(root) || !SourceTools.ValidText(file.Content,out _))return null;
        return new(project.Id,_workspace.SessionId,_workspace.WorkingGeneration,_workspace.CurrentRevision?.Id,_factory.CurrentWorkspaceEpoch,
            _version,_editor.Intent,_workspace.LockId,_workspace.Recovery,file,root,file.RelativePath,root.RelativePath,file.Content,Context)
        {
            Targets=_workspace.Files.Where(f=>!f.Deleted && f.MediaKind=="text").ToDictionary(f=>f.Id,f=>new TargetPreimage(f,f.RelativePath,f.Content,f.HasBom)),
            BinaryMetadata=_workspace.Files.Where(f=>!f.Deleted && f.MediaKind=="binary").ToDictionary(f=>f.Id,f=>new MetadataPreimage(f,f.RelativePath,f.MediaKind))
        };
    }
    private bool Current(Lease lease,CancellationToken ct,bool editing)=>Core(lease,ct,editing)
        && ReferenceEquals(_workspace.ActiveFile,lease.File) && !lease.File.Deleted && lease.File.MediaKind=="text"
        && lease.File.RelativePath==lease.Path && lease.File.Content==lease.Text && lease.File.HasBom==lease.HasBom;
    private bool Core(Lease lease,CancellationToken ct,bool editing)=>!_disposed && !ct.IsCancellationRequested && ReferenceEquals(_lease,lease)
        && lease.Version==_version && _root==lease.Root.Id && Context==lease.Context && !_editor.IsComposing && _editor.Intent==lease.Intent
        && _workspace.Project?.Id==lease.Project && _workspace.SessionId==lease.Session && _workspace.WorkingGeneration==lease.Generation
        && _workspace.CurrentRevision?.Id==lease.Head && _factory.CurrentWorkspaceEpoch==lease.Epoch
        && _workspace.LockId==lease.Lock && ReferenceEquals(_workspace.Recovery,lease.Recovery)
        && _workspace.Files.Any(f=>ReferenceEquals(f,lease.Root)) && !lease.Root.Deleted && ReferenceChecks.CanBeRoot(lease.Root)
        && lease.Root.RelativePath==lease.RootPath && lease.Root.Content==lease.RootText && lease.Root.HasBom==lease.RootHasBom && BinaryMetadataCurrent(lease)
        && (!editing || !_workspace.IsReadOnly && _workspace.Recovery is null && Owned(lease));
    private bool BinaryMetadataCurrent(Lease lease)
    {
        int count=0;
        foreach(var file in _workspace.Files.Where(f=>!f.Deleted && f.MediaKind=="binary"))
        {
            count++;
            if(!lease.BinaryMetadata.TryGetValue(file.Id,out var before) || !ReferenceEquals(file,before.File)
                || file.RelativePath!=before.Path || file.MediaKind!=before.Media)return false;
        }
        return count==lease.BinaryMetadata.Count;
    }
    private bool Owned(Lease lease)
    {try{return lease.Lock is { } id && _locks.IsOwnedLocally(id,"oop-project:"+lease.Project);}catch(Exception e)when(e is not OutOfMemoryException){return false;}}
    private static bool Dom(Lease lease,AuthoringAssistDom? dom)=>dom is not null && dom.FileId==lease.File.Id && dom.Intent==lease.Intent
        && dom.Value==SourceTools.TextareaText(lease.Text) && SourceTools.ValidRange(dom.Value,dom.Selection);
    private long ByteBudget(AnalysisSnapshot snapshot,Lease lease)
    {
        long other=snapshot.Files.Where(f=>f.FileId!=lease.File.Id).Sum(f=>f.ByteLength);
        foreach(var file in _workspace.Files.Where(f=>!f.Deleted && f.MediaKind=="binary"))
        {long size=file.BinaryContent?.LongLength??file.DurableLength;if(size<0)return -1;other+=size;if(other>WorkspaceTextLimits.ProjectBytes)return -1;}
        int bom=lease.File.HasBom?3:0;
        return Math.Min(WorkspaceTextLimits.FileBytes-bom,WorkspaceTextLimits.ProjectBytes-other-bom);
    }
    private static string Prefix(string text,int caret,AuthoringAssistContext context)
    {
        if(context==AuthoringAssistContext.Outline)return "";
        int start=caret;
        while(start>0 && caret-start<=1024)
        {
            char c=text[start-1];
            if(context==AuthoringAssistContext.Command ? !char.IsAsciiLetter(c) : c is '{' or '}' or ',' or '\\' or '\r' or '\n' or ' ' or '\t')break;
            start--;
        }
        if(start>0 && start<text.Length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start-1]))start--;
        return text[start..caret];
    }
    public void Dispose()
    {if(_disposed)return;_disposed=true;_workspace.WorkspaceChanged-=Invalidate;Invalidate();}
    private sealed record MetadataPreimage(WorkingFile File,string Path,string Media);
    private sealed record TargetPreimage(WorkingFile File,string Path,string Text,bool HasBom);
    private sealed class Lease(ProjectId project,Guid session,long generation,RevisionId? head,long epoch,long version,long intent,string? lockId,
        object? recovery,WorkingFile file,WorkingFile root,string path,string rootPath,string text,AuthoringAssistContext context)
    {
        internal ProjectId Project {get;}=project;internal Guid Session {get;}=session;internal long Generation {get;}=generation;
        internal RevisionId? Head {get;}=head;internal long Epoch {get;}=epoch;internal long Version {get;}=version;internal long Intent {get;}=intent;
        internal string? Lock {get;}=lockId;internal object? Recovery {get;}=recovery;internal WorkingFile File {get;}=file;internal WorkingFile Root {get;}=root;
        internal string Path {get;}=path;internal string RootPath {get;}=rootPath;internal string Text {get;}=text;internal AuthoringAssistContext Context {get;}=context;
        internal bool HasBom {get;}=file.HasBom;internal string RootText {get;}=root.Content;internal bool RootHasBom {get;}=root.HasBom;
        internal Dictionary<FileId,TargetPreimage> Targets {get;init;}=[];
        internal Dictionary<FileId,MetadataPreimage> BinaryMetadata {get;init;}=[];
        internal AuthoringAssistDom? Dom {get;set;}internal AuthoringAssistRequest? Request {get;set;}
    }
}
