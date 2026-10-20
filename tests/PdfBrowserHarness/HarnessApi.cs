using System.Text.Json;
using Client.Pdf;
using Microsoft.JSInterop;

namespace PdfBrowserHarness;

public static partial class HarnessApi
{
    private static IJSRuntime _js = null!;
    private static IJSObjectReference _test = null!;
    private static PdfRuntimePackage _package = null!;
    private static PdfDocument _regular = null!, _bold = null!;
    private static JsPdfInspector _main = null!;
    private static Counter _counter = null!;
    public static string LastObservation { get; private set; } = "";
    public static async Task InitializeAsync(IJSRuntime js, HttpClient http)
    {
        _js = js;
        var loader = await http.GetByteArrayAsync("test-inputs/e1.js.bin"); var wasm = await http.GetByteArrayAsync("test-inputs/e1.wasm.bin");
        _package = new(loader, wasm, "21da950bc1a97233b589c0ee9cb77b6303634d39a1a02f9b7bfe2ae1274435af", "10ebf70f88e40fb0af914122f5046c1be5d747e95f0c4de353508602cfe7a55f");
        if (!_package.IsValid) throw new InvalidOperationException("Private accepted runtime mismatch.");
        _regular = new(await http.GetByteArrayAsync("test-inputs/regular.pdf")); _bold = new(await http.GetByteArrayAsync("test-inputs/bold.pdf"));
        await InitializeActionsAsync(js, http);
        _test = await js.InvokeAsync<IJSObjectReference>("import", "./test-host.js");
        _counter = new(js); _main = new(_counter, new Provider(_package));
        LastObservation = JsonSerializer.Serialize(new { provisioned = true, regular = _regular.Sha256, bold = _bold.Sha256 });
    }
    [JSInvokable]
    public static async Task<Observation> Run(string scenario)
    {
        PdfInspectionOutcome result; int beforeImports = _counter.Imports;
        if (scenario is "missing-runtime" or "mismatched-runtime")
        {
            var count = new Counter(_js); var package = scenario == "missing-runtime" ? null : new PdfRuntimePackage(_package.CopyLoader(), _package.CopyWasm(), new('0', 64), _package.WasmSha256);
            await using var isolated = new JsPdfInspector(count, new Provider(package)); result = await isolated.InspectAsync(Request(_regular));
            return await Observe(scenario, result, count.Imports);
        }
        if (scenario is "cancel" or "dispose")
        {
            await _test.InvokeVoidAsync("armHold"); using var ct = new CancellationTokenSource();
            var pending = _main.InspectAsync(Request(_regular), ct.Token);
            await _test.InvokeVoidAsync("waitHeld");
            if (scenario == "cancel") ct.Cancel(); else await _main.DisposeAsync();
            result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); await _test.InvokeVoidAsync("waitIdle");
        }
        else
        {
            var document = scenario == "bold" ? _bold : scenario == "malformed" ? new PdfDocument([37, 80, 68, 70, 45, 120]) : _regular;
            result = await _main.InspectAsync(Request(document));
        }
        return await Observe(scenario, result, _counter.Imports - beforeImports);
    }
    private static PdfInspectRequest Request(PdfDocument document) => new(document, 0, new(), "Україно", true, 240, 340);
    private static async Task<Observation> Observe(string scenario, PdfInspectionOutcome result, int imports)
    {
        await _test.InvokeVoidAsync("waitIdle"); var state = await _test.InvokeAsync<WorkerState>("metrics"); var page = result.Page; var pixels = page?.CopyBgra() ?? [];
        if (page is not null) await _test.InvokeVoidAsync("draw", pixels, page.RasterWidth, page.RasterHeight);
        var value = new Observation(scenario, result.Status.ToString(), result.Code, result.RequestId, result.DocumentSha256, result.WorkerEpoch,
            result.NativeStatus, result.NativeError, result.EnteredNative, result.NativeCalls?.ToArray() ?? [], page?.PageCount, page?.PageIndex,
            page?.Text, page?.Utf16Units.ToArray(), page?.Glyphs.ToArray(), page?.SearchHits.ToArray(), page?.MediaBox, page?.CropBox,
            page?.CropDefaulted, page?.Rotation, page?.NativeDisplaySize, page?.CoordinateUnits, page?.PhysicalSizeInPdfPoints, page?.PhysicalSizeStatus, page?.RasterWidth, page?.RasterHeight,
            pixels.Length == 0 ? null : PdfHashes.Of(pixels), pixels.Length == 0 ? null : Convert.ToBase64String(pixels), imports, state);
        LastObservation = JsonSerializer.Serialize(value); await _test.InvokeVoidAsync("show", LastObservation); return value;
    }
    [JSInvokable]
    public static async Task Finish() { await _actionPort.DisposeAsync(); await _main.DisposeAsync(); await _test.InvokeVoidAsync("waitIdle"); }
    private sealed class Provider(PdfRuntimePackage? package) : IPdfRuntimeProvider
    { public ValueTask<PdfRuntimePackage?> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(package); }
    private sealed class Counter(IJSRuntime inner) : IJSRuntime
    {
        public int Imports;
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args)
        { if (identifier == "import") Imports++; return inner.InvokeAsync<T>(identifier, ct, args); }
    }
    public sealed record Observation(string Scenario, string Status, string Code, string RequestId, string DocumentSha256, long WorkerEpoch,
        int? NativeStatus, uint NativeError, bool? EnteredNative, string[] NativeCalls, int? PageCount, int? PageIndex, string? Text, int[]? Utf16,
        PdfGlyph[]? Glyphs, PdfSearchHit[]? SearchHits, PdfBox? MediaBox, PdfBox? CropBox, bool? CropDefaulted, int? Rotation, PdfPoint? NativeDisplaySize, string? CoordinateUnits, PdfPoint? PhysicalSizeInPdfPoints, string? PhysicalSizeStatus,
        int? RasterWidth, int? RasterHeight, string? RasterSha256, string? RasterBase64, int Imports, WorkerState Workers);
    public sealed class WorkerState
    { public int Created { get; set; } public int Terminated { get; set; } public int Active { get; set; } public int Held { get; set; } public WorkerFrame[] Frames { get; set; } = []; }
    public sealed class WorkerFrame
    { public string RequestId { get; set; } = ""; public string DocumentSha256 { get; set; } = ""; public long Epoch { get; set; } public string LoaderSha256 { get; set; } = ""; public string WasmSha256 { get; set; } = ""; public bool HeldBeforeDelivery { get; set; } }
}
