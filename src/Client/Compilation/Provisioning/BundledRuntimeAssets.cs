using System.Net;
using System.Text.Json;
using Client.Compilation.Native;
using Client.Pdf;
using Client.RuntimeAssets;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
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
    public static NativeRuntimeProfile CompilerProfile => CompilerAsset switch
    {
        { Length: NativeCompilerLuaFontPins.ArchiveLength, Sha256: NativeCompilerLuaFontPins.ArchiveSha256 }
            => NativeRuntimeProfile.LocalCompilerPageLabels,
        { Length: NativeCompilerCompatibleFontPins.ArchiveLength, Sha256: NativeCompilerCompatibleFontPins.ArchiveSha256 }
            => NativeRuntimeProfile.LocalCompilerPageLabels,
        { Length: NativeCompilerTermesPins.ArchiveLength, Sha256: NativeCompilerTermesPins.ArchiveSha256 }
            => NativeRuntimeProfile.LocalCompilerPageLabels,
        { Length: NativeCompilerPageLabelPins.ArchiveLength, Sha256: NativeCompilerPageLabelPins.ArchiveSha256 }
            => NativeRuntimeProfile.LocalCompilerPageLabels,
        { Length: NativeCompilerPdfOriginPins.ArchiveLength, Sha256: NativeCompilerPdfOriginPins.ArchiveSha256 }
            => NativeRuntimeProfile.LocalCompilerPdfOrigins,
        { Length: NativeCompilerDviGraphicsPins.ArchiveLength, Sha256: NativeCompilerDviGraphicsPins.ArchiveSha256 }
            => NativeRuntimeProfile.LocalCompilerDviGraphics,
        { Length: NativeCompilerCourseworkPins.ArchiveLength, Sha256: NativeCompilerCourseworkPins.ArchiveSha256 }
            => NativeRuntimeProfile.LocalCompilerCoursework,
        { Length: 267842942, Sha256: "7494e9fd1f6fa3195ad4d6d15a5cb2c7845e00cd6e3a81d928c60d10345cb8fe" }
            => NativeSelectedPdfContract.Profile,
        _ => NativeInputBudgetContract.Profile
    };
    public static PdfCompiledInputCapacity? SelectedPdfCapacity =>
        NativeSelectedPdfContract.Supports(CompilerProfile)
        && PdfLoaderAsset is { Length: 14243 } loader
        && loader.Sha256 == PdfCompiledInputCapacity.TwelveMiB.Renderer.LoaderSha256
        && PdfWasmAsset is { Length: 3725611 } wasm
        && wasm.Sha256 == PdfCompiledInputCapacity.TwelveMiB.Renderer.WasmSha256
        ? PdfCompiledInputCapacity.TwelveMiB : null;
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
        var asset = CompilerAsset;
        var profile = CompilerProfile;
        if (NativeSelectedPdfContract.Supports(profile) && SelectedPdfCapacity is null) throw new InvalidDataException();
        // Fetch SRI (or the managed SHA check) authenticates the exact bundled
        // archive. Stored entries in a verified suite can be shared as slices.
        var bytes = await Read(asset, new RuntimePreparation(token), token, bufferBrowserResponse: true);
        if (NativeRuntimeProfiles.IsSuite(profile) && ZipRuntimeArchive.OpenVerifiedStoredCompiler(bytes, profile, token) is { } stored)
            return stored;

        await using var hashModule = OperatingSystem.IsBrowser() && browser is not null
            ? await browser.InvokeAsync<IJSObjectReference>("import", token, "./js/bundled-runtime-cache.js") : null;
        var preparation = new RuntimePreparation(token, hashModule);
        await using var archive = new ZipRuntimeArchive();
        await archive.PrepareOwnedAsync(bytes, token, profile, preparation); bytes = [];
        var pins = LocalRuntimeSelection.ParsePins(archive.Pins);
        if (LocalRuntimeSelection.ProfileFor(pins) != profile) throw new InvalidDataException();
        var inputs = new List<NativeResourceInput>(pins.Length);
        foreach (var file in archive.Files)
        {
            token.ThrowIfCancellationRequested();
            inputs.Add(new(file.Name, await file.Read(token)));
        }
        await archive.DisposeAsync(); // Release the archive before transferring its decoded resource arrays.
        token.ThrowIfCancellationRequested();
        var result = await NativeRuntimePackage.CreateFromOwnedArchiveAsync(inputs, pins,
            LocalRuntimeSelection.LimitsFor(profile), profile, preparation);
        inputs.Clear();
        return result.Package ?? throw new InvalidDataException("Bundled compiler integrity failed.");
    }
    private async Task<PdfRuntimePackage> LoadPdf()
    {
        await Task.Yield();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        await ReleaseReady(); deadline.Token.ThrowIfCancellationRequested();
        var preparation = new RuntimePreparation(deadline.Token);
        var loader = await Read(PdfLoaderAsset, preparation, deadline.Token);
        var wasm = await Read(PdfWasmAsset, preparation, deadline.Token);
        var package = new PdfRuntimePackage(loader, wasm, PdfLoaderAsset.Sha256, PdfWasmAsset.Sha256);
        return package.IsValid ? package : throw new InvalidDataException("Bundled renderer integrity failed.");
    }
    private async Task<byte[]> Read(Asset asset, RuntimePreparation preparation, CancellationToken token, bool bufferBrowserResponse = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "bundled-runtime/" + asset.Name);
        bool browserFetch = OperatingSystem.IsBrowser();
        if (browserFetch)
        {
            // Fetch verifies the embedded pin before exposing the body, including
            // responses supplied from the service worker's release cache.
            request.SetBrowserRequestOption("integrity", "sha256-" + Convert.ToBase64String(Convert.FromHexString(asset.Sha256)));
            request.SetBrowserRequestOption("mode", "same-origin");
            request.SetBrowserResponseStreamingEnabled(!bufferBrowserResponse);
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != asset.Length) throw new InvalidDataException();
        if (browserFetch && bufferBrowserResponse)
        {
            var verified = await response.Content.ReadAsByteArrayAsync(token);
            if (verified.Length != asset.Length) throw new InvalidDataException("Bundled resource length differs from its pin.");
            return verified;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var bytes = new byte[asset.Length];
        for (int offset = 0; offset < bytes.Length; offset += RuntimePreparation.ChunkBytes)
        {
            await preparation.YieldIfNeededAsync();
            int length = Math.Min(RuntimePreparation.ChunkBytes, bytes.Length - offset);
            await stream.ReadExactlyAsync(bytes.AsMemory(offset, length), token);
        }
        if (await stream.ReadAsync(new byte[1], token) != 0
            || !browserFetch && NativeRuntimePackage.Digest(bytes).Hex != asset.Sha256)
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
