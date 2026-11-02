using Client.Analysis;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Patching;

namespace Client.Review;

/// <summary>One preview and one accepted recovery record per workspace session.
/// No provider, UI, storage schema, rule engine or second workspace mutation gate.
/// The small monitor protects review bookkeeping only; it is never held over I/O.</summary>
public sealed class ChangeReviewService : IChangeReviewService
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly AnalysisCoordinator _analysis;
    private readonly SourceNavigator _navigator;
    private readonly AnalysisSnapshotFactory _capture;
    private readonly object _sync=new();
    private ReviewPreview? _preview;
    private Accepted? _accepted;
    private long _previewIntent,_operationIntent,_notificationVersion;
    private CancellationTokenSource? _previewCts,_operationCts;
    private bool _busy,_disposed;
    private ReviewStatus _status;
    private ReviewFailure? _notice;
    public event Action<ReviewServiceState>? Changed;

    private sealed class Accepted(PreparedTextBatch batch,TextChangeSet inverse)
    {
        internal readonly Guid Id=Guid.NewGuid();
        internal readonly PreparedTextBatch Batch=batch;
        internal readonly TextChangeSet Inverse=inverse;
        internal bool Committed,Undone,Persisted;
        internal RevisionId? LegBaseHead=batch.Identity.ExpectedHead;
    }

    public ChangeReviewService(WorkspaceCoordinator workspace,AnalysisCoordinator analysis,SourceNavigator navigator)
    {
        _workspace=workspace;_analysis=analysis;_navigator=navigator;
        _capture=new(workspace);
        workspace.WorkspaceChanged+=WorkspaceChanged;
        workspace.RevisionCommitted+=Saved;
        analysis.Changed+=AnalysisChanged;
    }

    public ReviewServiceState State { get { lock(_sync){RefreshSession();RefreshPersistence();return Snapshot();} } }
    private ReviewServiceState Snapshot()
    {
        var action=_accepted;
        var status=_disposed?ReviewStatus.Disposed:_preview is not null&&!_busy?ReviewStatus.Ready:_status;
        return new(status,_preview,action is null?null:new(action.Id,action.Batch.Identity.SessionId,action.Undone,action.Persisted,!_busy&&!_disposed&&!action.Undone,action.Batch),_busy,_workspace.IsDirty,_notice);
    }
    private void Notify()
    {
        Action<ReviewServiceState>[] handlers;ReviewServiceState state;long version;
        lock(_sync){version=++_notificationVersion;state=Snapshot();handlers=Changed?.GetInvocationList().Cast<Action<ReviewServiceState>>().ToArray()??[];}
        foreach(var handler in handlers){lock(_sync){if(version!=_notificationVersion)break;}try{handler(state);}catch{/* isolate UI observer */}}
    }
    private static ReviewFailure Error(ReviewCode code)=>new(code,code switch{
        ReviewCode.PendingSave=>"Persist or recover the previous reviewed action before accepting another.",
        ReviewCode.Conflict=>"The affected file or durable head no longer matches. Inspect the action's applied/save state; recovery is retained.",
        ReviewCode.Cancelled=>"The operation was cancelled. Any already-applied edits and recovery data are retained.",
        ReviewCode.Stale=>"The preview or workspace is no longer current; request a new preview.",
        ReviewCode.UnknownFix=>"This descriptor is not an eligible canonical F1 in the current report.",
        ReviewCode.Busy=>"A review operation is already in progress.",
        ReviewCode.Disposed=>"The review service is disposed.",
        _=>"The review operation could not be completed safely.",
    });
    private ReviewResult Failure(ReviewCode code){lock(_sync){_notice=Error(code);return new(_disposed?ReviewStatus.Disposed:_status,Failure:_notice);} }
    private bool SameSession(Accepted action)=>_workspace.Project?.Id==action.Batch.Identity.ProjectId&&_workspace.SessionId==action.Batch.Identity.SessionId;
    private void RefreshSession()
    {
        if(_accepted is{} action&&!SameSession(action))_accepted=null;
        if(_preview is{} p&&!BaseCurrent(p.Binding))_preview=null;
        if(_accepted is null&&_preview is null&&!_busy)_status=ReviewStatus.Idle;
    }
    private static bool InRevision(Accepted action,ProjectRevision? revision)
        =>revision is not null&&revision.Id!=action.LegBaseHead&&revision.ProjectId==action.Batch.Identity.ProjectId&&action.Batch.Files.All(f=>{
            var image=action.Undone?f.Before:f.After;var stored=revision.FindFile(f.FileId);
            return stored is not null&&stored.RelativePath==f.RelativePath&&stored.MediaKind=="text"&&stored.ContentHash==image.Hash&&stored.ByteLength==image.Length&&stored.HasBom==image.HasBom;
        });
    private void RefreshPersistence()
    {
        if(_accepted is not{} a)return;
        a.Persisted|=InRevision(a,_workspace.CurrentRevision);
        if(a.Persisted&&!_busy)_status=a.Undone?ReviewStatus.UndoSaved:ReviewStatus.AppliedSaved;
    }
    private void Saved(WorkspaceSaveReceipt receipt)
    {
        lock(_sync){if(_disposed)return;RefreshSession();if(_accepted is{} a&&receipt.SessionId==a.Batch.Identity.SessionId&&InRevision(a,receipt.Revision)){a.Persisted=true;_status=a.Undone?ReviewStatus.UndoSaved:ReviewStatus.AppliedSaved;}}
        Notify();
    }
    private void InvalidatePreview()
    {
        _previewIntent++;_previewCts?.Cancel();_preview=null;
        if(!_busy&&_accepted is null)_status=ReviewStatus.Idle;
    }
    private void WorkspaceChanged(){lock(_sync){if(_disposed)return;InvalidatePreview();RefreshSession();}Notify();}
    private void AnalysisChanged(AnalysisState _){lock(_sync){if(_disposed)return;InvalidatePreview();}Notify();}
    private ReviewBinding Binding(AnalysisReport? report)=>new(_workspace.CurrentTextPatchIdentity!,_analysis.CurrentWorkspaceEpoch,_analysis.CurrentSelectionVersion,_analysis.CurrentSelection,
        report?.Snapshot.Id,report?.RequestId,report?.WorkerEpoch);
    private bool BaseCurrent(ReviewBinding b)=>_workspace.CurrentTextPatchIdentity==b.Workspace&&_analysis.CurrentWorkspaceEpoch==b.WorkspaceEpoch
        &&_analysis.CurrentSelectionVersion==b.SelectionVersion&&_analysis.CurrentSelection==b.Selection;
    private bool Authorized(ReviewPreview p)=>BaseCurrent(p.Binding)&&(p.Report is null||_navigator.IsStillCurrent(p.Report)
        &&p.Report.Result.Knu is{} k&&k.FixDescriptors.Any(f=>ReferenceEquals(f,p.Fix)));

    public Task<ReviewResult> PreviewAsync(TextChangeSet changeSet,CancellationToken ct)
        => changeSet?.Origin==TextPatchOrigin.Rule?Task.FromResult(Failure(ReviewCode.UnknownFix)):PreviewCore(changeSet!,null,null,null,ct);

    public async Task<ReviewResult> PreviewSubsetAsync(Guid expectedPreviewId,ReviewSubsetSelection selection,CancellationToken ct)
    {
        ReviewPreview original;TextChangeSet candidate;
        lock(_sync)
        {
            RefreshSession();RefreshPersistence();
            if(_disposed)return Failure(ReviewCode.Disposed);if(_busy)return Failure(ReviewCode.Busy);
            if(ct.IsCancellationRequested)return Failure(ReviewCode.Cancelled);
            if(_workspace.IsReadOnly||_workspace.Recovery is not null)return Failure(ReviewCode.ReadOnly);
            if(_preview is not{} p||p.Id!=expectedPreviewId||selection is null||selection.ProposalId!=p.Proposal.Id)return Failure(ReviewCode.Stale);
            if(!Authorized(p))return Failure(ReviewCode.Stale);
            if(!_workspace.IsCurrentTextPatchWriter(p.Binding.Workspace))return Failure(ReviewCode.ReadOnly);
            // Even a full Rule selection must use PreviewFixAsync's canonical
            // descriptor/report path. Never turn it into generic User/Ai/Rule.
            if(p.Proposal.Origin==TextPatchOrigin.Rule)return Failure(ReviewCode.UnknownFix);
            var creation=selection.CreateSubset(p.Proposal);
            if(creation.ChangeSet is not{} set)return Failure(ReviewCode.InvalidInput);
            original=p;candidate=set;
        }
        var result=await PreviewCore(candidate,original.Report,original.Fix,original.Citation,ct,original);
        // PreviewCore's final idle notification is a reentrancy boundary too.
        // Never return an old Ready result after an observer replaces/cancels it.
        lock(_sync)
        {
            if(result.Preview is{} preview&&(_disposed||ct.IsCancellationRequested||!ReferenceEquals(_preview,preview)||!Authorized(preview)
                ||!_workspace.IsCurrentTextPatchWriter(preview.Binding.Workspace)))
                return new(_disposed?ReviewStatus.Disposed:_status,Failure:Error(_disposed?ReviewCode.Disposed:ct.IsCancellationRequested?ReviewCode.Cancelled:ReviewCode.Stale));
        }
        return result;
    }
    public Task<ReviewResult> PreviewFixAsync(string descriptorId,CancellationToken ct)
    {
        lock(_sync){if(_disposed)return Task.FromResult(Failure(ReviewCode.Disposed));if(_busy)return Task.FromResult(Failure(ReviewCode.Busy));InvalidatePreview();}
        if(_analysis.State is not AnalysisState.Completed current)return Task.FromResult(Failure(ReviewCode.UnknownFix));
        try{
            var set=ReviewPreparation.CanonicalF1(current.Report,descriptorId,out var fix,out var citation);
            return set is null?Task.FromResult(Failure(ReviewCode.UnknownFix)):PreviewCore(set,current.Report,fix,citation,ct);
        }catch(Exception e)when(e is System.Text.Json.JsonException or ArgumentException or InvalidOperationException){return Task.FromResult(Failure(ReviewCode.UnknownFix));}
    }
    private async Task<ReviewResult> PreviewCore(TextChangeSet set,AnalysisReport? report,AnalysisFixDescriptorDto? fix,ReviewCitation? citation,CancellationToken ct,ReviewPreview? original=null)
    {
        long intent;CancellationTokenSource cts;ReviewBinding binding;
        lock(_sync){
            if(_disposed)return Failure(ReviewCode.Disposed);if(_busy)return Failure(ReviewCode.Busy);
            if(original is not null)
            {
                if(!ReferenceEquals(_preview,original)||!Authorized(original))return Failure(ReviewCode.Stale);
                if(_workspace.IsReadOnly||_workspace.Recovery is not null)return Failure(ReviewCode.ReadOnly);
                _previewIntent++;_previewCts?.Cancel(); // keep original identity until replacement commits
            }
            else InvalidatePreview();
            if(set is null||_workspace.CurrentTextPatchIdentity is null)return Failure(ReviewCode.InvalidInput);
            intent=_previewIntent;cts=CancellationTokenSource.CreateLinkedTokenSource(ct);_previewCts=cts;
            binding=original?.Binding??Binding(report);_busy=true;_status=ReviewStatus.Previewing;_notice=null;
        }
        Notify();
        bool Current(){lock(_sync)return !_disposed&&intent==_previewIntent&&!cts.IsCancellationRequested&&BaseCurrent(binding)
            &&(original is null||ReferenceEquals(_preview,original)&&Authorized(original)&&_workspace.IsCurrentTextPatchWriter(binding.Workspace));}
        ReviewResult Reject(ReviewCode code)
        {
            lock(_sync)
            {
                if(original is not null&&ReferenceEquals(_preview?.Proposal,set))_preview=null;
                return intent==_previewIntent&&!_disposed?Failure(code):new(_disposed?ReviewStatus.Disposed:_status,Failure:Error(code));
            }
        }
        try{
            if(!Current())return Reject(ReviewCode.Cancelled);
            if(original is not null&&(!await _workspace.VerifyTextPatchPreviewAuthorityAsync(binding.Workspace,cts.Token)||!Current()))return Reject(ReviewCode.Stale);
            if(report is not null&&!(await _navigator.VerifyCurrentAsync(report,cts.Token)).Ok)return Reject(ReviewCode.Stale);
            var capture=await _capture.CaptureSelectionAsync(binding.Selection,cts.Token,awaitActualReads:true);
            if(!Current())return Reject(cts.IsCancellationRequested?ReviewCode.Cancelled:ReviewCode.Stale);
            if(capture is not CaptureResult.Captured captured)return Reject(ReviewCode.Unavailable);
            if(original is not null&&(!await _workspace.VerifyTextPatchPreviewAuthorityAsync(binding.Workspace,cts.Token)||!Current()))return Reject(ReviewCode.Stale);
            var prep=PreparedTextBatch.Prepare(binding.Workspace,set.Files.Select(f=>f.Input()).ToArray(),ReviewPreparation.Sources(_workspace,captured.Snapshot),set.Origin,set.Explanation);
            if(prep.Batch is null)return Reject(ReviewCode.InvalidInput);
            var preview=new ReviewPreview(set,prep.Batch,binding,report,fix,citation,ReviewPreparation.Diff(prep.Batch));
            if(report is not null&&!(await _navigator.VerifyCurrentAsync(report,cts.Token)).Ok)return Reject(ReviewCode.Stale);
            lock(_sync){if(!Current()||!Authorized(preview))return Reject(ReviewCode.Stale);_preview=preview;_status=ReviewStatus.Ready;}
            Notify();
            if(original is not null)
            {
                bool PublishedCurrent(){lock(_sync)return !_disposed&&intent==_previewIntent&&!cts.IsCancellationRequested
                    &&ReferenceEquals(_preview,preview)&&Authorized(preview)&&_workspace.IsCurrentTextPatchWriter(binding.Workspace);}
                if(!PublishedCurrent()||!await _workspace.VerifyTextPatchPreviewAuthorityAsync(binding.Workspace,cts.Token)||!PublishedCurrent())
                {
                    lock(_sync){if(ReferenceEquals(_preview,preview))InvalidatePreview();}
                    return Reject(cts.IsCancellationRequested?ReviewCode.Cancelled:ReviewCode.Stale);
                }
            }
            return new(ReviewStatus.Ready,preview);
        }
        catch(OperationCanceledException){return Reject(ReviewCode.Cancelled);}
        catch(Exception){return Reject(original is not null&&_disposed?ReviewCode.Disposed:
            original is not null&&cts.IsCancellationRequested?ReviewCode.Cancelled:ReviewCode.Unavailable);}
        finally{lock(_sync){if(ReferenceEquals(_previewCts,cts)){_previewCts=null;_busy=false;if(_preview is null&&_status==ReviewStatus.Previewing)_status=_accepted is null?ReviewStatus.Idle:ReviewStatus.AppliedUnsaved;}}cts.Dispose();if(!_disposed)Notify();}
    }
    public ReviewResult Decline(Guid id)
    {
        lock(_sync){if(_disposed)return Failure(ReviewCode.Disposed);if(_busy)return Failure(ReviewCode.Busy);if(_preview?.Id!=id)return Failure(ReviewCode.Consumed);InvalidatePreview();_notice=null;}
        Notify();return new(ReviewStatus.Idle);
    }
    public void Cancel(){lock(_sync){if(_disposed)return;InvalidatePreview();_operationCts?.Cancel();}Notify();}

    private (long Intent,CancellationTokenSource Cts)? BeginOperation(CancellationToken ct,ReviewStatus status)
    {
        lock(_sync){if(_disposed||_busy)return null;_busy=true;_status=status;_notice=null;var cts=CancellationTokenSource.CreateLinkedTokenSource(ct);_operationCts=cts;return(++_operationIntent,cts);}
    }
    private bool OperationCurrent(long intent,CancellationToken ct){lock(_sync)return !_disposed&&_busy&&_operationIntent==intent&&!ct.IsCancellationRequested;}
    private void EndOperation(long intent,CancellationTokenSource cts)
    {
        lock(_sync){if(intent==_operationIntent){_busy=false;_operationCts=null;RefreshSession();RefreshPersistence();}}cts.Dispose();if(!_disposed)Notify();
    }
    public async Task<ReviewResult> AcceptAsync(Guid previewId,CancellationToken ct)
    {
        ReviewPreview preview;
        lock(_sync){
            RefreshSession();RefreshPersistence();if(_disposed)return Failure(ReviewCode.Disposed);if(_busy)return Failure(ReviewCode.Busy);
            if(_preview is not{} p||p.Id!=previewId)return Failure(ReviewCode.Consumed);
            if(_accepted is{Persisted:false})return Failure(ReviewCode.PendingSave);
            if(!Authorized(p)){InvalidatePreview();return Failure(ReviewCode.Stale);}preview=p;
        }
        var started=BeginOperation(ct,ReviewStatus.Applying);if(started is null)return Failure(ReviewCode.Busy);
        var (intent,cts)=started.Value;Accepted? action=null;
        Notify();
        try{
            var inverse=TextChangeSet.Create(TextPatchOrigin.User,"Undo the accepted exact text edits",ReviewPreparation.Inverse(preview.Prepared)).ChangeSet;
            if(inverse is null)return Failure(ReviewCode.InvalidInput);
            action=new(preview.Prepared,inverse); // owned recovery exists BEFORE the commit callback
            bool Eligible(){lock(_sync)return OperationCurrent(intent,cts.Token)&&ReferenceEquals(_preview,preview)&&Authorized(preview);}
            async Task<bool> Verify(CancellationToken token)=>Eligible()&&(preview.Report is null||(await _navigator.VerifyCurrentAsync(preview.Report,token)).Ok)&&Eligible();
            void Committed(){lock(_sync){action.Committed=true;_accepted=action;_preview=null;_status=ReviewStatus.AppliedUnsaved;_notice=null;}}
            var result=await _workspace.ApplyTextBatchAsync(preview.Prepared,cts.Token,Eligible,Verify,Committed);
            if(!result.AppliedUnsaved)return Failure(Map(result.Failure?.Code));
            return await Persist(action,cts.Token);
        }
        catch(OperationCanceledException){return action?.Committed==true?Applied(action,ReviewCode.Cancelled):Failure(ReviewCode.Cancelled);}
        catch(Exception){return action?.Committed==true?Applied(action,ReviewCode.Unavailable):Failure(ReviewCode.Unavailable);}
        finally{lock(_sync){if(ReferenceEquals(_preview,preview))InvalidatePreview();}EndOperation(intent,cts);}
    }
    private static ReviewCode Map(TextPatchError? code)=>code switch{
        TextPatchError.Cancelled=>ReviewCode.Cancelled,TextPatchError.ReadOnly or TextPatchError.LockLost=>ReviewCode.ReadOnly,
        TextPatchError.Stale or TextPatchError.Ineligible=>ReviewCode.Stale,TextPatchError.PreimageMismatch or TextPatchError.RevisionConflict=>ReviewCode.Conflict,
        _=>ReviewCode.InvalidInput,
    };
    private ReviewResult Applied(Accepted action,ReviewCode? issue=null)
    {
        lock(_sync){
            if(SameSession(action)&&ReferenceEquals(_accepted,action)){RefreshPersistence();_status=action.Persisted?(action.Undone?ReviewStatus.UndoSaved:ReviewStatus.AppliedSaved):issue is null?(action.Undone?ReviewStatus.UndoAppliedUnsaved:ReviewStatus.AppliedUnsaved):ReviewStatus.SaveFailed;_notice=issue is{} c?Error(c):null;}
            var status=action.Persisted?(action.Undone?ReviewStatus.UndoSaved:ReviewStatus.AppliedSaved):action.Undone?ReviewStatus.UndoAppliedUnsaved:ReviewStatus.AppliedUnsaved;
            return new(status,ActionId:action.Id,Failure:issue is{} e?Error(e):null,Changed:true);
        }
    }
    private async Task<ReviewResult> Persist(Accepted action,CancellationToken ct)
    {
        if(_disposed||!SameSession(action))return Applied(action,ReviewCode.Stale);
        try{
            var result=await _workspace.SaveSessionAsync(action.Batch.Identity.ProjectId,action.Batch.Identity.SessionId,ct);
            if(!SameSession(action))return Applied(action,ReviewCode.Stale);
            lock(_sync){if(ReferenceEquals(_accepted,action))RefreshPersistence();}
            if(result.Status!=CommitStatus.Saved)return Applied(action,result.Status==CommitStatus.RevisionConflict?ReviewCode.Conflict:ReviewCode.Unavailable);
            return Applied(action,action.Persisted?null:ReviewCode.PendingSave);
        }catch(OperationCanceledException){return Applied(action,ReviewCode.Cancelled);}
        catch(Exception){return Applied(action,ReviewCode.Unavailable);}
    }
    public async Task<ReviewResult> RetrySaveAsync(Guid actionId,CancellationToken ct)
    {
        Accepted action;lock(_sync){RefreshSession();RefreshPersistence();if(_disposed)return Failure(ReviewCode.Disposed);if(_busy)return Failure(ReviewCode.Busy);if(_accepted is not{} a||a.Id!=actionId)return Failure(ReviewCode.Consumed);action=a;if(a.Persisted)return Applied(a) with{Changed=false};}
        var started=BeginOperation(ct,action.Undone?ReviewStatus.UndoAppliedUnsaved:ReviewStatus.AppliedUnsaved);if(started is null)return Failure(ReviewCode.Busy);
        var (intent,cts)=started.Value;Notify();try{return (await Persist(action,cts.Token)) with{Changed=false};}finally{EndOperation(intent,cts);}
    }
    public async Task<ReviewResult> UndoAsync(Guid actionId,CancellationToken ct)
    {
        Accepted action;lock(_sync){RefreshSession();if(_disposed)return Failure(ReviewCode.Disposed);if(_busy)return Failure(ReviewCode.Busy);if(_accepted is not{} a||a.Id!=actionId||a.Undone)return Failure(ReviewCode.Consumed);action=a;InvalidatePreview();}
        var started=BeginOperation(ct,ReviewStatus.Undoing);if(started is null)return Failure(ReviewCode.Busy);
        var (intent,cts)=started.Value;Notify();var identity=_workspace.CurrentTextPatchIdentity;var epoch=_capture.CurrentWorkspaceEpoch;bool committed=false;
        bool Current(){lock(_sync)return OperationCurrent(intent,cts.Token)&&ReferenceEquals(_accepted,action)&&!action.Undone&&SameSession(action)&&_workspace.CurrentTextPatchIdentity==identity&&_capture.CurrentWorkspaceEpoch==epoch;}
        try{
            var cap=await _capture.CaptureSelectionAsync(AnalysisSelection.EngineStructural(),cts.Token,awaitActualReads:true);
            if(!Current())return Failure(ReviewCode.Stale);
            if(cap is not CaptureResult.Captured captured)return Failure(ReviewCode.Unavailable);
            var prep=PreparedTextBatch.Prepare(identity!,action.Inverse.Files.Select(f=>f.Input()).ToArray(),ReviewPreparation.Sources(_workspace,captured.Snapshot),TextPatchOrigin.User,action.Inverse.Explanation);
            if(prep.Batch is null)return Failure(ReviewCode.Conflict);
            void Committed(){lock(_sync){committed=true;action.Undone=true;action.Persisted=false;action.LegBaseHead=_workspace.CurrentRevision?.Id;_status=ReviewStatus.UndoAppliedUnsaved;_notice=null;}}
            var applied=await _workspace.ApplyTextBatchAsync(prep.Batch,cts.Token,Current,committedBeforeNotification:Committed);
            if(!applied.AppliedUnsaved)return Failure(Map(applied.Failure?.Code));
            return await Persist(action,cts.Token);
        }catch(OperationCanceledException){return committed?Applied(action,ReviewCode.Cancelled):Failure(ReviewCode.Cancelled);}
        catch(Exception){return committed?Applied(action,ReviewCode.Unavailable):Failure(ReviewCode.Unavailable);}
        finally{EndOperation(intent,cts);}
    }
    public async ValueTask DisposeAsync()
    {
        lock(_sync){if(_disposed)return;_disposed=true;InvalidatePreview();_operationCts?.Cancel();Changed=null;}
        _workspace.WorkspaceChanged-=WorkspaceChanged;_workspace.RevisionCommitted-=Saved;_analysis.Changed-=AnalysisChanged;
        await _capture.DisposeAsync();
        // Accepted recovery is not cleared by disposal/cancellation after commit.
        // It is bounded to one entry and released with the scoped service object.
    }
}
