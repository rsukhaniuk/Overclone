using Client.Review;

namespace Client.Pages;

public partial class Workspace
{
    private CancellationTokenSource? _reviewCts;
    private long _reviewIntent;
    private bool _reviewPending;
    private string? _reviewNotice;
    private Guid _reviewSession;
    private Guid? _seenPreview,_seenAction;
    private ReviewServiceState CurrentReview => ReviewService.State;
    private bool ReviewWritable => Coordinator.IsOpen && !Coordinator.IsReadOnly && Coordinator.Recovery is null;

    private void OnReviewChanged(ReviewServiceState ignored)
    {
        _ = ignored; // never render a delayed notification's old state
        _ = InvokeAsync(RenderReviewState).ContinueWith(ObserveTaskFault,TaskScheduler.Default);
    }
    private void RenderReviewState()
    {
        if(_pageDisposed)return;
        var current=CurrentReview;
        if(_reviewSession!=Coordinator.SessionId)
        {
            _reviewIntent++;_reviewCts?.Cancel();_reviewPending=false;_reviewNotice=null;
            _seenPreview=null;_seenAction=null;_reviewSession=Coordinator.SessionId;
        }
        if(_seenPreview is not null && current.Preview is null && current.Action?.Id==_seenAction && !_reviewPending)
            _reviewNotice="The preview is no longer current. Run analysis and request a fresh preview; no stale proposal was accepted.";
        _seenPreview=current.Preview?.Id;_seenAction=current.Action?.Id;
        StateHasChanged();
    }
    private Task PreviewFixAsync(string id)=>RunReviewAsync(ct=>ReviewService.PreviewFixAsync(id,ct));
    private Task AcceptReviewAsync(Guid id)=>RunReviewAsync(ct=>ReviewService.AcceptAsync(id,ct));
    private Task RetryReviewAsync(Guid id)=>RunReviewAsync(ct=>ReviewService.RetrySaveAsync(id,ct));
    private Task UndoReviewAsync(Guid id)=>RunReviewAsync(ct=>ReviewService.UndoAsync(id,ct));
    private void DeclineReview(Guid id)
    {
        if(_pageDisposed||_reviewPending||CurrentReview.Busy)return;
        var result=ReviewService.Decline(id);_reviewNotice=result.Failure?.SafeMessage;
        _seenPreview=null;RenderReviewState();
    }
    private void CancelReview()
    {
        _reviewCts?.Cancel();ReviewService.Cancel();
        _reviewNotice="Pending review work cancelled. Already-applied working edits and recovery, if any, are retained below.";
        _seenPreview=null;RenderReviewState();
    }
    private async Task RunReviewAsync(Func<CancellationToken,Task<ReviewResult>> operation)
    {
        if(_pageDisposed||_reviewPending||CurrentReview.Busy)return;
        var intent=++_reviewIntent;var session=Coordinator.SessionId;_reviewSession=session;
        var cts=new CancellationTokenSource();_reviewCts=cts;_reviewPending=true;_reviewNotice=null;
        // Review never cancels an editor's pending ordinary autosave. A real
        // save may advance the head and invalidate this preview; the accepted
        // service guards then require a fresh preview rather than stale apply.
        StateHasChanged();
        bool Current()=>!_pageDisposed&&intent==_reviewIntent&&session==Coordinator.SessionId;
        try
        {
            var result=await operation(cts.Token); // exactly one service operation
            if(!Current())return;
            _reviewNotice=result.Failure?.SafeMessage;
            _seenPreview=CurrentReview.Preview?.Id;_seenAction=CurrentReview.Action?.Id;
            if(CurrentReview.Action is { Persisted:true })await LoadHistoryAsync(first:true);
        }
        catch(OperationCanceledException)
        { if(Current())_reviewNotice="Review work cancelled; current applied/save state and retained recovery are shown below."; }
        catch(Exception)
        { if(Current())_reviewNotice="Review could not finish displaying its result. Inspect the current action/save state below; any applied working edits and recovery are retained."; }
        finally
        {
            if(ReferenceEquals(_reviewCts,cts)){_reviewCts=null;_reviewPending=false;}
            cts.Dispose();if(Current())StateHasChanged();
        }
    }
}
