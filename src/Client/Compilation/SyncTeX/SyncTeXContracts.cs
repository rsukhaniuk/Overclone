using Client.Compilation.Native;
using System.Text;

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
    string? File = null, int Line = 0, int Page = 0, double H = 0, double V = 0, SyncTeXRuntimeBinding? RuntimeBinding = null);
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
    public static SyncTeXRuntimePackage FromCompiler(NativeRuntimePackage compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        var binding = SyncTeXRuntimeBinding.ForProfile(compiler.Profile)
            ?? throw new ArgumentException("No closed source query profile.", nameof(compiler));
        if (compiler.Bundle != binding.Bundle) throw new ArgumentException("Compiler closure mismatch.", nameof(compiler));
        return new(compiler.Copy("synctex-query.js"), compiler.Copy("synctex-query.wasm"), binding.LoaderSha256, binding.WasmSha256)
        { CompilerBinding = binding };
    }
    public SyncTeXRuntimeBinding? CompilerBinding { get; private init; }
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

/// <summary>Immutable restriction derived from a verified closed compiler, never
/// navigation authority. Prevents changing query families under an accepted map.</summary>
public sealed class SyncTeXRuntimeBinding
{
    private SyncTeXRuntimeBinding(NativeRuntimeProfile profile, CompilationDescriptor bundle, string loader, string wasm)
    { Profile = profile; Bundle = bundle; LoaderSha256 = loader; WasmSha256 = wasm; }
    public NativeRuntimeProfile Profile { get; }
    public CompilationDescriptor Bundle { get; }
    public string LoaderSha256 { get; }
    public string WasmSha256 { get; }
    internal static SyncTeXRuntimeBinding? ForProfile(NativeRuntimeProfile profile)
    {
        if (!NativeRuntimeProfiles.Known(profile) || NativeRuntimeProfiles.ClosureHash(profile) is not { Length: 64 } closure
            || !(NativeRuntimeProfiles.IsSuite(profile) && NativeRuntimeProfiles.SuitePinsReady(profile)
                || profile is NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi)) return null;
        var identity = Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"native-runtime-profile-v1\n{NativeRuntimeProfiles.IdentityName(profile)}\n{closure}")));
        bool area64 = profile is NativeOriginCompilerContract.Profile or NativeInputBudgetContract.Profile;
        return new(profile, NativeRuntimePackage.BundleFor(profile, identity),
            area64 ? NativeCompilerOriginPins.QueryLoaderHash : NativeRuntimeProfiles.SyncTeXQueryLoaderHash,
            area64 ? NativeCompilerOriginPins.QueryWasmHash : NativeRuntimeProfiles.SyncTeXQueryWasmHash);
    }
    internal bool Matches(SyncTeXRuntimeBinding? other) => other is not null && Profile == other.Profile
        && Bundle == other.Bundle && LoaderSha256 == other.LoaderSha256 && WasmSha256 == other.WasmSha256;
}
