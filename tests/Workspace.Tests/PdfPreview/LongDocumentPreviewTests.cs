using Client.Pdf;
using Client.Pdf.Preview;
using Xunit;

namespace Workspace.Tests.PdfPreview;

// CONTRACT-ONLY long-document tests; not native/PDF/browser acceptance.
public sealed class LongDocumentPreviewTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(4);
    private static async Task Idle(PdfPreviewController c) => await c.WhenSettled.WaitAsync(Bound);
    [Fact] public void Default_policy_admits_512_but_search_remains_32()
    {
        var p = new PdfPreviewPolicy();
        Assert.Equal(512, p.MaxDocumentPages); Assert.Equal(32, p.MaxSearchPages); Assert.True(p.IsValid);
        Assert.True(new PdfPreviewPolicy(MaxDocumentPages: 1).IsValid);
        Assert.True(new PdfPreviewPolicy(MaxDocumentPages: 512).IsValid);
        Assert.False(new PdfPreviewPolicy(MaxDocumentPages: 513).IsValid);
        Assert.False(new PdfPreviewPolicy(MaxDocumentPages: 0).IsValid);
    }
    [Theory] [InlineData(33)] [InlineData(64)] [InlineData(512)]
    public async Task Long_document_last_page_displays_and_navigates_with_selected_budget(int pages)
    {
        var port = new PreviewInspector { PageCountOverride = pages };
        await using var c = new PdfPreviewController(port); c.Provide(PreviewInspector.Document()); await Idle(c);
        Assert.Equal(pages, c.State.PageCount); Assert.True(c.GoToPage(pages)); await Idle(c);
        Assert.Equal(PdfPreviewStatus.Available, c.State.Status); Assert.Equal(pages - 1, c.State.PageIndex);
        Assert.Equal(pages - 1, c.State.Frame!.PageIndex);
        Assert.All(port.Calls, r => Assert.True(r.PageIndex < pages && r.PageIndex < r.Budget.Pages));
        Assert.All(port.Calls, r => Assert.Equal(512, r.Budget.Pages));
    }
    [Fact] public async Task Explicit_32_budget_preserved_against_33_page_document()
    {
        var port = new PreviewInspector { PageCountOverride = 33 };
        await using var c = new PdfPreviewController(port, new(MaxDocumentPages: 32));
        c.Provide(PreviewInspector.Document()); await Idle(c);
        Assert.Equal(PdfPreviewStatus.Error, c.State.Status); Assert.Null(c.State.Frame);
        Assert.All(port.Calls, r => Assert.Equal(32, r.Budget.Pages));
    }
    [Fact] public async Task Page_index_beyond_budget_rejects_before_inspector()
    {
        var doc = PreviewInspector.Document();
        var budget = new PdfInspectionBudget(Pages: 64);
        Assert.False(new PdfInspectRequest(doc, 64, budget).IsValid);
        Assert.False(new PdfInspectRequest(doc, 513, new(Pages: 512)).IsValid);
        Assert.False(new PdfInspectRequest(doc, -1, new(Pages: 512)).IsValid);
        Assert.True(new PdfInspectRequest(doc, 63, budget).IsValid);
        Assert.False(new PdfInspectRequest(doc, 0, new(Pages: 513)).IsValid);
    }
    [Fact] public async Task Search_caps_at_32_and_reports_partial_for_long_document()
    {
        var port = new PreviewInspector { PageCountOverride = 64 };
        await using var c = new PdfPreviewController(port); c.Provide(PreviewInspector.Document()); await Idle(c);
        c.Search("І"); await Idle(c);
        Assert.Equal(PdfPreviewSearchStatus.Partial, c.State.Search.Status);
        Assert.Equal(32, c.State.Search.ScannedPages); Assert.Equal(64, c.State.Search.TotalPages);
        Assert.Contains("Incomplete", c.State.Search.Detail);
    }
    [Fact] public async Task Smaller_explicit_policy_respected_for_admission_and_navigation()
    {
        var port = new PreviewInspector { PageCountOverride = 40 };
        await using var c = new PdfPreviewController(port, new(MaxDocumentPages: 40));
        c.Provide(PreviewInspector.Document()); await Idle(c);
        Assert.Equal(40, c.State.PageCount); Assert.True(c.GoToPage(40)); await Idle(c);
        Assert.Equal(PdfPreviewStatus.Available, c.State.Status); Assert.Equal(39, c.State.PageIndex);
        Assert.False(c.GoToPage(41));
    }
    [Fact] public async Task Stale_and_cancel_ownership_unchanged_for_long_documents()
    {
        var port = new PreviewInspector { PageCountOverride = 64 };
        var reached = PreviewInspector.Signal<bool>(); var release = PreviewInspector.Signal<bool>(); bool hold = true;
        port.Handler = async (q, e) => { if (hold) { hold = false; reached.SetResult(true); await release.Task; } return PreviewInspector.Page(q, e, pageCount: 64); };
        await using var c = new PdfPreviewController(port);
        try
        {
            c.Provide(PreviewInspector.Document()); await reached.Task.WaitAsync(Bound); c.Cancel();
            Assert.Equal(PdfPreviewStatus.Cancelled, c.State.Status); Assert.True(c.ActualWorkPending);
            release.SetResult(true); await Idle(c); Assert.Null(c.State.Frame); Assert.Equal(0, port.Active);
        }
        finally { release.TrySetResult(true); await Idle(c); }
    }
}
