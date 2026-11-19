using System.Text.Json;
using Client.Pdf;
using Client.Pdf.Preview;
using Microsoft.JSInterop;
using Workspace.Tests.PdfPreview;
using Xunit;

namespace Workspace.Tests.Pdf;

public sealed class PdfCompiledInputCapacityTests
{
    private static readonly PdfCompiledInputCapacity Capacity = PdfCompiledInputCapacity.TwelveMiB;
    private static readonly PdfInspectionBudget Expanded = new(InputBytes: Capacity.MaxInputBytes, Pages: 512)
        { CompiledInputCapacity = Capacity };
    private static readonly PdfPreviewPolicy ExpandedPreview = new(InputBytes: Capacity.MaxInputBytes)
        { CompiledInputCapacity = Capacity };
    private static readonly TimeSpan TestBound = TimeSpan.FromSeconds(4);

    [Fact]
    public void Default_and_expanded_budgets_are_distinct_before_parsing_or_cloning()
    {
        Assert.Equal(8388608, new PdfInspectionBudget().InputBytes);
        Assert.Equal(8388608, new PdfPreviewPolicy().InputBytes);
        Assert.Throws<ArgumentException>(() => new PdfDocument(new byte[8388609]));
        Assert.Throws<ArgumentException>(() => new PdfDocument(new byte[Capacity.MaxInputBytes + 1], Capacity));
        Assert.False(new PdfInspectionBudget(InputBytes: Capacity.MaxInputBytes).IsValid);
        Assert.False(new PdfPreviewPolicy(InputBytes: Capacity.MaxInputBytes).IsValid);
        Assert.False((Expanded with { InputBytes = Capacity.MaxInputBytes + 1 }).IsValid);
        Assert.False((ExpandedPreview with { InputBytes = Capacity.MaxInputBytes + 1 }).IsValid);

        var document = new PdfDocument(new byte[8388609], Capacity);
        var ordinary = new PdfDocument([1]);
        Assert.False(new PdfInspectRequest(document, 0, new()).IsValid);
        Assert.False(new PdfInspectRequest(ordinary, 0, Expanded).IsValid);
        Assert.True(new PdfInspectRequest(document, 0, Expanded).IsValid);
        Assert.True(new PdfLayoutRequest(document, 0, Expanded).IsValid);
        Assert.False(new PdfLayoutRequest(document, 0, new()).IsValid);
        Assert.True(new PdfObjectLayoutRequest(document, 0, Expanded).IsValid);
        Assert.False(new PdfObjectLayoutRequest(ordinary, 0, Expanded, IncludeTextLayout: true).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conflicting_or_forged_package_never_imports_the_renderer(bool forge)
    {
        var package = forge
            ? new PdfRuntimePackage("//test"u8.ToArray(), [0, 97, 115, 109, 1, 0, 0, 0],
                Capacity.Renderer.LoaderSha256, Capacity.Renderer.WasmSha256)
            : OrdinaryPackage();
        var js = new Port();
        await using var inspector = new JsPdfInspector(js, new Provider(package));
        var request = new PdfInspectRequest(new PdfDocument(new byte[8388609], Capacity), 0, Expanded, raster: false);
        var result = await inspector.InspectAsync(request);
        Assert.Equal(PdfInspectionStatus.Unavailable, result.Status);
        Assert.Equal(forge ? "RuntimeMismatch" : "CompiledInputRendererMismatch", result.Code);
        Assert.Equal(0, js.Imports);
    }

    [Fact]
    public async Task Ordinary_inspector_serializes_the_exact_legacy_request_members()
    {
        var package = OrdinaryPackage();
        var js = new Port();
        await using var inspector = new JsPdfInspector(js, new Provider(package));
        var document = new PdfDocument("%PDF"u8.ToArray());
        await inspector.InspectAsync(new PdfInspectRequest(document, 0, new(), raster: false, rasterWidth: 1, rasterHeight: 1));
        var actual = Assert.Single(js.Requests);
        string expected = JsonSerializer.Serialize(new
        {
            version = 2, inspectActions = false, inspectLayout = false, requestId = actual.GetProperty("requestId").GetString(),
            documentSha256 = document.Sha256, epoch = 1, pageIndex = 0, search = (string?)null, raster = false, width = 1, height = 1,
            loaderSha256 = package.LoaderSha256, wasmSha256 = package.WasmSha256,
            budget = new { inputBytes = 8388608, pages = 32, textUnits = 100000, searchHits = 256, rasterPixels = 4000000,
                outputBytes = 16777216, liveHandles = 64, deadlineMs = 30000 }
        });
        Assert.Equal(expected, actual.GetRawText());
    }

    [Fact]
    public async Task Preview_only_forwards_the_expanded_budget_for_explicit_owned_documents()
    {
        var bytes = new byte[8388609];
        var document = new PdfDocument(bytes, Capacity);
        var inspector = new PreviewInspector { Handler = (request, epoch) => Task.FromResult(Preview(request, epoch)) };
        await using var legacy = new PdfPreviewController(inspector);
        Assert.False(legacy.Provide(document, provenance: PdfDisplayProvenance.CallerSaysCurrent));
        Assert.Empty(inspector.Calls);

        await using var preview = new PdfPreviewController(inspector, ExpandedPreview);
        Assert.False(preview.ProvideBytes(bytes, provenance: PdfDisplayProvenance.CallerSaysCurrent));
        Assert.Empty(inspector.Calls);
        Assert.True(preview.Provide(document, provenance: PdfDisplayProvenance.CallerSaysCurrent));
        await preview.WhenSettled.WaitAsync(TestBound);
        Assert.Equal(PdfPreviewStatus.Available, preview.State.Status);
        Assert.Equal(142, preview.State.PageCount);
        Assert.NotNull(preview.State.Frame);
        Assert.True(preview.GoToPage(142));
        await preview.WhenSettled.WaitAsync(TestBound);
        Assert.Equal(141, preview.State.Frame!.PageIndex);
        Assert.All(inspector.Calls, call =>
        {
            Assert.Same(Capacity, call.Budget.CompiledInputCapacity);
            Assert.Equal(Capacity.MaxInputBytes, call.Budget.InputBytes);
        });

        Assert.True(preview.ProvideBytes("%PDF"u8.ToArray()));
        await preview.WhenSettled.WaitAsync(TestBound);
        Assert.Equal(PdfPreviewStatus.Available, preview.State.Status);
        Assert.Null(inspector.Calls[^1].Budget.CompiledInputCapacity);
        Assert.Equal(PdfDocument.DefaultInputBytes, inspector.Calls[^1].Budget.InputBytes);
    }

    [Fact]
    public async Task Expanded_preview_rejects_a_completed_page_from_another_tuple()
    {
        var inspector = new PreviewInspector { Handler = (request, epoch) =>
            Task.FromResult(Preview(request, epoch) with { Runtime = new(new('a', 64), new('b', 64)) }) };
        await using var preview = new PdfPreviewController(inspector, ExpandedPreview);
        Assert.True(preview.Provide(new PdfDocument(new byte[8388609], Capacity)));
        await preview.WhenSettled.WaitAsync(TestBound);
        Assert.Equal(PdfPreviewStatus.Error, preview.State.Status);
        Assert.Null(preview.State.Frame);
        Assert.Single(inspector.Calls);
    }

    [PdfCompiledNativeReplayFact]
    public async Task Exact_artifacts_and_actual_native_worker_reply_replay_through_CSharp_inspector()
    {
        string renderer = Environment.GetEnvironmentVariable("PDF_LARGE_RENDERER_DIR")!;
        string replay = Environment.GetEnvironmentVariable("PDF_LARGE_REPLAY_DIR")!;
        var loader = File.ReadAllBytes(Path.Combine(renderer, "e1.js"));
        var wasm = File.ReadAllBytes(Path.Combine(renderer, "e1.wasm"));
        var package = new PdfRuntimePackage(loader, wasm, Capacity.Renderer.LoaderSha256, Capacity.Renderer.WasmSha256);
        Assert.True(package.IsValid);
        var document = new PdfDocument(File.ReadAllBytes(Path.Combine(replay, "synthetic-12-mib.pdf")), Capacity);
        Assert.Equal(Capacity.MaxInputBytes, document.Length);
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(replay, "synthetic-native-replies.json")));
        var js = new Port { Producer = receipt.RootElement };
        await using var inspector = new JsPdfInspector(js, new Provider(package));
        foreach (int pageIndex in new[] { 0, 3 })
        {
            var request = new PdfInspectRequest(document, pageIndex, Expanded, rasterWidth: 128, rasterHeight: 192);
            var result = await inspector.InspectAsync(request);
            Assert.Equal(PdfInspectionStatus.Completed, result.Status);
            Assert.Equal(Capacity.Renderer, result.Runtime);
            var page = Assert.IsType<PdfPageInspection>(result.Page);
            Assert.Equal(pageIndex, page.PageIndex);
            Assert.Equal(4, page.PageCount);
            Assert.NotEmpty(page.Text);
            Assert.Equal(128 * 192 * 4, page.CopyBgra().Length);
        }
        Assert.Equal(1, js.Imports);
        Assert.All(js.Requests, frame =>
        {
            Assert.Equal("compiled-12-mib", frame.GetProperty("inputCapacity").GetString());
            Assert.Equal(Capacity.MaxInputBytes, frame.GetProperty("budget").GetProperty("inputBytes").GetInt32());
        });
    }

