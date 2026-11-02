using System.Net;
using System.Text.Json;
using Client.Compilation.Native;
using Client.Pdf;
using Client.RuntimeAssets;
using Microsoft.JSInterop;

namespace Client.Compilation.Provisioning;

/// <summary>Immutable application-owned runtime bytes, verified once per app
/// lifetime. No user installation, document storage or runtime-selected URLs.
/// The published service worker caches these same three integrity-bound assets.
/// </summary>
public sealed class BundledRuntimeAssets(HttpClient http, IJSRuntime? browser = null) : IAsyncDisposable
{
    public sealed record Asset(string Name, int Length, string Sha256);
    private static readonly JsonDocument Pins = JsonDocument.Parse(typeof(BundledRuntimeAssets).Assembly
        .GetManifestResourceStream("Client.Compilation.Provisioning.bundled-runtime.json")!);
    public static Asset CompilerAsset => Pin("compiler");
    // Promotion is explicit: a ready closure may never fall back to older bytes.
    public static NativeRuntimeProfile CompilerProfile => NativeCompilerXeNavigationPins.Ready
        ? NativeRuntimeProfile.LocalCompilerXeNavigation
        : NativeCompilerNavigationPins.Ready
            ? NativeRuntimeProfile.LocalCompilerNavigation
            : NativeCompilerBibliographyPins.Ready
        ? NativeRuntimeProfile.LocalCompilerBibliography
        : NativeCompilerCompatibilityPins.Ready
            ? NativeRuntimeProfile.LocalCompilerCompatibility : NativeRuntimeProfile.LocalCompilerSuite;
    public static Asset PdfLoaderAsset => Pin("pdfLoader");
    public static Asset PdfWasmAsset => Pin("pdfWasm");
    private static Asset Pin(string role)
    {
        var p = Pins.RootElement.GetProperty(role);
        return new(p.GetProperty("name").GetString()!, p.GetProperty("length").GetInt32(), p.GetProperty("sha256").GetString()!);
    }
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task<NativeRuntimePackage>? _compiler;
    private Task<PdfRuntimePackage>? _pdf;
    private Task? _release;
    private IJSObjectReference? _releaseModule;
    private bool _disposed;
    public Task WhenSettled { get { lock (_gate) return Drain(Task.WhenAll((Task?)_compiler ?? Task.CompletedTask, (Task?)_pdf ?? Task.CompletedTask)); } }
    private static async Task Drain(Task task)
    { try { await task; } catch (Exception e) when (e is not OutOfMemoryException) { } }
    public async Task<InstalledNativeRuntimeRead> ReadCompilerAsync(CancellationToken token = default)
    {
        try
        {
            Task<NativeRuntimePackage> actual;
            lock (_gate)
            {
                if (_disposed) return new(new(RuntimeAssetStatus.Disposed), null, NativeRuntimeIssue.Unavailable);
                if (_compiler is null || _compiler.IsFaulted || _compiler.IsCanceled)
                { _compiler = LoadCompiler(); LocalInstalledRuntimeProviders.Observe(_compiler); }
                actual = _compiler;
            }
            var package = await actual.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            return new(new(RuntimeAssetStatus.Ready), package, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { return new(new(Status(e, token)), null, NativeRuntimeIssue.Unavailable); }
    }
    public async Task<InstalledPdfRuntimeRead> ReadPdfAsync(CancellationToken token = default)
    {
        try
        {
            Task<PdfRuntimePackage> actual;
            lock (_gate)
            {
                if (_disposed) return new(new(RuntimeAssetStatus.Disposed), null);
                if (_pdf is null || _pdf.IsFaulted || _pdf.IsCanceled)
                { _pdf = LoadPdf(); LocalInstalledRuntimeProviders.Observe(_pdf); }
                actual = _pdf;
            }
            var package = await actual.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            return new(new(RuntimeAssetStatus.Ready), package);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return new(new(Status(e, token)), null); }
    }
    public bool Owns(NativeRuntimePackage? package)
    {
        lock (_gate) return !_disposed && _compiler is { IsCompletedSuccessfully: true } task && ReferenceEquals(package, task.Result);
    }
    private RuntimeAssetStatus Status(Exception error, CancellationToken caller) => _disposed ? RuntimeAssetStatus.Disposed
        : error is OperationCanceledException ? caller.IsCancellationRequested ? RuntimeAssetStatus.Cancelled : RuntimeAssetStatus.TimedOut
        : error is HttpRequestException { StatusCode: HttpStatusCode.NotFound } ? RuntimeAssetStatus.Missing
        : error is InvalidDataException or FormatException or JsonException or EndOfStreamException ? RuntimeAssetStatus.Corrupt
        : RuntimeAssetStatus.Unavailable;
    private async Task<NativeRuntimePackage> LoadCompiler()
    {
        await Task.Yield();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        await ReleaseReady(); token.ThrowIfCancellationRequested();
        var bytes = await Read(CompilerAsset, token);
        await using var archive = new ZipRuntimeArchive();
        await archive.PrepareOwnedAsync(bytes, token); bytes = [];
        var pins = LocalRuntimeSelection.ParsePins(archive.Pins);
        var profile = CompilerProfile;
        if (LocalRuntimeSelection.ProfileFor(pins) != profile) throw new InvalidDataException();
        var inputs = new List<NativeResourceInput>(pins.Length);
        foreach (var file in archive.Files)
        {
            token.ThrowIfCancellationRequested();
            inputs.Add(new(file.Name, await file.Read(token)));
        }
        await archive.DisposeAsync(); // Release archive and read buffers before the owned package clone.
        token.ThrowIfCancellationRequested();
        var result = NativeRuntimePackage.Create(inputs, pins,
            LocalRuntimeSelection.LimitsFor(profile), profile);
        inputs.Clear();
        return result.Package ?? throw new InvalidDataException("Bundled compiler integrity failed.");
    }
    private async Task<PdfRuntimePackage> LoadPdf()
    {
        await Task.Yield();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        await ReleaseReady(); deadline.Token.ThrowIfCancellationRequested();
        var loader = await Read(PdfLoaderAsset, deadline.Token);
        var wasm = await Read(PdfWasmAsset, deadline.Token);
        var package = new PdfRuntimePackage(loader, wasm, PdfLoaderAsset.Sha256, PdfWasmAsset.Sha256);
        return package.IsValid ? package : throw new InvalidDataException("Bundled renderer integrity failed.");
    }
    private async Task<byte[]> Read(Asset asset, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "bundled-runtime/" + asset.Name);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != asset.Length) throw new InvalidDataException();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var bytes = new byte[asset.Length];
        await stream.ReadExactlyAsync(bytes, token);
        if (await stream.ReadAsync(new byte[1], token) != 0 || NativeRuntimePackage.Digest(bytes).Hex != asset.Sha256)
            throw new InvalidDataException("Bundled resource integrity failed.");
        return bytes;
    }
    private Task ReleaseReady()
    {
        if (browser is null) return Task.CompletedTask;
        lock (_gate)
        {
            if (_release is null || _release.IsFaulted || _release.IsCanceled) _release = WaitForRelease();
            return _release;
        }
    }
    private async Task WaitForRelease()
    {
        await Task.Yield();
        await using var module = await browser!.InvokeAsync<IJSObjectReference>("import", "./js/bundled-runtime-cache.js");
        lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(BundledRuntimeAssets)); _releaseModule = module; }
        try { await module.InvokeVoidAsync("waitForRelease", CancellationToken.None); }
        finally { lock (_gate) if (ReferenceEquals(_releaseModule, module)) _releaseModule = null; }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _shutdown.Cancel();
        if (_releaseModule is { } module)
            try { await module.InvokeVoidAsync("cancelPending"); } catch (JSException) { } catch (ObjectDisposedException) { }
        try { await WhenSettled; } catch (Exception e) when (e is not OutOfMemoryException) { }
        http.Dispose(); _shutdown.Dispose();
    }
}
