using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace Client.Pdf;

public enum PdfInspectionStatus { Completed, InvalidInput, Unavailable, Busy, Cancelled, Disposed, TimedOut, TransportError, InvalidResponse, NativeFailure, OutputLimit }

/// <summary>Explicit capacity for a trusted compiled-artifact caller and the matching pinned renderer.</summary>
public sealed class PdfCompiledInputCapacity
{
    private PdfCompiledInputCapacity() { }
    public static PdfCompiledInputCapacity TwelveMiB { get; } = new();
    public int MaxInputBytes => 12 * 1024 * 1024;
    public PdfRuntimeIdentity Renderer { get; } = new(
        "ea2cebb93f554788751e58554b6e16a094ee6d0883774cb697cbb4a5ce8ec38c",
        "b1630e6501e7715b4e0870a059f4e9b8b5acc4c96aa2af0e4ecacb04071622d7");
    internal string WireName => "compiled-12-mib";
}

public sealed class PdfDocument
{
    public const int DefaultInputBytes = 8 * 1024 * 1024;
    private readonly byte[] _bytes;
    public PdfDocument(byte[] bytes) : this(bytes, null) { }
    public PdfDocument(byte[] bytes, PdfCompiledInputCapacity? compiledInputCapacity)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 1 || bytes.Length > (compiledInputCapacity?.MaxInputBytes ?? DefaultInputBytes))
            throw new ArgumentException("PDF byte limit.", nameof(bytes));
        CompiledInputCapacity = compiledInputCapacity;
        _bytes = (byte[])bytes.Clone(); Sha256 = PdfHashes.Of(_bytes);
    }
    public PdfCompiledInputCapacity? CompiledInputCapacity { get; }
    public int Length => _bytes.Length;
    public string Sha256 { get; }
    public byte[] CopyBytes() => (byte[])_bytes.Clone();
}

public sealed record PdfInspectionBudget(int InputBytes = PdfDocument.DefaultInputBytes, int Pages = 32, int TextUnits = 100000,
    int SearchHits = 256, int RasterPixels = 4000000, int OutputBytes = 16777216, int LiveHandles = 64, int DeadlineMs = 30000)
{
    // Finite page-budget maximum; not full Overleaf parity or a global memory theorem.
    // Default32 preserves existing explicit callers; positive selected budgets <=512 are admitted.
    public const int MaxDocumentPages = 512;
    public PdfCompiledInputCapacity? CompiledInputCapacity { get; init; }
    public bool IsValid => InputBytes > 0 && InputBytes <= (CompiledInputCapacity?.MaxInputBytes ?? PdfDocument.DefaultInputBytes)
        && Pages is > 0 and <= MaxDocumentPages && TextUnits is > 0 and <= 100000
        && SearchHits is > 0 and <= 256 && RasterPixels is > 0 and <= 4000000 && OutputBytes is > 0 and <= 16777216
        && LiveHandles is > 0 and <= 64 && DeadlineMs is >= 1 and <= 30000;
    internal bool Admits(PdfDocument? document) => document is not null && IsValid
        && ReferenceEquals(document.CompiledInputCapacity, CompiledInputCapacity) && document.Length <= InputBytes;
}

public sealed class PdfInspectRequest
{
    public PdfInspectRequest(PdfDocument document, int pageIndex, PdfInspectionBudget budget, string? search = null,
        bool raster = true, int rasterWidth = 240, int rasterHeight = 340, bool inspectActions = false, bool inspectLayout = false)
    { Document = document; PageIndex = pageIndex; Budget = budget; Search = search; Raster = raster; RasterWidth = rasterWidth; RasterHeight = rasterHeight; InspectActions = inspectActions; InspectLayout = inspectLayout; }
    public PdfDocument Document { get; }
    public int PageIndex { get; }
    public PdfInspectionBudget Budget { get; }
    public string? Search { get; }
    public bool Raster { get; }
    public bool InspectActions { get; }
    public bool InspectLayout { get; }
    internal bool InspectObjectLayout { get; init; }
    public int RasterWidth { get; }
    public int RasterHeight { get; }
    public bool IsValid => Budget?.Admits(Document) == true
        && (!(InspectLayout || InspectObjectLayout) || !Raster && !InspectActions && Search is null)
        && PageIndex >= 0 && PageIndex < Budget.Pages && RasterWidth is > 0 and <= 16384 && RasterHeight is > 0 and <= 16384
        && (long)RasterWidth * RasterHeight <= Budget.RasterPixels
        && (!Raster || (long)RasterWidth * RasterHeight * 4 + 16 <= Budget.OutputBytes)
        && (Search is null || Search.Length is > 0 and <= 255 && !Search.Contains('\0') && PdfHashes.ValidUtf16(Search));
}

