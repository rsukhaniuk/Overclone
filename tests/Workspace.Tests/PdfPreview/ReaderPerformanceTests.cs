using Client.Pdf;
using Client.Pdf.Preview;
using Xunit;

namespace Workspace.Tests.PdfPreview;

// Synthetic inspector contracts; browser timing and native PDF coverage are separate.
public sealed class ReaderPerformanceTests
{
    private static async Task Idle(PdfPreviewController c) => await c.WhenSettled.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Neighbor_prefetch_keeps_current_page_and_uses_one_admission()
    {
        var inspector = new PreviewInspector { PageCountOverride = 12 };
        await using var c = new PdfPreviewController(inspector, interactiveReader: true);
        var neighbors = new List<PdfPreviewFrame>();
        c.ReaderPageReady += neighbors.Add;
        c.Provide(PreviewInspector.Document());
        await Idle(c);
        Assert.Equal(0, c.State.PageIndex);
        Assert.Equal(0, c.State.Frame!.PageIndex);
        Assert.Equal(new[] { 1, 2, 3 }, neighbors.Select(frame => frame.PageIndex));
        Assert.All(neighbors, frame => Assert.Equal(c.State.Generation, frame.Generation));
        Assert.Equal(1, inspector.PeakActive);
        Assert.True(c.GoToPage(2));
        Assert.Equal(PdfPreviewStatus.Available, c.State.Status); // cache hit is synchronous
        Assert.Equal(1, c.State.Frame!.PageIndex);
        await Idle(c);
    }

    [Fact]
    public async Task Navigation_cancels_prefetch_but_waits_for_actual_settlement_and_refuses_stale_frame()
    {
        var inspector = new PreviewInspector();
        var reached = PreviewInspector.Signal<bool>();
        var release = PreviewInspector.Signal<bool>();
        inspector.Handler = async (request, epoch) =>
        {
            if (request.PageIndex == 1) { reached.TrySetResult(true); await release.Task; }
            return PreviewInspector.Page(request, epoch, pageCount: 12);
        };
        // PreviewInspector deliberately ignores cancellation while its actual task is held.
        await using var c = new PdfPreviewController(inspector, interactiveReader: true);
        var neighbors = new List<PdfPreviewFrame>();
        c.ReaderPageReady += neighbors.Add;
        try
        {
            c.Provide(PreviewInspector.Document());
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PdfPreviewStatus.Available, c.State.Status);
            Assert.True(c.GoToPage(10));
            Assert.True(c.ActualWorkPending);
            Assert.Equal(1, inspector.Active);
            Assert.Equal(1, c.PendingSelections);
            release.TrySetResult(true);
            await Idle(c);
            Assert.Equal(9, c.State.PageIndex);
            Assert.DoesNotContain(neighbors, frame => frame.PageIndex == 1);
            Assert.All(neighbors, frame => Assert.Equal(c.State.Generation, frame.Generation));
            Assert.Equal(1, inspector.PeakActive);
        }
        finally { release.TrySetResult(true); await Idle(c); }
    }

    [Fact]
    public async Task Resolution_upgrade_resumes_the_interrupted_neighbor()
    {
        var inspector = new PreviewInspector();
        var reached = PreviewInspector.Signal<bool>();
        var release = PreviewInspector.Signal<bool>();
        inspector.Handler = async (request, epoch) =>
        {
            if (request.PageIndex == 1) { reached.TrySetResult(true); await release.Task; }
            return PreviewInspector.Page(request, epoch);
        };
        await using var c = new PdfPreviewController(inspector, interactiveReader: true);
        var neighbors = new List<PdfPreviewFrame>();
        c.ReaderPageReady += neighbors.Add;
        try
        {
            c.Provide(PreviewInspector.Document());
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            typeof(PdfPreviewController).GetMethod("RefineReaderRaster", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(c, new object[] { c.State.Generation, c.State.DocumentSha256!, 0, 700 });
            release.TrySetResult(true);
            await Idle(c);
            Assert.Equal(700, c.State.Frame!.Width);
            Assert.Contains(neighbors, frame => frame.PageIndex == 1);
            Assert.Equal(1, inspector.PeakActive);
        }
        finally { release.TrySetResult(true); await Idle(c); }
    }

    [Fact]
    public async Task Speculative_errors_do_not_replace_ready_page_or_loop()
    {
        var inspector = new PreviewInspector();
        inspector.Handler = (request, epoch) => Task.FromResult(request.PageIndex == 0
            ? PreviewInspector.Page(request, epoch, pageCount: 12)
            : PreviewInspector.Page(request, epoch, pageCount: 12) with { Status = PdfInspectionStatus.TransportError, Page = null });
        await using var c = new PdfPreviewController(inspector, interactiveReader: true);
        c.Provide(PreviewInspector.Document());
        await Idle(c);
        Assert.Equal(PdfPreviewStatus.Available, c.State.Status);
        Assert.Equal(0, c.State.Frame!.PageIndex);
        Assert.Equal(5, inspector.Calls.Count); // current metadata+raster, then three bounded attempts
    }

    [Fact]
    public async Task Replacing_document_never_reuses_old_neighbors()
    {
        var inspector = new PreviewInspector();
        await using var c = new PdfPreviewController(inspector, interactiveReader: true);
        c.Provide(PreviewInspector.Document());
        await Idle(c);
        var oldHash = c.State.DocumentSha256;
        var frames = new List<PdfPreviewFrame>();
        c.ReaderPageReady += frames.Add;
        c.Provide(PreviewInspector.Document(2));
        await Idle(c);
        Assert.NotEqual(oldHash, c.State.DocumentSha256);
        Assert.All(frames, frame => Assert.Equal(c.State.DocumentSha256, frame.DocumentSha256));
        Assert.True(c.GoToPage(2));
        Assert.Equal(c.State.DocumentSha256, c.State.Frame!.DocumentSha256);
        await Idle(c);
    }
}
