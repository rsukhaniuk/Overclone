using Microsoft.JSInterop;

namespace Client.Compilation.Download;

/// <summary>Preparation may await module import; the prepared commit MUST be synchronous.
/// A deferred/async JS invocation would leave a stale-project side effect after the guard.</summary>
public interface IPdfDownloadBrowser
{
    ValueTask<IPreparedPdfDownload> PrepareAsync(CancellationToken cancellationToken);
}

public interface IPreparedPdfDownload : IAsyncDisposable
{
    void Download(string filename, byte[] bytes);
}

public sealed class PdfDownloadBrowser(IJSRuntime js) : IPdfDownloadBrowser
{
    public async ValueTask<IPreparedPdfDownload> PrepareAsync(CancellationToken cancellationToken)
    {
        // Do not abandon an in-flight import: retain and dispose even a late module.
        var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/download.js");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (module is not IJSInProcessObjectReference synchronous)
                throw new InvalidOperationException("Synchronous browser download unavailable.");
            return new Prepared(synchronous);
        }
        catch
        {
            await module.DisposeAsync();
            throw;
        }
    }

    private sealed class Prepared(IJSInProcessObjectReference module) : IPreparedPdfDownload
    {
        private bool _disposed;
        public void Download(string filename, byte[] bytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            module.InvokeVoid("downloadPdf", filename, bytes);
        }
        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            return module.DisposeAsync();
        }
    }
}
