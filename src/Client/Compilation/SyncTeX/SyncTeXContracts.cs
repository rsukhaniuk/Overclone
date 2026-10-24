namespace Client.Compilation.SyncTeX;

public enum SyncTeXDirection { Forward, Inverse }
public enum SyncTeXQueryStatus
{
    Completed, NoMatches, InvalidInput, Unavailable, Busy, Cancelled,
    TimedOut, Disposed, InvalidResponse, Failed
}

// These are upstream visible big-point coordinates. Box V is its baseline.
// Renderer crop/rotation and DOM conversion belong to the display integration.
public sealed record SyncTeXPoint(double H, double V);
public sealed record SyncTeXBox(double H, double V, double Width, double Height, double Depth);
public sealed record SyncTeXMatch(string File, int Tag, int Line, int? Column, int Page,
    SyncTeXPoint Point, SyncTeXBox Box);
public sealed record SyncTeXQueryRequest(byte[] Map, string MapSha256, SyncTeXDirection Direction,
    string? File = null, int Line = 0, int Page = 0, double H = 0, double V = 0);
public sealed record SyncTeXQueryOutcome(SyncTeXQueryStatus Status, IReadOnlyList<SyncTeXMatch> Matches,
    bool Truncated = false, string Code = "");

public interface ISyncTeXQuery : IAsyncDisposable
{
    Task<SyncTeXQueryOutcome> QueryAsync(SyncTeXQueryRequest request, CancellationToken cancellationToken = default);
}

// Trusted composition provides a separately verified local runtime, never a URL
// or executable selected by document contents. Consumers own and verify copies.
public sealed class SyncTeXRuntimePackage
{
    private readonly byte[] _loader, _wasm;
    public SyncTeXRuntimePackage(byte[] loader, byte[] wasm, string loaderSha256, string wasmSha256)
    {
        ArgumentNullException.ThrowIfNull(loader); ArgumentNullException.ThrowIfNull(wasm);
        if (loader.Length is < 1 or > 1048576 || wasm.Length is < 8 or > 1048576)
            throw new ArgumentException("SyncTeX runtime size limit.");
        _loader = loader.ToArray(); _wasm = wasm.ToArray();
        LoaderSha256 = loaderSha256; WasmSha256 = wasmSha256;
    }
    public string LoaderSha256 { get; }
    public string WasmSha256 { get; }
    public bool IsValid => Client.Pdf.PdfHashes.IsSha(LoaderSha256) && Client.Pdf.PdfHashes.IsSha(WasmSha256)
        && Client.Pdf.PdfHashes.Of(_loader) == LoaderSha256 && Client.Pdf.PdfHashes.Of(_wasm) == WasmSha256
        && _wasm.AsSpan(0, 8).SequenceEqual(new byte[] { 0, 97, 115, 109, 1, 0, 0, 0 });
    public byte[] CopyLoader() => _loader.ToArray();
    public byte[] CopyWasm() => _wasm.ToArray();
}
public interface ISyncTeXRuntimeProvider
{
    ValueTask<SyncTeXRuntimePackage?> GetAsync(CancellationToken cancellationToken);
}
