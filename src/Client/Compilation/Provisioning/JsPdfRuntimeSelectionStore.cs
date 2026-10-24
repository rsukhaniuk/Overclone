using Client.RuntimeAssets;
using Microsoft.JSInterop;

namespace Client.Compilation.Provisioning;

/// <summary>Separate fixed public PDF resource record, not an executable-capability registry.</summary>
public sealed class JsPdfRuntimeSelectionStore(IJSRuntime js) : IPdfRuntimeSelectionStore
{
    public async Task<string?> ReadAsync()
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/native-runtime-selection.js");
            return await module.InvokeAsync<string?>("readPdfSelection");
        }
        catch (JSException error)
        {
            throw new RuntimeAssetIoException(error.Message switch
            {
                var s when s.Contains("SELECTION_IO:quota", StringComparison.Ordinal) => RuntimeAssetStatus.QuotaExceeded,
                var s when s.Contains("SELECTION_IO:permission", StringComparison.Ordinal) => RuntimeAssetStatus.PermissionDenied,
                var s when s.Contains("SELECTION_IO:busy", StringComparison.Ordinal) => RuntimeAssetStatus.Busy,
                var s when s.Contains("SELECTION_IO:corrupt", StringComparison.Ordinal) => RuntimeAssetStatus.Corrupt,
                _ => RuntimeAssetStatus.StorageFailure
            });
        }
    }
    public async Task WriteAsync(string metadata)
    {
        _ = PdfRuntimeSelection.Decode(metadata);
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/native-runtime-selection.js");
        await module.InvokeVoidAsync("writePdfSelection", metadata);
    }
}
public sealed class PdfRuntimeSetupFactory(IJSRuntime js)
{
    public PdfRuntimeSetup Create() => new(new(new JsRuntimeAssetBackend(js)), new JsPdfRuntimeSelectionStore(js));
}
