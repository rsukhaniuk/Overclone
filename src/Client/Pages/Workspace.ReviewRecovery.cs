using Client.Workspace;
using Client.Workspace.Domain;
using Microsoft.JSInterop;

namespace Client.Pages;

public partial class Workspace
{
    private bool _historyRecoveryBusy;
    private string? _historyRecoveryNotice;
    private string _historyRecoveryFile = "";
    private Task DownloadSelectedReviewRecoveryAsync(Guid actionId) => Guid.TryParse(_historyRecoveryFile, out var file)
        ? DownloadReviewRecoveryAsync(actionId, new FileId(file)) : Task.CompletedTask;

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
