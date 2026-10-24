using System.Collections.Immutable;
using System.Text;
using Client.Workspace.Domain;
using Hash = Client.Workspace.Domain.SHA256;

namespace Client.Compilation.Native;

public sealed record NativeResourcePin(string Name, int Length, string Sha256);
public sealed record NativeResourceInput(string Name, byte[] Bytes);
public sealed record NativeRuntimeLimits(int MaxFiles, int MaxFileBytes, int MaxTotalBytes,
    NativeRuntimeProfile Profile = NativeRuntimeProfile.Legacy)
{
    public bool IsValid => NativeRuntimeProfiles.Known(Profile) && MaxFiles > 0 && MaxFiles <= 512
        && MaxFileBytes > 0 && MaxFileBytes <= NativeRuntimeProfiles.MaxFileBytes(Profile)
        && MaxTotalBytes > 0 && MaxTotalBytes <= NativeRuntimeProfiles.MaxTotalBytes(Profile);
}
public enum NativeRuntimeIssue { InvalidLimits, Unavailable, InvalidInventory, ResourceLimit, TechnicalHashMismatch }
public sealed record NativeRuntimeCreation(NativeRuntimePackage? Package, NativeRuntimeIssue? Issue);

/// <summary>Explicit technical inputs, NOT an installation/license/safety approval.
/// No default loader, fetch, filesystem lookup, host fonts or fallback engine.</summary>
public sealed class NativeRuntimePackage
{
    public const string EngineId = "pdftex";
    public const string EngineVersion = "1.40.21";
    public const string WasmHash = "7d239273b63b42d0c8f31d5e198a221c200c222f365760c20b9178408077048e";
    public const string FormatHash = "99c77a3e291d0a74bb62152e3a377f848296d442e204afc41108130ac3df8c51";
    public const string LoaderHash = "39384532be2e32fd09dba99226db9da7572032c70da2bb2428fc9123c7fd163f";
    public const string FormatResourceName = "pdflatex.fmt";
    public const string NativeFormatName = "swiftlatexpdftex.fmt";
    private static string Destination(string logicalName) => logicalName == FormatResourceName ? NativeFormatName : logicalName;
    private readonly ImmutableDictionary<string, byte[]> _bytes;
    private NativeRuntimePackage(ImmutableDictionary<string, byte[]> bytes, Hash identity, NativeRuntimeProfile profile)
    { _bytes = bytes; Identity = identity; Profile = profile; }
    public Hash Identity { get; }
    public NativeRuntimeProfile Profile { get; }
    public CompilationDescriptor Engine => new(EngineId, EngineVersion, new(NativeRuntimeProfiles.CompilerWasmHash(Profile)));
    public CompilationDescriptor Bundle => Profile switch
    {
        NativeRuntimeProfile.Legacy => new("n1-historical-assets", "2015-10-01", Identity),
        NativeRuntimeProfile.LocalLatex2023 => new("local-latex-assets", "2023-11-01-p1-profile-v1", Identity),
        NativeRuntimeProfile.LocalLatex2023Authoring => new("local-latex-authoring", "2023-11-01-p1-authoring-profile-v1", Identity),
        NativeRuntimeProfile.LocalLatex2023SyncTeX => new("local-latex-synctex", "2023-11-01-p1-synctex-profile-v1", Identity),
        NativeRuntimeProfile.LocalLatex2023Dvi => new("local-latex-dvi", "2023-11-01-p1-dvi-profile-v1", Identity),
        _ => throw new InvalidOperationException("Unknown trusted runtime profile.")
    };
    public CompilationDescriptor Bibtex => new("bibtex", "0.99d", new(NativeRuntimeProfiles.CompilerWasmHash(Profile)));
    public static CompileOutputDescriptor Pdf => new("pdf", "1", null);
    public static CompileOutputDescriptor Adapter => new("native-pdftex", "n11-v1", new(WasmHash));
    public CompilationDescriptor DviEngine => new("latex-dvi", EngineVersion, new(NativeRuntimeProfiles.CompilerWasmHash(Profile)));
    public CompileOutputDescriptor DviOutputAdapter => NativeLatexDviContract.OutputAdapter;
    public CompileOutputDescriptor OutputAdapter => Profile is NativeRuntimeProfile.LocalLatex2023SyncTeX or NativeRuntimeProfile.LocalLatex2023Dvi
        ? new("native-pdftex", "n11-synctex-v1", new(NativeRuntimeProfiles.SyncTeXCompilerWasmHash)) : Adapter;
    public byte[] Copy(string name) => _bytes[name].ToArray();
    // The inventory/Identity always use logical package names. Only the phase
    // projection binds the same owned format bytes to the native VFS name.
    private static bool RuntimeData(string name) => name is not "engine.js" and not "engine.wasm"
        and not "synctex-query.js" and not "synctex-query.wasm" and not "dvi-converter.js" and not "dvi-converter.wasm"
        and not FormatResourceName and not "latex-dvi.fmt";
    private IEnumerable<KeyValuePair<string, byte[]>> PhaseFiles(bool dvi) => _bytes.Where(x => RuntimeData(x.Key)
        || x.Key == (dvi ? "latex-dvi.fmt" : FormatResourceName));
    private static string PhaseDestination(string name) => name == "latex-dvi.fmt" ? NativeFormatName : Destination(name);
    internal IEnumerable<(string Name, int Length)> FileLengthsFor(bool dvi) => PhaseFiles(dvi).Select(x => (PhaseDestination(x.Key), x.Value.Length));
    internal IEnumerable<(string Name, int Length)> FileLengths => FileLengthsFor(false);
    internal NativeFileTransfer[] CopyFiles(bool dvi = false) => PhaseFiles(dvi)
        .OrderBy(x => PhaseDestination(x.Key), StringComparer.Ordinal).Select(x => new NativeFileTransfer(PhaseDestination(x.Key), x.Value.ToArray())).ToArray();
    private static bool ConverterResource(string name) => name is "glyphlist.txt" or "pdfglyphlist.txt"
        || Path.GetExtension(name) is ".tfm" or ".pfb" or ".ttf" or ".map" or ".enc";
    internal IEnumerable<(string Name, int Length)> ConverterResourceLengths => _bytes.Where(x => ConverterResource(x.Key)).Select(x => (x.Key, x.Value.Length));
    internal NativeFileTransfer[] CopyConverterResources() => _bytes.Where(x => ConverterResource(x.Key))
        .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new NativeFileTransfer(x.Key, x.Value.ToArray())).ToArray();

    public static NativeRuntimeCreation Create(IReadOnlyList<NativeResourceInput>? input,
        IReadOnlyList<NativeResourcePin>? expected, NativeRuntimeLimits? limits,
        NativeRuntimeProfile profile = NativeRuntimeProfile.Legacy)
    {
        if (!NativeRuntimeProfiles.Known(profile) || limits is null || !limits.IsValid || limits.Profile != profile)
            return new(null, NativeRuntimeIssue.InvalidLimits);
        if (input is null || expected is null) return new(null, NativeRuntimeIssue.Unavailable);
        // Counts first: don't access an entry or allocate a count-sized collection yet.
        int count = input.Count;
        if (count < 3 || count > limits.MaxFiles || expected.Count != count
            || NativeRuntimeProfiles.Modern(profile) && count != NativeRuntimeProfiles.InstalledCount(profile))
            return new(null, NativeRuntimeIssue.ResourceLimit);
        var supplied = new NativeResourceInput[count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        var pins = new Dictionary<string, NativeResourcePin>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            var p = expected[i];
            if (p is null || !Name(p.Name) || !Hex(p.Sha256) || p.Length < 0 || p.Length > limits.MaxFileBytes || !pins.TryAdd(p.Name, p))
                return new(null, NativeRuntimeIssue.InvalidInventory);
        }
        // A different logical resource must not occupy the mapped destination,
        // even with identical bytes. Never duplicate, merge or silently overwrite.
        if (pins.ContainsKey(NativeFormatName)) return new(null, NativeRuntimeIssue.InvalidInventory);
        if (!Fixed("engine.js", NativeRuntimeProfiles.CompilerLoaderLength(profile), NativeRuntimeProfiles.CompilerLoaderHash(profile))
            || !Fixed("engine.wasm", NativeRuntimeProfiles.CompilerWasmLength(profile), NativeRuntimeProfiles.CompilerWasmHash(profile))
            || !Fixed(FormatResourceName, NativeRuntimeProfiles.FormatLength(profile), NativeRuntimeProfiles.FormatHash(profile)))
            return new(null, NativeRuntimeIssue.TechnicalHashMismatch);
        bool Fixed(string name, int length, string hash) => pins.TryGetValue(name, out var pin) && pin.Length == length && pin.Sha256 == hash;
        if (profile == NativeRuntimeProfile.LocalLatex2023Dvi
            && (!Fixed("latex-dvi.fmt", NativeRuntimeProfiles.DviFormatLength, NativeRuntimeProfiles.DviFormatHash)
                || !Fixed("dvi-converter.js", 12606, NativeRuntimeProfiles.DviConverterLoaderHash)
                || !Fixed("dvi-converter.wasm", 685953, NativeRuntimeProfiles.DviConverterWasmHash)))
            return new(null, NativeRuntimeIssue.TechnicalHashMismatch);
        // Bind the complete declared closure before even inspecting resource bytes.
        // Supplied expected pins alone cannot expand the trusted set.
        var closure = ResourceIdentity(pins.Values);
        if (NativeRuntimeProfiles.Modern(profile) && closure.Hex != NativeRuntimeProfiles.ClosureHash(profile))
            return new(null, NativeRuntimeIssue.TechnicalHashMismatch);
        // Preflight ALL supplied lengths before any clone/hash. Copy-before-hash below.
        for (int i = 0; i < count; i++)
        {
            var r = input[i];
            if (r is null || !Name(r.Name) || r.Bytes is null || !names.Add(r.Name) || !pins.TryGetValue(r.Name, out var pin))
                return new(null, NativeRuntimeIssue.InvalidInventory);
            if (r.Bytes.Length != pin.Length || r.Bytes.Length > limits.MaxFileBytes || r.Bytes.Length > limits.MaxTotalBytes - total)
                return new(null, NativeRuntimeIssue.ResourceLimit);
            total += r.Bytes.Length; supplied[i] = r;
        }
        var owned = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.Ordinal);
        foreach (var r in supplied)
        {
            var bytes = r.Bytes.ToArray();
            if (Digest(bytes).Hex != pins[r.Name].Sha256) return new(null, NativeRuntimeIssue.TechnicalHashMismatch);
            owned.Add(r.Name, bytes);
        }
        // Legacy identity remains byte-for-byte compatible. Modern semantics,
        // contract version and complete resource closure form a distinct identity.
        var identity = profile == NativeRuntimeProfile.Legacy ? closure : Digest(Encoding.UTF8.GetBytes(
            $"native-runtime-profile-v1\n{NativeRuntimeProfiles.IdentityName(profile)}\n{closure.Hex}"));
        return new(new(owned.ToImmutable(), identity, profile), null);
    }
    internal static Hash ResourceIdentity(IEnumerable<NativeResourcePin> pins)
        => Digest(Encoding.UTF8.GetBytes(string.Join("\n", pins.OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => FormattableString.Invariant($"{x.Name}\t{x.Length}\t{x.Sha256}")))));
    private static bool Name(string? name) => name is { Length: > 0 and <= 200 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    private static bool Hex(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static Hash Digest(byte[] bytes) => Hash.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes));
}

public interface INativeRuntimeProvider { Task<NativeRuntimePackage?> GetAsync(); }
public sealed class FixedNativeRuntimeProvider(NativeRuntimePackage? package) : INativeRuntimeProvider
{ public Task<NativeRuntimePackage?> GetAsync() => Task.FromResult(package); }
