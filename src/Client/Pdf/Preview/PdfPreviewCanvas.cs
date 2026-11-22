using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Client.Pdf.Preview;

/// <summary>WASM in-process invalidation is synchronous: a logically newer
/// selection invalidates canvas pixels even while an old async draw is unsettled.
/// One actual draw/import and one replaceable latest frame; no global callbacks.</summary>
public sealed class PdfPreviewCanvas : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly ElementReference _element;
    private readonly bool _reader;
    private readonly DotNetObjectReference<PdfPreviewCanvas> _pointer;
    private readonly object _gate = new();
    private IJSInProcessObjectReference? _module, _channel;
    private PdfPreviewFrame? _pending;
    private (string? Hash, int Count, int Page, int Zoom) _readerView;
    private long _generation, _pumpId;
    private bool _disposed, _running;
    private Task _settled = Task.CompletedTask;
    public PdfPreviewCanvas(IJSRuntime js, ElementReference element, bool reader = false)
    { _js = js; _element = element; _reader = reader; _pointer = DotNetObjectReference.Create(this); }
    public Task WhenSettled { get { lock (_gate) return _settled; } }
    public bool ActualWorkPending { get { lock (_gate) return _running; } }
    public string Status { get; private set; } = "Not drawn";
    public event Action? Changed;
    public event Action<PdfPreviewPointer>? PointRequested;
    public event Action<string, int>? ReaderPageRequested;
    public event Action<long, string, int, int>? ReaderRasterRequested;
    [JSInvokable]
    public Task OnReaderPage(string hash, int page)
    {
        lock (_gate)
            if (!_disposed && _reader && hash == _readerView.Hash && page >= 0 && page < _readerView.Count)
                ReaderPageRequested?.Invoke(hash, page);
        return Task.CompletedTask;
    }
    [JSInvokable]
    public Task OnReaderRaster(long generation, string hash, int page, int pixelWidth)
    {
        lock (_gate)
            if (!_disposed && _reader && generation == _generation && hash == _readerView.Hash && page == _readerView.Page
                && pixelWidth is >= 1 and <= 4096)
                ReaderRasterRequested?.Invoke(generation, hash, page, pixelWidth);
        return Task.CompletedTask;
    }
    internal void ConfigureReader(PdfPreviewState state)
    {
        lock (_gate)
        {
            if (_disposed || !_reader) return;
            var view = (state.DocumentSha256, state.PageCount, state.PageIndex, state.ZoomPercent);
            if (_readerView == view) return;
            _readerView = view;
            SetReaderView();
        }
    }
    internal void CacheReaderPage(PdfPreviewFrame frame)
    {
        lock (_gate)
        {
            if (_disposed || !_reader || _channel is null || frame.Generation != _generation || frame.DocumentSha256 != _readerView.Hash) return;
            _channel.Invoke<bool>("draw", frame.Generation, frame.DocumentSha256, frame.PageIndex,
                frame.Width, frame.Height, frame.CopyRgba(), frame.ReaderPage);
        }
    }
    internal void SetReaderHighlights(long generation, IReadOnlyList<PdfReaderHighlight> highlights)
    {
        lock (_gate)
            if (!_disposed && _reader && generation == _generation) _channel?.InvokeVoid("highlights", generation, highlights);
    }
    private void SetReaderView() => _channel?.InvokeVoid("configure", _readerView.Hash, _readerView.Count, _readerView.Page, _readerView.Zoom);
    [JSInvokable]
    public Task OnPointer(long generation, string hash, int page, double x, double y, bool activate)
    {
        lock (_gate)
        {
            if (!_disposed && generation == _generation && PdfHashes.IsSha(hash) && page is >= 0 and < 512
                && double.IsFinite(x + y) && x is >= 0 and <= 1 && y is >= 0 and <= 1)
                PointRequested?.Invoke(new(generation, hash, page, x, y, activate));
        }
        return Task.CompletedTask;
    }
    public void Select(long generation, PdfPreviewFrame? frame)
    {
        lock (_gate)
        {
            if (_disposed || generation < _generation) return;
            if (frame is not null && (frame.Generation != generation || frame.Width < 1 || frame.Height < 1 || (long)frame.Width * frame.Height > 4000000))
                throw new ArgumentException("Fresh bounded frame required.");
            if (generation > _generation)
            {
                _generation = generation;
                try { _channel?.InvokeVoid("invalidate", generation); } // cannot queue behind a held old draw
                catch (JSException) { Status = "Canvas invalidation error"; _pending = null; Changed?.Invoke(); return; }
            }
            _pending = frame;
            if (frame is null) { Status = "Cleared for latest selection"; return; }
            Status = "Drawing";
            if (!_running) { _running = true; _settled = Pump(++_pumpId); }
        }
    }
    public int MeasureFitWidth()
    {
        lock (_gate) return _disposed || _channel is null ? 360 : Math.Clamp(_channel.Invoke<int>("measure"), 64, 1600);
    }
    internal void SelectReaderText()
    {
        lock (_gate) if (!_disposed && _reader) _channel?.InvokeVoid("selectPageText");
    }
    private async Task Pump(long pumpId)
    {
        await Task.Yield();
        try
        {
            if (_module is null)
            {
                var imported = await _js.InvokeAsync<IJSInProcessObjectReference>("import",
                    _reader ? "./js/pdf-reader.js" : "./js/pdf-preview-canvas.js").ConfigureAwait(false);
                lock (_gate)
                {
                    _module = imported;
                    if (!_disposed)
                    {
                        _channel = imported.Invoke<IJSInProcessObjectReference>(_reader ? "createReader" : "create", _element, _pointer);
                        if (_reader) SetReaderView();
                        _channel.InvokeVoid("invalidate", _generation);
                    }
                }
            }
            while (true)
            {
                PdfPreviewFrame frame; IJSInProcessObjectReference channel;
                lock (_gate)
                {
                    if (_disposed || _pending is null || _channel is null) { if (pumpId == _pumpId) _running = false; break; }
                    frame = _pending; _pending = null; channel = _channel;
                    if (frame.Generation != _generation) continue;
                }
                // Byte cloning occurs only after latest/shape admission. JS draws
                // synchronously once delivered and rechecks monotonic generation.
                bool drawn;
                if (_reader)
                {
                    drawn = channel.Invoke<bool>("reuse", frame.Generation, frame.DocumentSha256, frame.PageIndex, frame.Width, frame.Height)
                        || await channel.InvokeAsync<bool>("draw", frame.Generation, frame.DocumentSha256, frame.PageIndex,
                            frame.Width, frame.Height, frame.CopyRgba(), frame.ReaderPage).ConfigureAwait(false);
                }
                else drawn = await channel.InvokeAsync<bool>("draw", frame.Generation, frame.DocumentSha256, frame.PageIndex,
                    frame.Width, frame.Height, frame.CopyRgba()).ConfigureAwait(false);
                lock (_gate) if (!_disposed && frame.Generation == _generation) { Status = drawn ? "Drawn" : "Rejected stale draw"; Changed?.Invoke(); }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { lock (_gate) if (!_disposed) { Status = "Canvas error: " + error.GetType().Name; Changed?.Invoke(); } }
        finally
        {
            bool release;
            lock (_gate) { if (pumpId == _pumpId) _running = false; release = _disposed && pumpId == _pumpId; }
            if (release) await Release().ConfigureAwait(false);
        }
    }
    public ValueTask DisposeAsync()
    {
        bool release;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true; _pending = null; _generation++;
            Changed = null; PointRequested = null; ReaderPageRequested = null; ReaderRasterRequested = null;
            try { _channel?.InvokeVoid("dispose"); } // prevents any late draw before async release
            catch (JSException) { }
            release = !_running; Status = "Disposed";
        }
        if (release) _ = Release();
        return ValueTask.CompletedTask;
    }
    private async Task Release()
    {
        IJSInProcessObjectReference? channel, module;
        lock (_gate) { channel = _channel; module = _module; _channel = null; _module = null; }
        foreach (var reference in new[] { channel, module })
        {
            if (reference is null) continue;
            try { await reference.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        _pointer.Dispose();
    }
}
