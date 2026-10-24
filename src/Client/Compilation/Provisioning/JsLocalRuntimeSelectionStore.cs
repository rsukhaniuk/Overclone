using Microsoft.JSInterop;

namespace Client.Compilation.Provisioning;

/// <summary>Browser-only public selection metadata, separate from projects and runtime bytes.</summary>
public sealed class JsLocalRuntimeSelectionStore(IJSRuntime js) : ILocalRuntimeSelectionStore
{
    public async Task<string?> ReadAsync()
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/native-runtime-selection.js");
            return await module.InvokeAsync<string?>("readSelection");
        }
        catch (JSException error)
        {
            throw new Client.RuntimeAssets.RuntimeAssetIoException(error.Message switch
            {
                var s when s.Contains("SELECTION_IO:quota", StringComparison.Ordinal) => Client.RuntimeAssets.RuntimeAssetStatus.QuotaExceeded,
                var s when s.Contains("SELECTION_IO:permission", StringComparison.Ordinal) => Client.RuntimeAssets.RuntimeAssetStatus.PermissionDenied,
                var s when s.Contains("SELECTION_IO:busy", StringComparison.Ordinal) => Client.RuntimeAssets.RuntimeAssetStatus.Busy,
                var s when s.Contains("SELECTION_IO:corrupt", StringComparison.Ordinal) => Client.RuntimeAssets.RuntimeAssetStatus.Corrupt,
                _ => Client.RuntimeAssets.RuntimeAssetStatus.StorageFailure
            });
        }
    }
    public async Task WriteAsync(string metadata)
    {
        _ = LocalRuntimeSelection.Decode(metadata);
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/native-runtime-selection.js");
        await module.InvokeVoidAsync("writeSelection", metadata);
    }
}
public sealed class NativeRuntimeSetupFactory(IJSRuntime js)
{
    public NativeRuntimeSetup Create() => new(new(new Client.RuntimeAssets.JsRuntimeAssetBackend(js)), new JsLocalRuntimeSelectionStore(js));
}