// Provisioning is an explicit trusted caller responsibility, not PDF metadata or
// an arbitrary script/URL in an inspect request. Digest agreement is NOT adoption.
public sealed class PdfRuntimePackage
{
    private readonly byte[] _loader, _wasm;
    private readonly Lazy<bool> _valid;
    public PdfRuntimePackage(byte[] loader, byte[] wasm, string loaderSha256, string wasmSha256)
    {
        ArgumentNullException.ThrowIfNull(loader); ArgumentNullException.ThrowIfNull(wasm);
        // Check BOTH lengths before either expensive owned clone/hash. Caller
        // gets ArgumentException with the offending parameter, not a giant copy
        // followed by IsValid=false. Valid packages retain defensive copying.
        if (loader.Length is < 1 or > 1048576) throw new ArgumentException("Runtime loader byte limit.", nameof(loader));
        if (wasm.Length is < 8 or > 16777216) throw new ArgumentException("Runtime WASM byte limit.", nameof(wasm));
        _loader = (byte[])loader.Clone(); _wasm = (byte[])wasm.Clone(); LoaderSha256 = loaderSha256; WasmSha256 = wasmSha256;
        _valid = new(Validate);
    }
    public string LoaderSha256 { get; }
    public string WasmSha256 { get; }
    public bool IsValid => _valid.Value;
    private bool Validate() => _loader.Length is > 0 and <= 1048576 && _wasm.Length is >= 8 and <= 16777216
        && _wasm.AsSpan(0, 8).SequenceEqual(new byte[] { 0, 97, 115, 109, 1, 0, 0, 0 })
        && PdfHashes.IsSha(LoaderSha256) && PdfHashes.IsSha(WasmSha256)
        && PdfHashes.Of(_loader) == LoaderSha256 && PdfHashes.Of(_wasm) == WasmSha256;
    public byte[] CopyLoader() => (byte[])_loader.Clone();
    public byte[] CopyWasm() => (byte[])_wasm.Clone();
}
public interface IPdfRuntimeProvider { ValueTask<PdfRuntimePackage?> GetAsync(CancellationToken cancellationToken); }
public interface IPdfInspector : IAsyncDisposable { Task<PdfInspectionOutcome> InspectAsync(PdfInspectRequest request, CancellationToken cancellationToken = default); }
// Geometry facts below use native/raw PDF page units, NOT a general physical
// 1/72-inch promise. Raster dimensions are separate integer pixel counts.
public readonly record struct PdfBox(double Left, double Bottom, double Right, double Top);
public readonly record struct PdfPoint(double X, double Y);
public readonly record struct PdfDisplayBox(double Left, double Top, double Right, double Bottom);
public sealed record PdfGlyph(int Index, int UnicodeValue, PdfPoint? Origin, PdfBox? Box, PdfDisplayBox? DisplayBox, string BoxStatus, string Font, string FontStatus);
public readonly record struct PdfSearchHit(int StartCharacterIndex, int CharacterCount);