    private static PdfInspectionOutcome Preview(PdfInspectRequest request, long epoch)
    {
        var ordinary = new PdfInspectRequest(PreviewInspector.Document(), request.PageIndex,
            request.Budget with { InputBytes = PdfDocument.DefaultInputBytes, CompiledInputCapacity = null },
            request.Search, request.Raster, request.RasterWidth, request.RasterHeight);
        return PreviewInspector.Page(ordinary, epoch, 142) with
            { Runtime = Capacity.Renderer, DocumentSha256 = request.Document.Sha256 };
    }

    private static PdfRuntimePackage OrdinaryPackage()
    {
        byte[] loader = "//test"u8.ToArray(), wasm = [0, 97, 115, 109, 1, 0, 0, 0];
        return new(loader, wasm, PdfHashes.Of(loader), PdfHashes.Of(wasm));
    }

    private sealed class Provider(PdfRuntimePackage package) : IPdfRuntimeProvider
    {
        public ValueTask<PdfRuntimePackage?> GetAsync(CancellationToken token) => ValueTask.FromResult<PdfRuntimePackage?>(package);
    }

    private sealed class Port : IJSRuntime, IJSObjectReference
    {
        public int Imports { get; private set; }
        public List<JsonElement> Requests { get; } = [];
        public JsonElement? Producer { get; init; }

        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => InvokeAsync<T>(name, default, args);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken token, object?[]? args)
        {
            if (name == "import") { Imports++; return ValueTask.FromResult((T)(object)this); }
            if (name == "createPdfPort") return ValueTask.FromResult((T)(object)this);
            if (name == "dispose") return ValueTask.FromResult(default(T)!);
            Assert.Equal("inspect", name);
            using var input = JsonDocument.Parse((string)args![0]!);
            var frame = input.RootElement;
            Requests.Add(frame.Clone());
            PdfWireReply wire = new() { Version = 2, Status = "transport-error" };
            if (Producer is { } replies)
            {
                var page = replies.EnumerateArray().Single(r =>
                    r.GetProperty("request").GetProperty("pageIndex").GetInt32() == frame.GetProperty("pageIndex").GetInt32());
                wire = page.GetProperty("response").Deserialize<PdfWireReply>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.Equal(wire.DocumentSha256, PdfHashes.Of((byte[])args[1]!));
                Assert.Equal(wire.LoaderSha256, PdfHashes.Of((byte[])args[2]!));
                Assert.Equal(wire.WasmSha256, PdfHashes.Of((byte[])args[3]!));
            }
            wire.RequestId = frame.GetProperty("requestId").GetString()!;
            wire.DocumentSha256 = frame.GetProperty("documentSha256").GetString()!;
            wire.Epoch = frame.GetProperty("epoch").GetInt64();
            wire.LoaderSha256 = frame.GetProperty("loaderSha256").GetString()!;
            wire.WasmSha256 = frame.GetProperty("wasmSha256").GetString()!;
            return ValueTask.FromResult((T)(object)wire);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PdfCompiledNativeReplayFactAttribute : FactAttribute
    {
        public PdfCompiledNativeReplayFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PDF_LARGE_RENDERER_DIR"))
                || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PDF_LARGE_REPLAY_DIR")))
                Skip = "Requires the exact candidate renderer and the synthetic native worker replay.";
        }
    }
}
