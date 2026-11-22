using System.Diagnostics;
using Microsoft.JSInterop;

namespace Client.Compilation.Native;

/// <summary>Keeps large, verified runtime reads from monopolizing the browser thread.</summary>
internal sealed class RuntimePreparation(CancellationToken cancellationToken, IJSObjectReference? browserHash = null)
{
    internal const int ChunkBytes = 512 * 1024;
    internal const int HashBatchBytes = 8 * 1024 * 1024;
    internal const int HashBatchCount = 64;
    private long _sliceStart = Stopwatch.GetTimestamp();

    internal async ValueTask YieldIfNeededAsync()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsBrowser() || Stopwatch.GetElapsedTime(_sliceStart).TotalMilliseconds < 12) return;

        // Task.Yield alone can schedule another continuation before browser input or paint.
        await Task.Delay(1, cancellationToken);
        _sliceStart = Stopwatch.GetTimestamp();
    }

    internal async ValueTask<string[]> DigestBatchAsync(byte[][] resources)
    {
        await YieldIfNeededAsync();
        if (browserHash is null) return resources.Select(bytes => NativeRuntimePackage.Digest(bytes).Hex).ToArray();
        // Own the bounded batch through settlement even if the caller cancels.
        var digests = await browserHash.InvokeAsync<string[]>("sha256Batch", CancellationToken.None, (object)resources);
        cancellationToken.ThrowIfCancellationRequested();
        return digests;
    }
}
