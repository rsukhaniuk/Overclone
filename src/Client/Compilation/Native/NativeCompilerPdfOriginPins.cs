namespace Client.Compilation.Native;

/// <summary>Pinned direct pdfTeX writer; all other P19 resources are retained.</summary>
public static class NativeCompilerPdfOriginPins
{
    public static bool Ready => NativeCompilerDviGraphicsPins.Ready;
    public const int ResourceCount = 5154, TotalBytes = 278964393, ArchiveLength = 280335305;
    public const string ArchiveSha256 = "a689da4e943bbb9f8feadd0580e92b758195f7359dbb797d201bace8184c2abf";
    public const string ClosureHash = "7eacad1bb6b823996a97c7bc8d5ee0cf7176d9ac9e240eb156dcfaaa9f3c5f7b";
    public const string IdentityName = "local-compiler-pdftex-origin-v1";
    public const string RuntimeIdentity = "84abead5a2feb357d52ff01364f5b72ed099610c40a8c0428ac10f9a64b0e652";
    internal const string LoaderName = "pdftex-origin-engine.js", WasmName = "pdftex-origin-engine.wasm";
    internal const int LoaderLength = 142478, WasmLength = 1959300;
    internal const string LoaderHash = "57daa24fd7edf922c6ce74a76cf10f97ec8ace1a8db39b0d0b82296fb7168d85";
    internal const string WasmHash = "cf1e37c774fcd112efe739e4768f3d88c508d064d5340af2535395bbbe0508ec";

    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)
    {
        if (pins.Count != ResourceCount || pins.Values.Sum(pin => (long)pin.Length) != TotalBytes
            || NativeRuntimePackage.ResourceIdentity(pins.Values).Hex != ClosureHash
            || !pins.TryGetValue(LoaderName, out var loader) || loader.Length != LoaderLength || loader.Sha256 != LoaderHash
            || !pins.TryGetValue(WasmName, out var wasm) || wasm.Length != WasmLength || wasm.Sha256 != WasmHash) return false;
        var parent = pins.Where(pair => pair.Key != LoaderName && pair.Key != WasmName)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return NativeCompilerDviGraphicsPins.MatchesComponents(parent);
    }
}
