using System.Net;
using Client.Pdf;
using Client.Pdf.Preview;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.PdfPreview;

// CONTRACT: actual Razor static renderer plus synthetic inspector. Native pixels,
// browser focus/events/details visibility are established separately in TEST UI.
public sealed class PreviewPresentationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(4);
    private static async Task<string> Render(PdfPreviewController? controller = null, bool owns = false)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<PdfPreviewPane>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(PdfPreviewPane.Controller)] = controller, [nameof(PdfPreviewPane.OwnsController)] = owns }));
            return WebUtility.HtmlDecode(result.ToHtmlString());
        });
    }
    private static async Task Idle(PdfPreviewController controller) => await controller.WhenSettled.WaitAsync(Bound);
    [Fact]
    public async Task Empty_component_has_plain_heading_no_pdf_and_collapsed_technical_details()
    {
        string html = await Render();
        Assert.Contains("<h2>PDF preview</h2>", html);
        Assert.Contains("No PDF is available.", html);
        Assert.Contains("Enter a search term.", html);
        Assert.Contains("<details data-testid=\"pdf-details\">", html);
        Assert.DoesNotContain("<details open", html);
        Assert.DoesNotContain("No matches", html);
        Assert.Contains("aria-label=\"Go to source\" disabled", html);
    }
    [Fact]
    public async Task Provided_pdf_without_inspector_is_unavailable_not_compile_error_or_zero_matches()
    {
        await using var c = new PdfPreviewController(); c.Provide(PreviewInspector.Document());
        string html = await Render(c);
        Assert.Contains("Preview unavailable.", html);
        Assert.DoesNotContain("compile error", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No matches", html);
        Assert.Null(c.State.Frame); Assert.False(c.ActualWorkPending);
    }
    [Fact]
    public async Task Caller_current_metadata_never_asserts_compilation_freshness_and_is_encoded()
    {
        await using var c = new PdfPreviewController(new PreviewInspector());
        c.Provide(PreviewInspector.Document(), "<script>untrusted title</script>", PdfDisplayProvenance.CallerSaysCurrent); await Idle(c);
        string html = await Render(c);
        Assert.Contains("Page ready.", html); Assert.Contains("Page 1 of 3", html);
        Assert.Contains("CallerSaysCurrent", html);
        Assert.DoesNotContain("PDF up to date", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("compiled successfully", html, StringComparison.OrdinalIgnoreCase);
        // HtmlDecode is for text assertions above; examine actual encoded markup independently.
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        string encoded = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<PdfPreviewPane>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PdfPreviewPane.Controller)] = c }))).ToHtmlString());
        Assert.DoesNotContain("<script>", encoded); Assert.Contains("&lt;script&gt;", encoded);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_zero_and_complete_hits_have_distinct_rendered_search_meaning(bool hits)
    {
        await using var c = new PdfPreviewController(new PreviewInspector()); c.Provide(PreviewInspector.Document()); await Idle(c);
        c.Search(hits ? "І" : "absent"); await Idle(c); string html = await Render(c);
        Assert.Contains(hits ? "3 matches across 3 pages." : "No matches.", html);
        Assert.Equal(hits ? 3 : 0, c.State.Search.Hits.Count);
        Assert.Equal(PdfPreviewSearchStatus.Complete, c.State.Search.Status);
    }
    [Fact]
    public async Task Partial_zero_is_not_no_matches_and_exposes_measured_coverage()
    {
        await using var c = new PdfPreviewController(new PreviewInspector(), new(MaxSearchPages: 1)); c.Provide(PreviewInspector.Document()); await Idle(c);
        c.Search("absent"); await Idle(c); string html = await Render(c);
        Assert.Contains("Search incomplete: 0 matches; 1 of 3 pages fully checked.", html);
        Assert.DoesNotContain("No matches", html); Assert.Equal(PdfPreviewSearchStatus.Partial, c.State.Search.Status);
    }
    [Fact]
    public async Task Failed_search_is_unfinished_and_never_displays_old_frame_as_success()
    {
        var port = new PreviewInspector(); await using var c = new PdfPreviewController(port); c.Provide(PreviewInspector.Document()); await Idle(c);
        port.Handler = (q, epoch) => Task.FromResult(PreviewInspector.Page(q, epoch) with { Status = PdfInspectionStatus.TransportError, Page = null });
        c.Search("І"); await Idle(c); string html = await Render(c);
        Assert.Contains("Search could not finish.", html); Assert.Contains("Could not display the PDF.", html);
        Assert.DoesNotContain("No matches", html); Assert.Null(c.State.Frame);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scanning_and_cancelled_copy_retain_actual_admission_without_zero_claim(bool cancel)
    {
        var port = new PreviewInspector(); await using var c = new PdfPreviewController(port); c.Provide(PreviewInspector.Document()); await Idle(c);
        var reached = PreviewInspector.Signal<bool>(); var release = PreviewInspector.Signal<bool>();
        port.Handler = async (q, epoch) => { reached.TrySetResult(true); await release.Task; return PreviewInspector.Page(q, epoch); };
        try
        {
            c.Search("І"); await reached.Task.WaitAsync(Bound); if (cancel) c.Cancel(); string html = await Render(c);
            Assert.Contains(cancel ? "Search cancelled. Results are incomplete." : "Searching PDF…", html);
            Assert.DoesNotContain("No matches", html); Assert.True(c.ActualWorkPending); Assert.Equal(1, port.Active); Assert.Null(c.State.Frame);
            if (cancel) Assert.Contains("Finishing previous request.", html);
        }
        finally { release.TrySetResult(true); await Idle(c); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_component_disposal_respects_owned_vs_borrowed_controller(bool owns)
    {
        var port = new PreviewInspector(); var c = new PdfPreviewController(port, ownsInspector: true);
        try
        {
            c.Provide(PreviewInspector.Document()); await Idle(c); await Render(c, owns);
            Assert.Equal(owns ? PdfPreviewStatus.Disposed : PdfPreviewStatus.Available, c.State.Status);
            Assert.Equal(owns ? 1 : 0, port.Disposes);
            if (!owns) { Assert.True(c.GoToPage(2)); await Idle(c); Assert.Equal(1, c.State.PageIndex); }
        }
        finally { await c.DisposeAsync(); }
    }
    [Fact]
    public async Task Controller_replacement_disposes_only_previous_owned_instance()
    {
        await using var first = new PdfPreviewController(new PreviewInspector());
        await using var owned = new PdfPreviewController(new PreviewInspector());
        await using var last = new PdfPreviewController(new PreviewInspector());
        first.Provide(PreviewInspector.Document()); owned.Provide(PreviewInspector.Document(2)); last.Provide(PreviewInspector.Document(3));
        await Idle(first); await Idle(owned); await Idle(last);
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoJs()).BuildServiceProvider();
        var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>()); TransitionHost? host = null;
        try
        {
            await renderer.Dispatcher.InvokeAsync(async () => await renderer.RenderComponentAsync<TransitionHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(TransitionHost.Initial)] = first, [nameof(TransitionHost.Capture)] = (Action<TransitionHost>)(value => host = value) })));
            var mounted = host ?? throw new InvalidOperationException("Actual renderer did not mount transition host.");
            await renderer.Dispatcher.InvokeAsync(() => mounted.Switch(owned, true)); Assert.Equal(PdfPreviewStatus.Available, first.State.Status);
            await renderer.Dispatcher.InvokeAsync(() => mounted.Switch(last, false)); Assert.Equal(PdfPreviewStatus.Disposed, owned.State.Status);
            Assert.Equal(PdfPreviewStatus.Available, last.State.Status);
        }
        finally { await renderer.DisposeAsync(); }
        Assert.Equal(PdfPreviewStatus.Available, first.State.Status); Assert.Equal(PdfPreviewStatus.Available, last.State.Status);
    }
    public sealed class TransitionHost : ComponentBase
    {
        [Parameter] public PdfPreviewController? Initial { get; set; }
        [Parameter] public Action<TransitionHost>? Capture { get; set; }
        private PdfPreviewController? _selected;
        private bool _owns;
        protected override void OnInitialized() { _selected = Initial; Capture?.Invoke(this); }
        public void Switch(PdfPreviewController controller, bool owns) { _selected = controller; _owns = owns; StateHasChanged(); }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<PdfPreviewPane>(0); builder.AddAttribute(1, nameof(PdfPreviewPane.Controller), _selected);
            builder.AddAttribute(2, nameof(PdfPreviewPane.OwnsController), _owns); builder.CloseComponent();
        }
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new InvalidOperationException("Static rendering must not pretend to draw.");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
    }
}
