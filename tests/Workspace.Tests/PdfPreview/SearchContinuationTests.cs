using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Client.Pdf;
using Client.Pdf.Preview;
using Xunit;

namespace Workspace.Tests.PdfPreview;

// MODELED inspector/real wire parser only. These are NOT native or browser evidence.
public sealed class SearchContinuationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static Task Idle(PdfPreviewController c) => c.WhenSettled.WaitAsync(Bound);
    private static PreviewInspector Long(int pages, bool lastOnly = true) => new()
    {
        Handler = (q, e) => Task.FromResult(PreviewInspector.Page(
            lastOnly && q.PageIndex != pages - 1 ? WithoutSearch(q) : q, e, pages))
    };
    private static PdfInspectRequest WithoutSearch(PdfInspectRequest q) => new(q.Document, q.PageIndex, q.Budget, null, q.Raster, q.RasterWidth, q.RasterHeight);
    private static int[] Searched(PreviewInspector p) => p.Calls.Where(q => q.Search is not null).Select(q => q.PageIndex).ToArray();

    [Theory] [InlineData(67)] [InlineData(68)] [InlineData(512)]
    public async Task Explicit_batches_reach_final_page_once_and_navigation_does_not_search(int pages)
    {
        var p = Long(pages); await using var c = new PdfPreviewController(p);
        c.Provide(PreviewInspector.Document()); await Idle(c); c.Search("І"); await Idle(c);
        Assert.Equal(PdfPreviewSearchStatus.Partial, c.State.Search.Status);
        Assert.Equal(32, c.State.Search.ScannedPages); Assert.Empty(c.State.Search.Hits);
        while (c.CanContinueSearch)
        {
            int before = Searched(p).Length;
            Assert.True(c.ContinueSearch()); Assert.False(c.ContinueSearch()); await Idle(c);
            Assert.InRange(Searched(p).Length - before, 1, 32);
        }
        Assert.Equal(Enumerable.Range(0, pages), Searched(p));
        Assert.Equal(PdfPreviewSearchStatus.Complete, c.State.Search.Status);
        Assert.Equal(pages, c.State.Search.ScannedPages);
        var hit = Assert.Single(c.State.Search.Hits); Assert.Equal(pages - 1, hit.PageIndex);
        Assert.NotEmpty(hit.NormalizedBoxes); Assert.False(c.ContinueSearch());
        var searched = Searched(p);
        Assert.True(c.SelectHit(0)); await Idle(c); Assert.Equal(pages - 1, c.State.Frame!.PageIndex);
        Assert.True(c.Zoom(125)); await Idle(c); Assert.True(c.Fit(300)); await Idle(c);
        Assert.True(c.GoToPage(1)); await Idle(c); Assert.True(c.SelectHit(0)); await Idle(c);
        Assert.Equal(searched, Searched(p)); Assert.Equal(0, c.State.SelectedHit);
        Assert.All(p.Calls, q => { Assert.Equal(256, q.Budget.SearchHits); Assert.Equal(512, q.Budget.Pages); });
    }

    [Fact] public async Task Partial_navigation_and_same_query_reuse_cursor_without_automatic_scan()
    {
        var p = Long(68); await using var c = new PdfPreviewController(p);
        c.Provide(PreviewInspector.Document()); await Idle(c); c.Search("І"); await Idle(c);
        var searched = Searched(p);
        c.GoToPage(68); await Idle(c); c.Zoom(125); await Idle(c); c.Search("І"); await Idle(c);
        Assert.Equal(searched, Searched(p)); Assert.Equal(32, c.State.Search.ScannedPages);
        Assert.True(c.CanContinueSearch); c.ContinueSearch(); await Idle(c);
        Assert.Equal(Enumerable.Range(0, 64), Searched(p)); Assert.Empty(c.State.Search.Hits);
        c.ContinueSearch(); await Idle(c); Assert.Equal(67, Assert.Single(c.State.Search.Hits).PageIndex);
    }

    [Fact] public async Task Empty_partial_is_not_empty_complete_and_accumulation_has_no_missing_hits()
    {
        var p = Long(68, false); await using var c = new PdfPreviewController(p);
        c.Provide(PreviewInspector.Document()); await Idle(c); c.Search("absent"); await Idle(c);
        Assert.Equal(PdfPreviewSearchStatus.Partial, c.State.Search.Status); Assert.Empty(c.State.Search.Hits);
        c.ContinueSearch(); await Idle(c); c.ContinueSearch(); await Idle(c);
        Assert.Equal(PdfPreviewSearchStatus.Complete, c.State.Search.Status); Assert.Contains("zero matches", c.State.Search.Detail);
        c.Search("І"); await Idle(c); c.ContinueSearch(); await Idle(c); c.ContinueSearch(); await Idle(c);
        Assert.Equal(Enumerable.Range(0, 68), c.State.Search.Hits.Select(h => h.PageIndex));
        Assert.Equal(68, c.State.Search.Hits.Count);
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Held_result_cannot_revive_same_hash_replacement_or_query_A_B_A(int change)
    {
        var p = Long(68); await using var c = new PdfPreviewController(p);
        var document = PreviewInspector.Document(); c.Provide(document); await Idle(c); c.Search("І"); await Idle(c);
        var reached = PreviewInspector.Signal<bool>(); var release = PreviewInspector.Signal<bool>(); bool held = false;
        p.Handler = async (q, e) =>
        {
            if (!held) { held = true; reached.TrySetResult(true); await release.Task; }
            return PreviewInspector.Page(q, e, 68);
        };
        try
        {
            c.ContinueSearch(); await reached.Task.WaitAsync(Bound);
            if (change == 0) c.Provide(PreviewInspector.Document()); // equal bytes/hash, different instance
            else if (change == 1) c.Provide(document); // even same instance is a new provision
            else { c.Search("😀"); c.Search("І"); }
            Assert.False(c.CanContinueSearch); Assert.False(c.ContinueSearch()); Assert.False(c.Retry());
            Assert.Equal(1, p.Active); Assert.Equal(1, c.PendingSelections);
            release.TrySetResult(true); await Idle(c);
            Assert.Equal(1, p.PeakActive);
            if (change < 2) { Assert.Empty(c.State.Search.Hits); Assert.Equal(PdfPreviewSearchStatus.Empty, c.State.Search.Status); }
            else
            {
                Assert.Equal(32, c.State.Search.ScannedPages);
                Assert.Equal(Enumerable.Range(0, 32), c.State.Search.Hits.Select(h => h.PageIndex));
            }
        }
        finally { release.TrySetResult(true); await Idle(c); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Cancel_or_deadline_retains_admission_and_retries_uncommitted_page_only(bool deadline)
    {
        var p = Long(68, false); await using var c = new PdfPreviewController(p, new(DeadlineMs: deadline ? 1000 : 25000));
        c.Provide(PreviewInspector.Document()); await Idle(c); c.Search("І"); await Idle(c);
        var reached = PreviewInspector.Signal<bool>(); var release = PreviewInspector.Signal<bool>(); var expired = PreviewInspector.Signal<bool>(); bool held = false;
        c.Changed += () => { if (c.State.Status == PdfPreviewStatus.TimedOut) expired.TrySetResult(true); };
        p.Handler = async (q, e) =>
        {
            if (q.Search is not null && q.PageIndex == 33 && !held)
            { held = true; reached.TrySetResult(true); await release.Task; }
            return PreviewInspector.Page(q, e, 68);
        };
        try
        {
            c.ContinueSearch(); await reached.Task.WaitAsync(Bound);
            if (deadline) await expired.Task.WaitAsync(Bound); else c.Cancel();
            Assert.True(c.ActualWorkPending); Assert.Equal(1, p.Active);
            Assert.False(c.CanContinueSearch); Assert.False(c.ContinueSearch()); Assert.False(c.Retry());
            Assert.Equal(33, c.State.Search.ScannedPages); Assert.Equal(33, c.State.Search.Hits.Count);
            release.TrySetResult(true); await Idle(c);
            Assert.NotEqual(PdfPreviewSearchStatus.Complete, c.State.Search.Status);
            Assert.True(c.Retry()); await Idle(c); Assert.Equal(65, c.State.Search.ScannedPages);
            Assert.True(c.ContinueSearch()); await Idle(c);
            Assert.Equal(Enumerable.Range(0, 68), c.State.Search.Hits.Select(h => h.PageIndex));
            Assert.Equal(2, Searched(p).Count(i => i == 33)); Assert.Equal(1, p.PeakActive);
        }
        finally { release.TrySetResult(true); await Idle(c); }
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task Failed_page_has_honest_status_and_explicit_resume_without_duplicate_hits(int failure)
    {
        var p = Long(68, false); await using var c = new PdfPreviewController(p);
        c.Provide(PreviewInspector.Document()); await Idle(c); c.Search("І"); await Idle(c);
        bool failed = false;
        p.Handler = (q, e) =>
        {
            if (q.Search is not null && q.PageIndex == 34 && !failed)
            {
                failed = true;
                if (failure == 0) throw new InvalidOperationException("modeled throw");
                if (failure == 4) throw new OperationCanceledException("modeled foreign cancellation");
                if (failure == 3) return Task.FromResult(PreviewInspector.Page(q, e, 68) with { DocumentSha256 = new('0', 64) });
                return Task.FromResult(PreviewInspector.Page(q, e, 68) with
                { Status = failure == 1 ? PdfInspectionStatus.Busy : PdfInspectionStatus.TimedOut, Page = null });
            }
            return Task.FromResult(PreviewInspector.Page(q, e, 68));
        };
        c.ContinueSearch(); await Idle(c);
        Assert.NotEqual(PdfPreviewStatus.Available, c.State.Status); Assert.Null(c.State.Frame);
        Assert.Equal(34, c.State.Search.ScannedPages); Assert.Equal(34, c.State.Search.Hits.Count);
        Assert.NotEqual(PdfPreviewSearchStatus.Complete, c.State.Search.Status); Assert.True(c.CanContinueSearch);
        c.ContinueSearch(); await Idle(c); c.ContinueSearch(); await Idle(c);
        Assert.Equal(Enumerable.Range(0, 68), c.State.Search.Hits.Select(h => h.PageIndex));
        Assert.Equal(PdfPreviewSearchStatus.Complete, c.State.Search.Status);
    }

    [Fact] public async Task Disposal_keeps_actual_task_and_rejects_late_continuation_result()
    {
        var p = Long(68); var c = new PdfPreviewController(p);
        c.Provide(PreviewInspector.Document()); await Idle(c); c.Search("І"); await Idle(c);
        var reached = PreviewInspector.Signal<bool>(); var release = PreviewInspector.Signal<bool>();
        p.Handler = async (q, e) => { reached.TrySetResult(true); await release.Task; return PreviewInspector.Page(q, e, 68); };
        int events = 0; c.Changed += () => events++;
        try
        {
            c.ContinueSearch(); await reached.Task.WaitAsync(Bound); await c.DisposeAsync(); int before = events;
            Assert.True(c.ActualWorkPending); Assert.False(c.ContinueSearch()); Assert.False(c.Retry());
            release.TrySetResult(true); await Idle(c);
            Assert.Equal(before, events); Assert.Equal(PdfPreviewStatus.Disposed, c.State.Status); Assert.Null(c.State.Frame);
        }
        finally { release.TrySetResult(true); await Idle(c); await c.DisposeAsync(); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Aggregate_match_or_highlight_cap_is_terminal_and_navigation_does_not_restart(bool boxes)
    {
        var p = Long(68, false); await using var c = new PdfPreviewController(p,
            boxes ? new(MaxHighlightBoxes: 1) : new(MaxHits: 1));
        c.Provide(PreviewInspector.Document()); await Idle(c); c.Search("І"); await Idle(c);
        Assert.Equal(PdfPreviewSearchStatus.Partial, c.State.Search.Status); Assert.False(c.CanContinueSearch);
        Assert.Contains(boxes ? "Highlight limit" : "Match limit", c.State.Search.Detail);
        Assert.Contains("more specific", c.State.Search.Detail); Assert.False(c.ContinueSearch());
        Assert.InRange(c.State.Search.Hits.Sum(h => h.NormalizedBoxes.Count), 0, boxes ? 1 : 1024);
        int calls = Searched(p).Length; c.Zoom(125); await Idle(c); c.Retry(); await Idle(c);
        Assert.Equal(calls, Searched(p).Length); Assert.False(c.CanContinueSearch);
    }

    [Fact] public async Task Native_256_cap_never_claims_complete_even_on_last_page()
    {
        var p = new PreviewInspector { Handler = (q, e) => Task.FromResult(q.Search is null
            ? PreviewInspector.Page(q, e, 1) : NativeCappedPage(q, e)) };
        await using var c = new PdfPreviewController(p, new(MaxHits: 256));
        c.Provide(PreviewInspector.Document()); await Idle(c); c.Search("x"); await Idle(c);
        Assert.Equal(PdfPreviewSearchStatus.Partial, c.State.Search.Status); Assert.Equal(0, c.State.Search.ScannedPages);
        Assert.Equal(256, c.State.Search.Hits.Count); Assert.False(c.CanContinueSearch);
        Assert.Contains("one page", c.State.Search.Detail); Assert.False(c.ContinueSearch());
    }

    [Fact] public async Task Razor_exposes_continue_coverage_then_final_highlight_and_complete_status()
    {
        var p = Long(68); await using var c = new PdfPreviewController(p);
        c.Provide(PreviewInspector.Document()); await Idle(c); c.Search("І"); await Idle(c);
        string partial = await Render(c);
        Assert.Contains("32 of 68 pages fully checked", partial);
        Assert.Contains("data-testid=\"continue-pdf-search\"", partial);
        Assert.DoesNotContain("No matches.", partial);
        c.ContinueSearch(); await Idle(c); c.ContinueSearch(); await Idle(c); c.SelectHit(0); await Idle(c);
        string complete = await Render(c);
        Assert.Contains("1 match across 68 pages.", complete);
        Assert.Contains("data-testid=\"glyph-highlight\"", complete);
        Assert.Contains("Page 68 of 68", complete);
        Assert.Contains("disabled", complete.Split("data-testid=\"continue-pdf-search\"")[1].Split('>')[0]);
    }
    private static async Task<string> Render(PdfPreviewController c)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => System.Net.WebUtility.HtmlDecode(
            (await renderer.RenderComponentAsync<PdfPreviewPane>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(PdfPreviewPane.Controller)] = c }))).ToHtmlString()));
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new InvalidOperationException("Static render only.");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => InvokeAsync<T>(identifier, args);
    }

    // Synthetic repeated ranges exercise the wire/native-cap contract, not actual PDF matching.
    private static PdfInspectionOutcome NativeCappedPage(PdfInspectRequest q, long epoch)
    {
        string id = Guid.NewGuid().ToString("N"); var runtime = new PdfRuntimeIdentity(new('a', 64), new('b', 64));
        var wire = new PdfWireReply
        {
            Version = 2, RequestId = id, DocumentSha256 = q.Document.Sha256, Epoch = epoch,
            LoaderSha256 = runtime.LoaderSha256, WasmSha256 = runtime.WasmSha256,
            Status = "completed", NativeStatus = 0, EnteredNative = true, PageCount = 1, ActionState = "not-requested",
            FactsJson = "{\"media\":[0,0,100,200],\"crop\":null,\"cropDefaulted\":true,\"effective\":[0,0,100,200],\"rotation\":0,\"displayPoints\":[100,200],\"nativeWidthHeight\":[100,200],\"sourceMapping\":\"unsupported\"}",
            TextJson = "{\"text\":\"x\",\"utf16\":[120],\"characterCount\":1,\"characters\":[{\"index\":0,\"unicode\":120,\"origin\":[10,20],\"box\":[10,20,20,30],\"displayBox\":[10,20,20,30],\"boxStatus\":\"available\",\"font\":\"Model\",\"fontStatus\":\"available\"}],\"sourceMapping\":\"unsupported\"}",
            SearchJson = JsonSerializer.Serialize(Enumerable.Repeat(new { start = 0, count = 1 }, 256)),
            NativeCalls = ["e1_version", "e1_open", "e1_page", "e1_facts", "e1_text", "e1_search", "e1_reset"]
        };
        return PdfWireParser.Parse(wire, q, id, epoch, runtime);
    }
}
