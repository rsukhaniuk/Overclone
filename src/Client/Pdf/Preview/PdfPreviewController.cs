namespace Client.Pdf.Preview;

/// <summary>Reusable, unregistered preview orchestration. One ACTUAL inspect task
/// and one replaceable latest selection. Cancellation does not release admission.</summary>
public sealed class PdfPreviewController : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IPdfInspector? _inspector;
    private readonly bool _ownsInspector;
    private readonly PdfPreviewPolicy _policy;
    private PdfDocument? _document;
    private PdfPreviewState _state = PdfPreviewState.Empty;
    private Selection? _pending;
    private CancellationTokenSource? _active;
    private Task _pump = Task.CompletedTask;
    private bool _running, _disposed, _backgroundActive;
    private int _inspectionPauses;
    private long _generation, _lastNativeEpoch, _provenanceVersion;
    private PdfDocument? _cachedDocument;
    private PdfPreviewSearch _cachedSearch = PdfPreviewSearch.Empty;
    private readonly Dictionary<int, PdfPreviewState> _readerPages = [];
    private readonly HashSet<int> _prefetchAttempted = [];
    public event Action<PdfPreviewFrame>? ReaderPageReady;
    private readonly int _preferredFitWidth;
    private int _readerRequestedWidth;
    public PdfPreviewController(IPdfInspector? inspector = null, PdfPreviewPolicy? policy = null, bool ownsInspector = false,
        int preferredFitWidth = 360, bool interactiveReader = false)
    {
        _policy = policy ?? new(); if (!_policy.IsValid) throw new ArgumentException("Invalid preview policy.", nameof(policy));
        _inspector = inspector; _ownsInspector = ownsInspector;
        _preferredFitWidth = Math.Clamp(preferredFitWidth, 360, 1600);
        InteractiveReader = interactiveReader;
    }
    public bool InteractiveReader { get; }
    public event Action? Changed;
    public PdfPreviewState State { get { lock (_gate) return _state; } }
    internal long ProvenanceVersion { get { lock (_gate) return _provenanceVersion; } }
    public Task WhenSettled { get { lock (_gate) return _pump; } }
    public bool ActualWorkPending { get { lock (_gate) return _running; } }
    public int PendingSelections { get { lock (_gate) return _pending is null ? 0 : 1; } }
    public bool CanContinueSearch { get { lock (_gate) return !_disposed && !_running && _inspector is not null
        && ReferenceEquals(_cachedDocument, _document) && _cachedSearch.Query == _state.Search.Query && _cachedSearch.CanContinue; } }

    // The current PDF and review share an inspector. Let admitted foreground
    // work settle and hold new preview requests until explicit review releases it.
    internal IDisposable PauseInspection()
    {
        lock (_gate)
        {
            _inspectionPauses++;
            if (_backgroundActive) Stop(_active);
            return new InspectionPause(this);
        }
    }

    private sealed class InspectionPause(PdfPreviewController owner) : IDisposable
    {
        private PdfPreviewController? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } preview) return;
            lock (preview._gate)
            {
                preview._inspectionPauses--;
                if (preview._pending is not null || preview._state.Status == PdfPreviewStatus.Available)
                    preview.StartPumpLocked();
            }
        }
    }

    private void StartPumpLocked()
    {
        if (_disposed || _running || _inspectionPauses != 0 || _inspector is null) return;
        _running = true;
        _pump = Pump();
    }
    private sealed record Selection(long Generation, PdfDocument Document, int Page, int Zoom, int FitWidth, string Query, int Hit, bool Scan,
        int ResumePage, (int Width, int Height)? RefineRaster = null, bool Background = false);

    public bool ProvideBytes(byte[]? bytes, string displayName = "", PdfDisplayProvenance provenance = PdfDisplayProvenance.ProvidedPdf)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (bytes is null || bytes.Length < 1 || bytes.Length > Math.Min(_policy.InputBytes, PdfDocument.DefaultInputBytes) || displayName is null || displayName.Length > 256
                || !PdfHashes.ValidUtf16(displayName) || !Enum.IsDefined(provenance)) return Invalid("PDF input/metadata budget rejected before copy.");
            return Provide(new PdfDocument(bytes), displayName, provenance);
        }
    }
    public bool Provide(PdfDocument? document, string displayName = "", PdfDisplayProvenance provenance = PdfDisplayProvenance.ProvidedPdf)
    {
        if (document is null || document.Length > _policy.InputBytes
            || document.CompiledInputCapacity is { } capacity && !ReferenceEquals(capacity, _policy.CompiledInputCapacity)
            || displayName is null || displayName.Length > 256 || !PdfHashes.ValidUtf16(displayName) || !Enum.IsDefined(provenance))
            return Invalid("Invalid owned document or display metadata.");
        lock (_gate)
        {
            if (_disposed) return false;
            int resumePage = InteractiveReader && _document is not null ? _state.PageIndex : -1;
            int zoom = InteractiveReader ? _state.ZoomPercent : 100;
            if (InteractiveReader && _document?.Sha256 == document.Sha256)
            {
                SetProvenance(provenance);
                return true;
            }
            _document = document; _cachedDocument = null; _cachedSearch = PdfPreviewSearch.Empty;
            _readerPages.Clear();
            _state = PdfPreviewState.Empty with { DisplayName = displayName, Provenance = provenance, DocumentSha256 = document.Sha256, Generation = _generation };
            return SubmitLocked(0, zoom, _state.FitWidth, "", -1, resumePage: resumePage);
        }
    }
    internal void SetProvenance(PdfDisplayProvenance provenance)
    {
        lock (_gate)
        {
            if (_disposed || _state.Provenance == provenance) return;
            _provenanceVersion++;
            _state = _state with { Provenance = provenance };
            NotifyLocked();
        }
    }
    public bool GoToPage(int oneBasedPage)
    {
        lock (_gate)
        {
            if (_disposed || _document is null || _state.PageCount < 1 || oneBasedPage < 1 || oneBasedPage > _state.PageCount) return false;
            return SubmitLocked(oneBasedPage - 1, _state.ZoomPercent, _state.FitWidth, _state.Search.Query, -1);
        }
    }
    public bool Zoom(int percent)
    {
        lock (_gate)
        {
            if (_disposed || _document is null || percent is < 25 or > 300) return false;
            if (InteractiveReader)
            {
                if (_state.ZoomPercent != percent) { _state = _state with { ZoomPercent = percent }; NotifyLocked(); }
                return true;
            }
            return SubmitLocked(_state.PageIndex, percent, _state.FitWidth, _state.Search.Query, _state.SelectedHit);
        }
    }
    public bool Fit(int width)
    {
        if (InteractiveReader) return Zoom(100);
        lock (_gate)
        {
            if (_disposed || _document is null || width is < 64 or > 1600) return false;
            return SubmitLocked(_state.PageIndex, 100, width, _state.Search.Query, _state.SelectedHit);
        }
    }

    internal void RefineReaderRaster(long generation, string hash, int page, int requestedWidth)
    {
        lock (_gate)
        {
            if (_disposed || !InteractiveReader || _document is null || _inspector is null || _generation != generation
                || _document.Sha256 != hash || _state.PageIndex != page || requestedWidth is < 1 or > 4096) return;
            _readerRequestedWidth = requestedWidth;
            if ((_running && !_backgroundActive) || _state.Status != PdfPreviewStatus.Available || _state.Frame is not { } frame
                || _state.PageGeometry is not { } geometry
                || PdfPreviewPixels.ReaderDimensions(geometry.Display, requestedWidth, _policy) is not { } size
                || size.Width < frame.Width + 16) return;
            // A resolution change keeps the page identity and the selectable DOM.
            // Page/search/document changes can still cancel the shared inspector.
            _pending = new(generation, _document, page, _state.ZoomPercent, _state.FitWidth, _state.Search.Query,
                _state.SelectedHit, false, -1, size);
            if (_backgroundActive) Stop(_active);
            StartPumpLocked();
        }
    }
    public bool Search(string? query)
    {
        if (query is null || query.Length > 255 || query.Contains('\0') || !PdfHashes.ValidUtf16(query)) return Invalid("Invalid search query; maximum255 UTF16 units.");
        lock (_gate)
        {
            if (_disposed || _document is null) return false;
            if (query != _state.Search.Query) { _cachedDocument = null; _cachedSearch = PdfPreviewSearch.Empty; }
            return SubmitLocked(_state.PageIndex, _state.ZoomPercent, _state.FitWidth, query, -1,
                query.Length != 0 && _cachedDocument is null);
        }
    }
    public bool ContinueSearch()
    {
        lock (_gate)
        {
            if (!CanContinueSearch) return false;
            return SubmitLocked(_state.PageIndex, _state.ZoomPercent, _state.FitWidth, _state.Search.Query, _state.SelectedHit, true);
        }
    }
    public bool SelectHit(int index)
    {
        lock (_gate)
        {
            if (_disposed || _document is null || index < 0 || index >= _state.Search.Hits.Count) return false;
            return SubmitLocked(_state.Search.Hits[index].PageIndex, _state.ZoomPercent, _state.FitWidth, _state.Search.Query, index);
        }
    }
    public bool Retry()
    {
        lock (_gate)
        {
            if (_disposed || _document is null || _running) return false;
            return SubmitLocked(_state.PageIndex, _state.ZoomPercent, _state.FitWidth, _state.Search.Query, _state.SelectedHit,
                _state.Search.Query.Length != 0 && (_cachedDocument is null || _cachedSearch.CanContinue));
        }
    }
    public void Cancel()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _generation++; _pending = null; Stop(_active);
            _state = _state with { Generation = _generation, Status = PdfPreviewStatus.Cancelled, Detail = "Cancelled; actual foreign work retains admission until settlement.", Frame = null,
                Search = _state.Search.Status == PdfPreviewSearchStatus.Scanning
                    ? _state.Search with { Status = PdfPreviewSearchStatus.Cancelled, Detail = "Search cancelled. Continue when ready, or Retry." } : _state.Search };
            NotifyLocked();
        }
    }
    private bool Invalid(string detail)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            _generation++; _pending = null; Stop(_active);
            _state = _state with { Generation = _generation, Status = PdfPreviewStatus.Error, Detail = detail, Frame = null }; NotifyLocked(); return false;
        }
    }
    private bool SubmitLocked(int page, int zoom, int width, string query, int hit, bool scan = false, int resumePage = -1)
    {
        if (_document is null || _generation >= 9007199254740990) return false;
        _generation++; Stop(_active); _prefetchAttempted.Clear();
        var search = query.Length == 0 ? PdfPreviewSearch.Empty : ReferenceEquals(_cachedDocument, _document) && _cachedSearch.Query == query ? _cachedSearch
            : new(PdfPreviewSearchStatus.Partial, query, 0, _state.PageCount, Array.AsReadOnly(Array.Empty<PdfPreviewHit>()), "Search not finished. Use Retry to search.") { CanContinue = true };
        if (query.Length != 0 && _cachedDocument is null) { _cachedDocument = _document; _cachedSearch = search; }
        if (InteractiveReader && !scan && resumePage < 0 && _readerPages.TryGetValue(page, out var cached))
        {
            _pending = null;
            _state = cached with { Generation = _generation, Frame = cached.Frame?.ForGeneration(_generation),
                Provenance = _state.Provenance, ZoomPercent = zoom, Search = search, SelectedHit = hit };
            StartPumpLocked();
            NotifyLocked();
            return true;
        }
        _pending = new(_generation, _document, page, zoom, width, query, hit, scan, resumePage);
        if (scan) search = search with { Status = PdfPreviewSearchStatus.Scanning, Detail = "Searching next pages." };
        _state = _state with { Generation = _generation, Status = _inspector is null ? PdfPreviewStatus.Unavailable : PdfPreviewStatus.Loading,
            Detail = _inspector is null ? "Unavailable: caller has not supplied an inspector/runtime." : "Loading latest selection.", PageIndex = resumePage >= 0 ? resumePage : page, ZoomPercent = zoom, FitWidth = width,
            Text = "", Utf16Units = Array.AsReadOnly(Array.Empty<int>()), Glyphs = Array.AsReadOnly(Array.Empty<PdfGlyph>()), Frame = null, PageGeometry = null, Search = search, SelectedHit = hit, NativeRequestId = null, NativeEpoch = null, Runtime = null };
        if (_inspector is null) _pending = null;
        else StartPumpLocked();
        NotifyLocked(); return true;
    }
    private async Task Pump()
    {
        await Task.Yield();
        while (true)
        {
            Selection selection; CancellationTokenSource stop;
            lock (_gate)
            {
                if (_inspectionPauses != 0)
                { _running = false; _backgroundActive = false; if (!_disposed) NotifyLocked(); return; }
                if (!_disposed && _pending is null && InteractiveReader && _document is not null
                    && _state.Status == PdfPreviewStatus.Available && _state.Search.Query.Length == 0)
                {
                    // One admission for foreground and speculative work. Navigation
                    // cancels speculation and the latest request always goes first.
                    foreach (int offset in new[] { 1, -1, 2, 3 })
                    {
                        int next = _state.PageIndex + offset;
                        if (next < 0 || next >= _state.PageCount || _readerPages.ContainsKey(next) || !_prefetchAttempted.Add(next)) continue;
                        _pending = new(_generation, _document, next, 100, 480, "", -1, false, -1, Background: true);
                        break;
                    }
                }
                if (_disposed || _pending is null) { _running = false; _backgroundActive = false; if (!_disposed) NotifyLocked(); return; }
                selection = _pending; _pending = null; stop = new(); _active = stop; _backgroundActive = selection.Background;
            }
            using var deadline = new CancellationTokenSource(_policy.DeadlineMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, deadline.Token);
            using var registration = deadline.Token.Register(() =>
            {
                Stop(linked); // cancel before publishing; linked outlives this registration
                if (!selection.Background && selection.RefineRaster is null) Publish(selection, s => s with { Status = PdfPreviewStatus.TimedOut, Detail = "Selection deadline reached; actual admission retained until settlement.", Frame = null,
                    Search = s.Search.Status != PdfPreviewSearchStatus.Scanning ? s.Search : s.Search with { Status = PdfPreviewSearchStatus.Partial, Detail = "Deadline: scan unfinished. Continue when ready, or Retry." } });
            });
            try { await Run(selection, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                lock (_gate)
                    if (selection.Background && Current(selection) && stop.IsCancellationRequested && !deadline.IsCancellationRequested)
                        _prefetchAttempted.Remove(selection.Page); // retry after a same-page resolution upgrade
            }
            catch (OperationCanceledException) { if (!selection.Background && selection.RefineRaster is null) Publish(selection, s => s with { Status = PdfPreviewStatus.Error, Detail = "ForeignCancellation: inspector cancelled without caller/deadline cancellation.", Frame = null, Search = ScanError(s.Search, "Foreign cancellation: scan incomplete.") }); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (!selection.Background && selection.RefineRaster is null) Publish(selection, s => s with { Status = PdfPreviewStatus.Error, Detail = "Inspector/drawing data error: " + error.GetType().Name, Frame = null, Search = ScanError(s.Search, "Inspection failed; not zero matches.") }); }
            finally { lock (_gate) { if (ReferenceEquals(_active, stop)) _active = null; stop.Dispose(); } }
        }
    }
    private async Task Run(Selection selection, CancellationToken token)
    {
        PdfActionInspection? readerActions = null;
        var backgroundState = State with { PageIndex = selection.Page, Frame = null };
        void Update(Func<PdfPreviewState, PdfPreviewState> update)
        {
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (!Current(selection)) return;
                if (selection.Background)
                {
                    backgroundState = update(backgroundState);
                    if (backgroundState.Status == PdfPreviewStatus.Available && backgroundState.Frame is { } frame)
                    {
                        RememberReaderPage(backgroundState);
                        ReaderPageReady?.Invoke(frame);
                    }
                }
                else { _state = update(_state); NotifyLocked(); }
            }
        }
        async Task<PdfPageInspection?> Inspect(int page, bool raster, int width = 1, int height = 1, bool searchPage = false)
        {
            token.ThrowIfCancellationRequested();
            var capacity = selection.Document.CompiledInputCapacity;
            int inputBytes = Math.Min(_policy.InputBytes, capacity?.MaxInputBytes ?? PdfDocument.DefaultInputBytes);
            var budget = new PdfInspectionBudget(InputBytes: inputBytes, Pages: _policy.MaxDocumentPages, SearchHits: 256, RasterPixels: _policy.MaxPixels,
                OutputBytes: 16777216, DeadlineMs: _policy.DeadlineMs) { CompiledInputCapacity = capacity };
            var request = new PdfInspectRequest(selection.Document, page, budget, searchPage ? selection.Query : null, raster, width, height,
                inspectActions: raster && InteractiveReader && selection.RefineRaster is null);
            // Await the ACTUAL task, never a cancelled WaitAsync proxy. A
            // noncooperative task occupies this single admission after disposal.
            var outcome = await _inspector!.InspectAsync(request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!Current(selection)) throw new OperationCanceledException(token);
                if (outcome.DocumentSha256 != selection.Document.Sha256 || !Guid.TryParseExact(outcome.RequestId, "N", out _) || outcome.WorkerEpoch <= _lastNativeEpoch)
                    throw new InvalidOperationException("Foreign hash/request/epoch.");
                _lastNativeEpoch = outcome.WorkerEpoch;
            }
            if (outcome.Status != PdfInspectionStatus.Completed)
            {
                if (selection.RefineRaster is not null) return null;
                var status = outcome.Status switch { PdfInspectionStatus.Unavailable => PdfPreviewStatus.Unavailable, PdfInspectionStatus.Busy => PdfPreviewStatus.Busy,
                    PdfInspectionStatus.Cancelled => PdfPreviewStatus.Error, PdfInspectionStatus.TimedOut => PdfPreviewStatus.TimedOut, _ => PdfPreviewStatus.Error };
                Update(s => s with { Status = status, Detail = outcome.Status + ": " + outcome.Code + ". Retry is explicit; no polling.", Frame = null,
                    Search = ScanError(s.Search, "Scan failed/unfinished, not zero matches.") });
                return null;
            }
            if (outcome.Page is not { } result || result.PageIndex != page || result.PageCount < 1 || result.PageCount > _policy.MaxDocumentPages
                || capacity is not null && outcome.Runtime != capacity.Renderer || outcome.NativeStatus != 0 || outcome.EnteredNative != true
                || (raster && (result.RasterWidth != width || result.RasterHeight != height))) throw new InvalidOperationException("Foreign page/status/dimensions.");
            if (selection.RefineRaster is null)
                Update(s => s with { PageCount = result.PageCount, NativeRequestId = outcome.RequestId, NativeEpoch = outcome.WorkerEpoch, Runtime = outcome.Runtime });
            if (raster && InteractiveReader) readerActions = outcome.Actions;
            return result;
        }
        if (selection.RefineRaster is { } refinement)
        {
            var refined = await Inspect(selection.Page, true, refinement.Width, refinement.Height).ConfigureAwait(false);
            if (refined is null) return;
            var pixels = PdfPreviewPixels.ToRgba(refined.CopyBgra(), refinement.Width, refinement.Height, _policy);
            Update(s => RememberReaderPage(s with
            {
                Frame = new(selection.Generation, selection.Document.Sha256, selection.Page, refinement.Width, refinement.Height, pixels)
                { ReaderPage = s.Frame?.ReaderPage }
            }));
            return;
        }
        PdfPreviewSearch search;
        lock (_gate) search = ReferenceEquals(_cachedDocument, selection.Document) && _cachedSearch.Query == selection.Query ? _cachedSearch : PdfPreviewSearch.Empty;
        bool scan = selection.Scan && selection.Query.Length != 0 && search.CanContinue;
        bool reuseFirst = scan && selection.Page == search.ScannedPages;
        var first = await Inspect(selection.Page, false, searchPage: reuseFirst).ConfigureAwait(false); if (first is null) return;
        int displayPage = selection.Page;
        if (selection.ResumePage >= 0)
        {
            displayPage = Math.Min(selection.ResumePage, first.PageCount - 1);
            Update(s => s with { PageIndex = displayPage });
            if (displayPage != selection.Page)
            {
                first = await Inspect(displayPage, false).ConfigureAwait(false);
                if (first is null) return;
            }
        }
        if (selection.Query.Length != 0 && search.TotalPages > 0 && search.TotalPages != first.PageCount)
            throw new InvalidOperationException("Page count changed for this document.");
        if (scan)
        {
            var hits = search.Hits.ToList(); int scanned = search.ScannedPages;
            int boxCount = hits.Sum(h => h.NormalizedBoxes.Count);
            int end = Math.Min(first.PageCount, scanned + _policy.MaxSearchPages);
            for (int index = scanned; index < end; index++)
            {
                var page = reuseFirst && index == selection.Page ? first : await Inspect(index, false, searchPage: true).ConfigureAwait(false);
                if (page is null) return;
                if (page.PageCount != first.PageCount) throw new InvalidOperationException("Page count changed during search.");
                string? limit = null;
                foreach (var hit in page.SearchHits)
                {
                    token.ThrowIfCancellationRequested();
                    if (hits.Count == _policy.MaxHits) { limit = "Match limit reached. Use a more specific search term."; break; }
                    var mapped = PdfPreviewPixels.Hit(page, hit, Math.Max(0, _policy.MaxHighlightBoxes - boxCount));
                    boxCount += mapped.NormalizedBoxes.Count; hits.Add(mapped);
                    // Missing native geometry is not a missing match. Only our storage
                    // budget makes continuation terminal; do not guess missing boxes.
                    if (hit.CharacterCount > mapped.NormalizedBoxes.Count && boxCount >= _policy.MaxHighlightBoxes)
                        limit = "Highlight limit reached. Use a more specific search term.";
                }
                if (page.SearchHits.Count >= 256) limit = "Too many matches on one page. Use a more specific search term.";
                if (limit is null) scanned++; // never move the cursor past an incomplete page
                bool complete = scanned == first.PageCount && limit is null;
                if (!complete && hits.Count >= _policy.MaxHits && limit is null)
                    limit = "Match limit reached. Use a more specific search term.";
                search = new(complete ? PdfPreviewSearchStatus.Complete : PdfPreviewSearchStatus.Partial, selection.Query, scanned, first.PageCount, Array.AsReadOnly(hits.ToArray()),
                    complete ? hits.Count == 0 ? "Complete scan: zero matches." : "Complete document scan."
                        : "Incomplete: " + (limit ?? "More pages remain. Choose Continue search."))
                    { CanContinue = !complete && limit is null };
                // Commit whole-page checkpoints only while this exact selection still
                // owns authority. A failed/cancelled page is retried, never skipped.
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Current(selection)) throw new OperationCanceledException(token);
                    _cachedDocument = selection.Document; _cachedSearch = search;
                    _state = _state with { Search = index + 1 < end && search.CanContinue
                        ? search with { Status = PdfPreviewSearchStatus.Scanning } : search };
                    NotifyLocked();
                }
                if (!search.CanContinue) break;
            }
        }
        if (selection.Query.Length == 0) search = PdfPreviewSearch.Empty;
        Update(s => s with { Search = search });
        var rasterZoom = InteractiveReader ? 100 : selection.Zoom;
        var dimensions = rasterZoom == 100 && _preferredFitWidth > selection.FitWidth
            ? PdfPreviewPixels.Dimensions(first.NativeDisplaySize, _preferredFitWidth, rasterZoom, _policy)
            : null;
        dimensions ??= PdfPreviewPixels.Dimensions(first.NativeDisplaySize, selection.FitWidth, rasterZoom, _policy);
        if (InteractiveReader) dimensions = PdfPreviewPixels.ReaderDimensions(first.NativeDisplaySize,
            selection.Background ? 480 : Math.Max(_preferredFitWidth, _readerRequestedWidth), _policy);
        if (dimensions is null) { Update(s => s with { Status = PdfPreviewStatus.Error, Detail = "Raster dimensions exceed configured pixel/output budget.", Frame = null }); return; }
        var rendered = await Inspect(displayPage, true, dimensions.Value.Width, dimensions.Value.Height).ConfigureAwait(false); if (rendered is null) return;
        token.ThrowIfCancellationRequested();
        var rgba = PdfPreviewPixels.ToRgba(rendered.CopyBgra(), rendered.RasterWidth, rendered.RasterHeight, _policy);
        var frame = new PdfPreviewFrame(selection.Generation, selection.Document.Sha256, displayPage, rendered.RasterWidth, rendered.RasterHeight, rgba)
        { ReaderPage = InteractiveReader ? PdfReaderPage.From(rendered, readerActions) : null };
        Update(s =>
        {
            var ready = s with { Status = PdfPreviewStatus.Available, Detail = "Native page available. Provided PDF provenance remains caller metadata.", Frame = frame,
                Text = rendered.Text, Utf16Units = rendered.Utf16Units, Glyphs = rendered.Glyphs, Search = search,
                PageGeometry = new(rendered.MediaBox, rendered.EffectiveBox, rendered.Rotation / 90, rendered.NativeDisplaySize) };
            return InteractiveReader && !selection.Background ? RememberReaderPage(ready) : ready;
        });
    }
    private PdfPreviewState RememberReaderPage(PdfPreviewState state)
    {
        _readerPages[state.PageIndex] = state;
        while (_readerPages.Count > 6 || _readerPages.Values.Sum(page => (long)page.Frame!.Width * page.Frame.Height * 4) > 2L * _policy.MaxRgbaBytes)
            _readerPages.Remove(_readerPages.Keys.MaxBy(index => Math.Abs(index - _state.PageIndex)));
        return state;
    }
    private static PdfPreviewSearch ScanError(PdfPreviewSearch search, string detail) => search.Status == PdfPreviewSearchStatus.Scanning
        ? search with { Status = PdfPreviewSearchStatus.Error, Detail = detail } : search;
    private bool Current(Selection selection) => !_disposed && _generation == selection.Generation && ReferenceEquals(_document, selection.Document);
    private void Publish(Selection selection, Func<PdfPreviewState, PdfPreviewState> update)
    { lock (_gate) if (Current(selection)) { _state = update(_state); NotifyLocked(); } }
    private void NotifyLocked() { if (!_disposed) Changed?.Invoke(); }
    private static void Stop(CancellationTokenSource? source) { if (source is not null) { try { source.Cancel(); } catch (AggregateException) { } } }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true; _pending = null; _document = null; _cachedDocument = null; _cachedSearch = PdfPreviewSearch.Empty; _readerPages.Clear(); Changed = null; ReaderPageReady = null; Stop(_active);
            _state = PdfPreviewState.Empty with { Generation = ++_generation, Status = PdfPreviewStatus.Disposed, Detail = "Disposed; late foreign settlement cannot publish." };
        }
        if (_ownsInspector && _inspector is not null) _ = DisposeOwnedAsync();
        return ValueTask.CompletedTask;
    }
    private async Task DisposeOwnedAsync()
    {
        try { await _inspector!.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
}
