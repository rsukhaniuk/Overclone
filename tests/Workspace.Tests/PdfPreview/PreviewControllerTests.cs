using Client.Pdf;
using Client.Pdf.Preview;
using Xunit;

namespace Workspace.Tests.PdfPreview;

// Fake inspector CONTRACT tests, not native PDF/UI acceptance.
public sealed class PreviewControllerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(4);
    private static async Task Idle(PdfPreviewController c) => await c.WhenSettled.WaitAsync(Bound);
    [Fact] public async Task Default_absence_is_unavailable_without_actual_work()
    {
        await using var c = new PdfPreviewController(); Assert.True(c.Provide(PreviewInspector.Document()));
        Assert.Equal(PdfPreviewStatus.Unavailable, c.State.Status); Assert.False(c.ActualWorkPending); Assert.Null(c.State.Frame);
        Assert.Equal(PdfDisplayProvenance.ProvidedPdf, c.State.Provenance); Assert.Contains("Unavailable", c.State.SourceNavigationStatus);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Invalid_policy_is_rejected_before_any_work(int field)
    {
        var p = new PdfPreviewPolicy(); p = field switch { 0 => p with { InputBytes = 0 }, 1 => p with { MaxPixels = 0 }, 2 => p with { MaxRgbaBytes = 0 },
            3 => p with { MaxSearchPages = 33 }, 4 => p with { MaxHits = 257 }, 5 => p with { MaxHighlightBoxes = 0 }, 6 => p with { DeadlineMs = 30001 },
            7 => p with { MaxDocumentPages = 0 }, _ => p with { MaxDocumentPages = 513 } };
        Assert.Throws<ArgumentException>(() => new PdfPreviewController(new PreviewInspector(), p));
    }
    [Fact] public async Task Overbudget_input_rejects_before_inspector_or_raster()
    {
        var port = new PreviewInspector(); await using var c = new PdfPreviewController(port, new(InputBytes: 4));
        Assert.False(c.ProvideBytes([1, 2, 3, 4, 5])); Assert.Empty(port.Calls); Assert.Null(c.State.Frame); Assert.Equal(PdfPreviewStatus.Error, c.State.Status);
    }
    [Fact] public async Task Owned_input_hash_and_bgra_rgba_color_alpha_are_preserved()
    {
        var port = new PreviewInspector(); await using var c = new PdfPreviewController(port); byte[] b = [37, 80, 68, 70, 1]; string sha = PdfHashes.Of(b);
        Assert.True(c.ProvideBytes(b, "<script>ordinary text</script>")); b[0] = 0; await Idle(c);
        Assert.Equal(PdfPreviewStatus.Available, c.State.Status); Assert.Equal(sha, c.State.DocumentSha256); Assert.Equal(sha, c.State.Frame!.DocumentSha256);
        Assert.Equal(new byte[] { 200, 7, 3, 99 }, c.State.Frame.CopyRgba().Take(4));
        var rgba = c.State.Frame.CopyRgba(); rgba[0] = 0; Assert.Equal(200, c.State.Frame.CopyRgba()[0]); Assert.Equal(37, port.Calls[0].Document.CopyBytes()[0]);
    }
    [Fact] public void Pixel_budget_and_shape_precede_conversion_allocation()
    {
        Assert.Throws<ArgumentException>(() => PdfPreviewPixels.ToRgba([1, 2, 3, 4], 2, 2, new()));
        Assert.Throws<ArgumentException>(() => PdfPreviewPixels.ToRgba([], int.MaxValue, int.MaxValue, new()));
        Assert.Null(PdfPreviewPixels.Dimensions(new(double.NaN, 100), 360, 100, new()));
        Assert.Null(PdfPreviewPixels.Dimensions(new(1, 100000), 360, 100, new()));
        Assert.Null(PdfPreviewPixels.Dimensions(new(100, 200), 360, 301, new()));
    }
    [Fact] public void Raster_highlight_edges_round_once_instead_of_accumulating_css_percent_error()
    {
        // Actual native N11 glyph I in configuration4. Separate CSS left/width
        // quantization produced right87.484375 versus native device right88.
        var box = PdfPreviewPixels.RasterBox(new(.2379541960516362, .17791107219430333, .005104505258996167, .008082342397440656), 360, 509);
        Assert.Equal(new PdfPreviewRect(86, 91, 2, 4), box);
        Assert.Equal(88, box.Left + box.Width);
        Assert.Throws<ArgumentException>(() => PdfPreviewPixels.RasterBox(new(-.1, 0, .1, .1), 360, 509));
        Assert.Throws<ArgumentException>(() => PdfPreviewPixels.RasterBox(new(0, 0, double.NaN, .1), 360, 509));
    }
    [Fact] public async Task Bounds_do_not_dispatch_and_overbudget_zoom_never_requests_pixels()
    {
        var port = new PreviewInspector(); await using var c = new PdfPreviewController(port); c.Provide(PreviewInspector.Document()); await Idle(c); int n = port.Calls.Count;
        Assert.False(c.GoToPage(0)); Assert.False(c.GoToPage(4)); Assert.False(c.Zoom(0)); Assert.False(c.Fit(1601)); Assert.Equal(n, port.Calls.Count);
        Assert.True(c.Fit(1600)); await Idle(c); Assert.Equal(PdfPreviewStatus.Error, c.State.Status); Assert.Null(c.State.Frame); Assert.False(port.Calls.Last().Raster);
    }
    [Fact] public void Native_glyph_index_is_not_utf16_offset_and_missing_boxes_are_not_guessed()
    {
        var request = new PdfInspectRequest(PreviewInspector.Document(), 0, new(), "😀", false);
        var page = PreviewInspector.Page(request, 1).Page!; Assert.Equal(4, page.Text.Length); Assert.Equal(3, page.Glyphs.Count);
        var hit = PdfPreviewPixels.Hit(page, Assert.Single(page.SearchHits), 10);
        Assert.Equal("😀", hit.GlyphText); Assert.Equal(.3, Assert.Single(hit.NormalizedBoxes).Left, 8);
        Assert.Equal(new[] { 1030, 55357, 56832, 120 }, page.Utf16Units);
        var missing = PreviewInspector.Page(request, 2, missingBox: true).Page!;
        var uncertain = PdfPreviewPixels.Hit(missing, missing.SearchHits[0], 10); Assert.Empty(uncertain.NormalizedBoxes); Assert.Contains("indeterminate", uncertain.GeometryStatus);
    }
    [Fact] public void Display_geometry_uses_rotated_normalized_space_and_clips_without_physical_claims()
    {
        var request = new PdfInspectRequest(PreviewInspector.Document(), 0, new(), "І", false);
        var page = PreviewInspector.Page(request, 1, rotate: true).Page!; var rect = Assert.Single(PdfPreviewPixels.Hit(page, page.SearchHits[0], 1).NormalizedBoxes);
        Assert.Equal(.05, rect.Left, 8); Assert.Equal(.2, rect.Top, 8); Assert.Equal(.05, rect.Width, 8); Assert.Null(page.PhysicalSizeInPdfPoints);
        Assert.Empty(PdfPreviewPixels.Hit(page, page.SearchHits[0], 0).NormalizedBoxes);
    }
    [Fact] public async Task Full_document_search_and_result_navigation_use_real_page_indices()
    {
        var port = new PreviewInspector(); await using var c = new PdfPreviewController(port); c.Provide(PreviewInspector.Document()); await Idle(c);
        c.Search("І"); await Idle(c); Assert.Equal(PdfPreviewSearchStatus.Complete, c.State.Search.Status); Assert.Equal(3, c.State.Search.ScannedPages);
        Assert.Equal(new[] { 0, 1, 2 }, c.State.Search.Hits.Select(h => h.PageIndex)); Assert.True(c.SelectHit(2)); await Idle(c);
        Assert.Equal(2, c.State.PageIndex); Assert.Equal(2, c.State.Frame!.PageIndex); Assert.Equal(2, c.State.SelectedHit);
        Assert.False(c.SelectHit(3)); Assert.True(c.Search("")); await Idle(c); Assert.Equal(PdfPreviewSearchStatus.Empty, c.State.Search.Status); Assert.Empty(c.State.Search.Hits);
    }
    [Fact] public async Task Zero_hits_require_a_complete_scan_not_an_empty_or_failed_query()
    {
        await using var c = new PdfPreviewController(new PreviewInspector()); c.Provide(PreviewInspector.Document()); await Idle(c); c.Search("absent"); await Idle(c);
        Assert.Equal(PdfPreviewSearchStatus.Complete, c.State.Search.Status); Assert.Empty(c.State.Search.Hits); Assert.Equal(3, c.State.Search.ScannedPages); Assert.Contains("zero matches", c.State.Search.Detail);
        Assert.False(c.Search("\ud800")); Assert.False(c.Search(new string('a', 256)));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Page_or_aggregate_hit_caps_report_partial_not_complete(bool pageCap)
    {
        await using var c = new PdfPreviewController(new PreviewInspector(), pageCap ? new(MaxSearchPages: 1) : new(MaxHits: 1));
        c.Provide(PreviewInspector.Document()); await Idle(c); c.Search("І"); await Idle(c);
        Assert.Equal(PdfPreviewSearchStatus.Partial, c.State.Search.Status); Assert.Single(c.State.Search.Hits); Assert.Contains("Incomplete", c.State.Search.Detail);
    }
    [Fact] public async Task Search_failure_preserves_partial_hits_but_never_claims_zero_or_complete()
    {
        var port = new PreviewInspector(); port.Handler = (q, e) => Task.FromResult(q.Search is not null && q.PageIndex == 1
            ? PreviewInspector.Page(q, e) with { Status = PdfInspectionStatus.TransportError, Page = null, Code = "InjectedContractFailure" } : PreviewInspector.Page(q, e));
        await using var c = new PdfPreviewController(port); c.Provide(PreviewInspector.Document()); await Idle(c); c.Search("І"); await Idle(c);
        Assert.Equal(PdfPreviewStatus.Error, c.State.Status); Assert.Equal(PdfPreviewSearchStatus.Error, c.State.Search.Status); Assert.Single(c.State.Search.Hits); Assert.Null(c.State.Frame);
    }
    [Fact] public async Task Rapid_page_selections_hold_actual_admission_and_keep_only_latest()
    {
        var port = new PreviewInspector(); await using var c = new PdfPreviewController(port); c.Provide(PreviewInspector.Document()); await Idle(c);
        var reached = PreviewInspector.Signal<bool>(); var release = PreviewInspector.Signal<bool>(); bool hold = true;
        port.Handler = async (q, e) => { if (hold) { hold = false; reached.SetResult(true); await release.Task; } return PreviewInspector.Page(q, e); };
        try
        {
            c.GoToPage(2); await reached.Task.WaitAsync(Bound); c.GoToPage(3); c.GoToPage(1);
            Assert.Equal(1, port.Active); Assert.Equal(1, c.PendingSelections); Assert.True(c.ActualWorkPending); Assert.Null(c.State.Frame);
            release.SetResult(true); await Idle(c); Assert.Equal(0, c.State.PageIndex); Assert.Equal(0, c.State.Frame!.PageIndex); Assert.Equal(1, port.PeakActive);
        }
        finally { release.TrySetResult(true); await Idle(c); }
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Old_document_or_query_reply_cannot_publish_after_latest_selection(bool document)
    {
        var port = new PreviewInspector(); await using var c = new PdfPreviewController(port); c.Provide(PreviewInspector.Document()); await Idle(c);
        var reached = PreviewInspector.Signal<bool>(); var release = PreviewInspector.Signal<bool>(); bool hold = true;
        port.Handler = async (q, e) => { if (hold) { hold = false; reached.SetResult(true); await release.Task; } return PreviewInspector.Page(q, e); };
        try
        {
            c.Search("І"); await reached.Task.WaitAsync(Bound);
            var latest = PreviewInspector.Document(2);
            if (document) c.Provide(latest); else c.Search("😀");
            Assert.Null(c.State.Frame); release.SetResult(true); await Idle(c);
            if (document) { Assert.Equal(latest.Sha256, c.State.Frame!.DocumentSha256); Assert.Equal(PdfPreviewSearchStatus.Empty, c.State.Search.Status); }
            else { Assert.Equal("😀", c.State.Search.Query); Assert.All(c.State.Search.Hits, h => Assert.Equal("😀", h.GlyphText)); }
        }
        finally { release.TrySetResult(true); await Idle(c); }
    }
    [Fact] public async Task Caller_cancel_is_prompt_but_actual_unfinished_task_keeps_admission()
    {
        var port = new PreviewInspector(); var reached = PreviewInspector.Signal<bool>(); var release = PreviewInspector.Signal<bool>();
        port.Handler = async (q, e) => { reached.TrySetResult(true); await release.Task; return PreviewInspector.Page(q, e); };
        await using var c = new PdfPreviewController(port);
        try
        {
            c.Provide(PreviewInspector.Document()); await reached.Task.WaitAsync(Bound); c.Cancel();
            Assert.Equal(PdfPreviewStatus.Cancelled, c.State.Status); Assert.True(c.ActualWorkPending); Assert.False(c.Retry()); Assert.Equal(1, port.Active);
            release.SetResult(true); await Idle(c); Assert.Null(c.State.Frame); Assert.Equal(0, port.Active);
        }
        finally { release.TrySetResult(true); await Idle(c); }
    }
    [Fact] public async Task Deadline_reports_unfinished_and_does_not_release_actual_task()
    {
        var port = new PreviewInspector(); var reached = PreviewInspector.Signal<bool>(); var release = PreviewInspector.Signal<bool>(); var expired = PreviewInspector.Signal<bool>();
        port.Handler = async (q, e) => { reached.TrySetResult(true); await release.Task; return PreviewInspector.Page(q, e); };
        await using var c = new PdfPreviewController(port, new(DeadlineMs: 500)); c.Changed += () => { if (c.State.Status == PdfPreviewStatus.TimedOut) expired.TrySetResult(true); };
        try
        {
            c.Provide(PreviewInspector.Document()); await reached.Task.WaitAsync(Bound); await expired.Task.WaitAsync(Bound);
            Assert.True(c.ActualWorkPending); Assert.Equal(1, port.Active); Assert.Null(c.State.Frame); release.SetResult(true); await Idle(c); Assert.Null(c.State.Frame);
        }
        finally { release.TrySetResult(true); await Idle(c); }
    }
    [Fact] public async Task Foreign_cancellation_is_error_not_caller_cancellation()
    {
        var port = new PreviewInspector { Handler = (_, _) => Task.FromException<PdfInspectionOutcome>(new OperationCanceledException()) };
        await using var c = new PdfPreviewController(port); c.Provide(PreviewInspector.Document()); await Idle(c);
        Assert.Equal(PdfPreviewStatus.Error, c.State.Status); Assert.Contains("ForeignCancellation", c.State.Detail);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Foreign_document_page_or_epoch_is_rejected(int field)
    {
        var port = new PreviewInspector(); port.Handler = (q, e) => Task.FromResult(field switch
        {
            0 => PreviewInspector.Page(q, e) with { DocumentSha256 = new('0', 64) },
            1 => PreviewInspector.Page(new(q.Document, 1, q.Budget, q.Search, q.Raster, q.RasterWidth, q.RasterHeight), e),
            _ => PreviewInspector.Page(q, e) with { WorkerEpoch = 0 }
        });
        await using var c = new PdfPreviewController(port); c.Provide(PreviewInspector.Document()); await Idle(c); Assert.Equal(PdfPreviewStatus.Error, c.State.Status); Assert.Null(c.State.Frame);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Disposal_detaches_callbacks_without_waiting_for_foreign_task_and_respects_ownership(bool owned)
    {
        var port = new PreviewInspector(); var reached = PreviewInspector.Signal<bool>(); var release = PreviewInspector.Signal<bool>();
        port.Handler = async (q, e) => { reached.SetResult(true); await release.Task; return PreviewInspector.Page(q, e); };
        var c = new PdfPreviewController(port, ownsInspector: owned); int events = 0; c.Changed += () => events++;
        try
        {
            c.Provide(PreviewInspector.Document()); await reached.Task.WaitAsync(Bound); await c.DisposeAsync(); int before = events;
            Assert.Equal(PdfPreviewStatus.Disposed, c.State.Status); Assert.True(c.ActualWorkPending); Assert.False(c.Provide(PreviewInspector.Document(2)));
            release.SetResult(true); await Idle(c); Assert.Equal(before, events); Assert.Equal(owned ? 1 : 0, port.Disposes);
        }
        finally { release.TrySetResult(true); await Idle(c); await c.DisposeAsync(); }
    }
}
