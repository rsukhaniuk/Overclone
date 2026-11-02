using Client.Review;
using Client.Workspace;
using Client.Workspace.Domain;
using Microsoft.JSInterop;

namespace Client.Pages;

// Proposed master-owned composition only. Does not replace coordinator/review
// mutation, persistence, inverse or source-editor journal policies.
public partial class Workspace
{
    private long _historyListIntent, _historyWholeIntent;
    private bool _historyWholeBusy, _historyRecoveryBusy;
    private CancellationTokenSource? _historyWholeCts;
    private RevisionId? _historyPriorHead;
    private Guid _historyPriorSession;
    private string? _historyRecoveryNotice;
    private string _historyRecoveryFile = "";
    private Task DownloadSelectedReviewRecoveryAsync(Guid actionId) => Guid.TryParse(_historyRecoveryFile, out var file)
        ? DownloadReviewRecoveryAsync(actionId, new FileId(file)) : Task.CompletedTask;
    private bool HistoryHasUnresolvedReview => _reviewPending || CurrentReview.Busy || CurrentReview.Preview is not null
        || CurrentReview.Action is { Persisted: false };
    private RevisionId? HistoryPriorHead => _historyPriorSession == Coordinator.SessionId ? _historyPriorHead : null;

    private Task HistoryReviewPreparedAsync(ReviewPreview preview) => InvokeAsync(() =>
    {
        if (_pageDisposed || CurrentReview.Preview?.Id != preview.Id) return;
        _reviewSession = Coordinator.SessionId;
        _seenPreview = preview.Id; _seenAction = CurrentReview.Action?.Id;
        _reviewNotice = "Historical text restore is proposed, not applied. Review the exact bytes below, then explicitly accept or decline.";
        StateHasChanged();
    });

    private void CancelHistoryWholePreview()
    {
        // A begun acceptance delegates to the existing atomic restore boundary;
        // this button cancels preview work only, never claims to undo a commit.
        if (_historyWholeBusy && _historyWholeCts is null) return;
        _historyWholeIntent++; _historyWholeCts?.Cancel();
        _restorePreview = null; _comparison = null;
        _restoreError = "Whole-project restore preview cancelled. Started reads must settle before retrying.";
    }

    private async Task OpenHistoryWholeRestoreAsync(RevisionId revision)
    {
        if (_pageDisposed || _historyWholeBusy || HistoryComparisons.Busy || !ReviewWritable || HistoryHasUnresolvedReview) return;
        if (Coordinator.Project is not { } project) return;
        var session = Coordinator.SessionId; long intent = ++_historyWholeIntent;
        var cts = new CancellationTokenSource(); _historyWholeCts = cts; _historyWholeBusy = true;
        _restorePreview = null; _comparison = null; _selectedDiff = null; _selectedDiffPath = ""; _restoreError = null;
        bool Current() => !_pageDisposed && intent == _historyWholeIntent && !cts.IsCancellationRequested
            && Coordinator.Project?.Id == project.Id && Coordinator.SessionId == session;
        StateHasChanged();
        try
        {
            // This is the explicitly requested whole-project action, NOT the
            // display-only comparison path. Its existing policy may flush/save.
            var preview = await Coordinator.CaptureRestorePreviewAsync(revision, cts.Token);
            if (!Current()) return;
            if (preview.Token.SessionId != Coordinator.SessionId || preview.Token.WorkingGeneration != Coordinator.WorkingGeneration
                || preview.Token.ExpectedHead != Coordinator.Project?.HeadRevisionId || !ReviewWritable || HistoryHasUnresolvedReview)
            { _restoreError = "The workspace changed. Request a fresh whole-project preview."; return; }
            _restorePreview = preview; _comparison = preview.Summary;
        }
        catch (OperationCanceledException) { if (Current()) _restoreError = "Whole-project preview cancelled."; }
        catch (Exception) { if (Current()) _restoreError = "Whole-project preview is unavailable. Resolve local save/recovery problems first; no restore has been accepted."; }
        finally
        {
            if (ReferenceEquals(_historyWholeCts, cts)) { _historyWholeCts = null; _historyWholeBusy = false; }
            cts.Dispose(); if (!_pageDisposed) StateHasChanged();
        }
    }

    private async Task DownloadReviewRecoveryAsync(Guid actionId, FileId fileId)
    {
        if (_pageDisposed || _historyRecoveryBusy || CurrentReview.Busy || _reviewPending) return;
        if (CurrentReview.Action is not { } action || action.Id != actionId || action.SessionId != Coordinator.SessionId) return;
        var file = action.Exact.Files.SingleOrDefault(f => f.FileId == fileId); if (file is null) return;
        var project = Coordinator.Project?.Id; var session = Coordinator.SessionId;
        long generation = Coordinator.WorkingGeneration; var head = Coordinator.Project?.HeadRevisionId;
        bool undone = action.UndoApplied;
        var bytes = (undone ? file.Before : file.After).ToArray();
        bool Current() => !_pageDisposed && Coordinator.Project?.Id == project && Coordinator.SessionId == session
            && Coordinator.WorkingGeneration == generation && Coordinator.Project?.HeadRevisionId == head
            && CurrentReview.Action is { } current && current.Id == actionId && current.UndoApplied == undone;
        _historyRecoveryBusy = true; _historyRecoveryNotice = null;
        IJSObjectReference? module = null;
        try
        {
            // Explicit None avoids cancellation/default-timeout proxies for a
            // started browser callback. Own bytes first; check before dispatch.
            module = await JS.InvokeAsync<IJSObjectReference>("import", CancellationToken.None, new object?[] { "./js/download.js" });
            if (!Current()) return;
            await module.InvokeVoidAsync("downloadBytes", CancellationToken.None, new object?[] { file.RelativePath, bytes });
            if (Current()) _historyRecoveryNotice = "Retained reviewed bytes were sent to the browser download. This is a recovery copy, not confirmation of a local save or of unrelated later edits.";
        }
        catch (Exception) { if (Current()) _historyRecoveryNotice = "Recovery download could not finish. Exact review recovery and retry/Undo are still retained."; }
        finally
        {
            if (module is not null) { try { await module.DisposeAsync(); } catch (Exception) { /* retain recovery; no raw exception */ } }
            _historyRecoveryBusy = false; if (!_pageDisposed) StateHasChanged();
        }
    }
}
