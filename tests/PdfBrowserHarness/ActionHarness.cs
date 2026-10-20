using Client.Pdf;
using Microsoft.JSInterop;
using System.Text.Json;

namespace PdfBrowserHarness;

public static partial class HarnessApi
{
    private static PdfRuntimePackage _newPackage = null!;
    private static readonly Dictionary<string, PdfDocument> _actionDocs = [];
    private static SwitchingProvider _actionProvider = null!;
    private static JsPdfInspector _actionPort = null!;
    private static async Task InitializeActionsAsync(IJSRuntime js, HttpClient http)
    {
        _newPackage = new(await http.GetByteArrayAsync("test-inputs/n10.js.bin"), await http.GetByteArrayAsync("test-inputs/n10.wasm.bin"),
            "040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b", "ea5a9a81d6e200fb59ccaced8ecd510d84999c648acf4a13e4ebbef07b1e2e8b");
        if (!_newPackage.IsValid) throw new InvalidOperationException("Corrected N10 private runtime mismatch.");
        foreach (string name in new[] { "actions", "unit1", "metadata", "locality", "lead-remote", "links128", "links129", "field4096", "field4097" })
            _actionDocs.Add(name, new(await http.GetByteArrayAsync("test-inputs/pdfs/" + name + ".pdf")));
        _actionProvider = new() { Package = _package }; _actionPort = new(js, _actionProvider);
    }
    [JSInvokable]
    public static async Task<object> RunActions(string scenario)
    {
        bool old = scenario.StartsWith("old-", StringComparison.Ordinal);
        _actionProvider.Package = old ? _package : _newPackage;
        string file = scenario switch { "new-zero" => "unit1", "new-metadata" => "metadata", "new-aggregate" => "metadata", "new-locality" => "locality", "new-lead" => "lead-remote", "new-links128" => "links128", "new-links129" => "links129", "new-field4096" => "field4096", "new-field4097" => "field4097", _ => "actions" };
        var document = _actionDocs[file]; bool requested = scenario != "new-default";
        var result = await _actionPort.InspectAsync(new(document, 0, new(OutputBytes: scenario == "new-aggregate" ? 2600 : 16777216), raster: scenario == "new-default", rasterWidth: 240, rasterHeight: 340, inspectActions: requested));
        await _test.InvokeVoidAsync("waitIdle"); var state = await _test.InvokeAsync<WorkerState>("metrics");
        var page = result.Page; byte[] pixels = page?.CopyBgra() ?? [];
        var observation = new
        {
            scenario,
            file,
            requested,
            status = result.Status.ToString(),
            result.Code,
            result.RequestId,
            result.DocumentSha256,
            epoch = result.WorkerEpoch,
            result.Runtime,
            result.NativeStatus,
            result.NativeError,
            result.EnteredNative,
            result.NativeCalls,
            pageCount = page?.PageCount,
            text = page?.Text,
            availability = result.Actions?.Availability.ToString(),
            capabilities = result.Actions?.Capabilities,
            links = result.Actions?.Links,
            rasterSha256 = pixels.Length == 0 ? null : PdfHashes.Of(pixels),
            rasterBase64 = pixels.Length == 0 ? null : Convert.ToBase64String(pixels),
            workers = state
        };
        LastObservation = JsonSerializer.Serialize(observation); await _test.InvokeVoidAsync("show", LastObservation); return observation;
    }
    private sealed class SwitchingProvider : IPdfRuntimeProvider
    {
        public PdfRuntimePackage Package = null!;
        public ValueTask<PdfRuntimePackage?> GetAsync(CancellationToken token) => ValueTask.FromResult<PdfRuntimePackage?>(Package);
    }
}
