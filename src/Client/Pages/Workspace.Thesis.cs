using Client.Analysis;

namespace Client.Pages;

public partial class Workspace
{
    private async Task OnThesisEvidenceAsync(ThesisEvidenceRequest request)
    {
        if (_pageDisposed || !ReferenceEquals(AnalysisCurrentReport, request.Report)) return;
        var ct = NavCancelAndStart(); _navNotice = null;
        var active = Coordinator.ActiveFileId; var sourceIntent = _sourceIntent;
        try
        {
            var navigation = new ThesisSourceNavigation(Navigator, Coordinator);
            var result = await navigation.NavigateAsync(request, ct);
            if (_pageDisposed || ct.IsCancellationRequested || !ReferenceEquals(AnalysisCurrentReport, request.Report)) return;
            if (active != Coordinator.ActiveFileId || sourceIntent != _sourceIntent)
            { _navNotice = "The active editor changed while navigating; choose the source evidence again."; StateHasChanged(); return; }
            await HandleAnalysisNavigationAsync(result, ct);
        }
        catch (System.OperationCanceledException) { }
        catch (Exception error) when (error is Microsoft.JSInterop.JSException or System.Text.Json.JsonException or InvalidOperationException)
        { if (!_pageDisposed && !ct.IsCancellationRequested) { _navNotice = "The source evidence is no longer selectable; analyze again."; StateHasChanged(); } }
    }
}
