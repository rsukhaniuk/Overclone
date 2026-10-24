using System.Runtime.CompilerServices;
using System.Text.Json;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Xunit;

namespace Workspace.Tests.SyncTeXResources;

internal static class SyncTeXResourceFixture
{
    internal const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalLatex2023SyncTeX;
    internal const string Identity = "4a7c8b1e4ffabeab35e0cc07bc8342e3213e7b4c53cee154752f095b153e2e60";
    internal static byte[] PinsJson([CallerFilePath] string source = "")
        => File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(source)!, "pins.json"));
    internal static NativeResourcePin[] Pins() => LocalRuntimeSelection.ParsePins(PinsJson()).ToArray();
    internal static NativeResourcePin[] OldPins() => Pins().Where(p => !p.Name.StartsWith("synctex-query.", StringComparison.Ordinal)).Select(p => p.Name switch
    {
        "engine.js" => new(p.Name, 72275, NativeRuntimePackage.LoaderHash),
        "engine.wasm" => new(p.Name, 1779444, NativeRuntimePackage.WasmHash),
        "pdflatex.fmt" => new(p.Name, NativeRuntimeProfiles.ModernFormatLength, NativeRuntimeProfiles.ModernFormatHash),
        _ => p
    }).ToArray();
    internal static byte[] Json(IEnumerable<NativeResourcePin> pins) => JsonSerializer.SerializeToUtf8Bytes(
        pins.Select(p => new { name = p.Name, length = p.Length, sha256 = p.Sha256 }));
    internal static LocalRuntimeFile[] Descriptors(IEnumerable<NativeResourcePin> pins, Action onRead)
        => pins.Select(p => new LocalRuntimeFile(p.Name, p.Length, _ => { onRead(); throw new InvalidOperationException("Unexpected resource IO"); })).ToArray();
    internal static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// Optional actual resource bytes are explicitly mounted by the integration owner.
// The public pins fixture above is not a synthetic runtime or an execution proof.
public sealed class SyncTeXRuntimeFactAttribute : FactAttribute
{
    public SyncTeXRuntimeFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SYNCTEX_RUNTIME_DIR")))
            Skip = "Requires explicitly mounted SYNCTEX_RUNTIME_DIR with exact499 pins.json and files/.";
    }
}

internal static class ActualSyncTeXResources
{
    internal static readonly Lazy<(byte[] Json, NativeResourcePin[] Pins, NativeResourceInput[] Inputs)> Fixture = new(() =>
    {
        var root = Environment.GetEnvironmentVariable("SYNCTEX_RUNTIME_DIR") ?? throw new InvalidOperationException("Explicit runtime fixture missing.");
        var json = File.ReadAllBytes(Path.Combine(root, "pins.json"));
        var pins = LocalRuntimeSelection.ParsePins(json).ToArray();
        Assert.Equal(SyncTeXResourceFixture.Profile, LocalRuntimeSelection.ProfileFor(pins));
        return (json, pins, pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(root, "files", p.Name)))).ToArray());
    });
    internal static LocalRuntimeFile[] Files() => Fixture.Value.Inputs.Select(i => new LocalRuntimeFile(i.Name, i.Bytes.Length, _ => Task.FromResult(i.Bytes))).ToArray();
}