public sealed class PdfPageInspection
{
    private readonly byte[] _bgra;
    internal PdfPageInspection(int pages, int index, PdfBox media, PdfBox? crop, bool cropDefaulted, PdfBox effective,
        int rotation, PdfPoint display, PdfPoint nativeSize, string text, int[] utf16, PdfGlyph[] glyphs, PdfSearchHit[] hits,
        byte[] bgra, int width, int height)
    {
        PageCount = pages; PageIndex = index; MediaBox = media; CropBox = crop; CropDefaulted = cropDefaulted; EffectiveBox = effective;
        Rotation = rotation; NativeDisplaySize = display; NativeReportedSize = nativeSize; Text = text;
        Utf16Units = Array.AsReadOnly((int[])utf16.Clone()); Glyphs = Array.AsReadOnly((PdfGlyph[])glyphs.Clone());
        SearchHits = Array.AsReadOnly((PdfSearchHit[])hits.Clone()); _bgra = (byte[])bgra.Clone(); RasterWidth = width; RasterHeight = height;
    }
    public int PageCount { get; }
    public int PageIndex { get; }
    public PdfBox MediaBox { get; }
    public PdfBox? CropBox { get; }
    public bool CropDefaulted { get; }
    public PdfBox EffectiveBox { get; }
    public int Rotation { get; }
    // E1/PDFium reports native/raw units for some valid UserUnit != 1 PDFs.
    // No reliable UserUnit discovery is exposed by the accepted ABI. Preserve
    // raw facts, never promise general physical 1/72-inch measurements.
    public string CoordinateUnits => "native/raw PDF page units";
    public PdfPoint? PhysicalSizeInPdfPoints => null;
    public string PhysicalSizeStatus => "indeterminate: UserUnit not independently established";
    public PdfPoint NativeDisplaySize { get; }
    public PdfPoint NativeReportedSize { get; }
    public string Text { get; }
    public ReadOnlyCollection<int> Utf16Units { get; }
    public ReadOnlyCollection<PdfGlyph> Glyphs { get; }
    public ReadOnlyCollection<PdfSearchHit> SearchHits { get; }
    public int RasterWidth { get; }
    public int RasterHeight { get; }
    public byte[] CopyBgra() => (byte[])_bgra.Clone();
}

public sealed record PdfInspectionOutcome(PdfInspectionStatus Status, string RequestId, string DocumentSha256, long WorkerEpoch,
    int? NativeStatus = null, uint NativeError = 0, bool? EnteredNative = null, PdfPageInspection? Page = null,
    string Code = "", IReadOnlyList<string>? NativeCalls = null,
    PdfRuntimeIdentity? Runtime = null, PdfActionInspection? Actions = null, PdfLayoutInspection? Layout = null)
{
    internal PdfObjectLayoutInspection? ObjectLayout { get; init; }
}

public sealed record PdfRuntimeIdentity(string LoaderSha256, string WasmSha256);
public enum PdfActionAvailability { NotRequested, Unsupported, Available }
public sealed record PdfActionCapabilities(int AbiVersion, int MaxLinks, int MaxFieldBytesIncludingNul,
    string CoordinateSpace, string PhysicalMeasurement, bool ActionExecution);
public sealed record PdfActionField(string Status, int? NativeBytes, string? Value);
public sealed record PdfActionDestination(string Status, string? Source, int? PageIndex);
public sealed record PdfPassiveAction(int Index, string RectangleStatus, PdfBox? RawRectangle,
    string ActionStatus, uint? NativeType, string TypeStatus, PdfActionField Uri,
    PdfActionField FilePath, PdfActionDestination Destination);
public sealed class PdfActionInspection
{
    internal PdfActionInspection(PdfActionAvailability availability, PdfActionCapabilities? capabilities, PdfPassiveAction[] links)
    { Availability = availability; Capabilities = capabilities; Links = Array.AsReadOnly((PdfPassiveAction[])links.Clone()); }
    public PdfActionAvailability Availability { get; }
    public PdfActionCapabilities? Capabilities { get; }
    public ReadOnlyCollection<PdfPassiveAction> Links { get; }
}

public static class PdfHashes
{
    public static string Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static bool IsSha(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool ValidUtf16(string value)
    {
        for (int i = 0; i < value.Length; i++)
            if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false; }
            else if (char.IsLowSurrogate(value[i])) return false;
        return true;
    }
}
