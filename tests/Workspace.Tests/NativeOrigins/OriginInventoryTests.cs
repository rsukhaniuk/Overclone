using System.Text;
using System.Text.Json;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.Compilation.SyncTeX;
using Microsoft.JSInterop;
using Workspace.Tests.CompilerThesis;
using Xunit;

namespace Workspace.Tests.NativeOrigins;

public sealed class OriginInventoryTests
{
    [OriginFixtureFact]
    public async Task Exact_closed_inventory_and_paired_query_profile_are_required()
    {
        var pins = OriginFixture.Pins;
        var package = OriginFixture.Package;
        Assert.Equal(NativeOriginCompilerContract.Profile, LocalRuntimeSelection.ProfileFor(pins));
        Assert.True(pins.Length == NativeCompilerOriginPins.ResourceCount);
        Assert.Equal(NativeCompilerOriginPins.TotalBytes, pins.Sum(p => (long)p.Length));
        Assert.Equal("local-compiler-origins", package.Bundle.Id);
        foreach (var name in new[] { "luatex.wasm", "dvi-map-converter.wasm", "xdv-map-converter.wasm", "synctex-query.js", "synctex-query.wasm" })
        {
            var changed = pins.Select(p => p.Name == name ? p with { Sha256 = new string('0', 64) } : p).ToArray();
            Assert.Throws<FormatException>(() => LocalRuntimeSelection.ProfileFor(changed));
        }
        var runtime = SyncTeXRuntimePackage.FromCompiler(package);
        Assert.True(runtime.IsValid);
        Assert.Equal(NativeCompilerOriginPins.QueryLoaderHash, runtime.LoaderSha256);
        Assert.Equal(NativeCompilerOriginPins.QueryWasmHash, runtime.WasmSha256);
        var js = new Interop();
        await using var query = new JsSyncTeXQuery(js, new Provider(runtime));
        Assert.Equal(SyncTeXQueryStatus.NoMatches, (await query.QueryAsync(Request(runtime.CompilerBinding))).Status);
        Assert.Equal(15, js.QueryProfile);
        Assert.Equal(SyncTeXQueryStatus.Unavailable, (await query.QueryAsync(Request(null))).Status);
        Assert.Equal(1, js.Imports);
    }

    [ThesisFixtureFact]
    public async Task Parent_query_keeps_legacy_wire_and_rejects_unbound_or_corrupted_replacement()
    {
        var runtime = SyncTeXRuntimePackage.FromCompiler(ThesisFixture.Package);
        var js = new Interop();
        await using (var query = new JsSyncTeXQuery(js, new Provider(runtime)))
            Assert.Equal(SyncTeXQueryStatus.NoMatches, (await query.QueryAsync(Request(runtime.CompilerBinding))).Status);
        Assert.Null(js.QueryProfile);
        Assert.Equal(NativeRuntimeProfiles.SyncTeXQueryWasmHash, runtime.WasmSha256);
        // Same genuine query bytes without the verified compiler restriction do
        // not satisfy an accepted map's bound compiler identity.
        foreach (var substitute in new[]
        {
            new SyncTeXRuntimePackage(runtime.CopyLoader(), runtime.CopyWasm(), runtime.LoaderSha256, runtime.WasmSha256),
            new SyncTeXRuntimePackage(runtime.CopyLoader(), runtime.CopyWasm(), runtime.LoaderSha256, NativeCompilerOriginPins.QueryWasmHash)
        })
        {
            var rejected = new Interop();
            await using var query = new JsSyncTeXQuery(rejected, new Provider(substitute));
            Assert.Equal(SyncTeXQueryStatus.Unavailable, (await query.QueryAsync(Request(runtime.CompilerBinding))).Status);
            Assert.Equal(0, rejected.Imports);
        }
    }

    [OriginAndThesisFixtureFact]
    public async Task Changing_installed_parent_or_mixing_area64_pair_cannot_query_an_origin_map()
    {
        var origin = SyncTeXRuntimePackage.FromCompiler(OriginFixture.Package);
        var parent = SyncTeXRuntimePackage.FromCompiler(ThesisFixture.Package);
        var mixed = new SyncTeXRuntimePackage(parent.CopyLoader(), origin.CopyWasm(), parent.LoaderSha256, origin.WasmSha256);
        Assert.True(mixed.IsValid); // Hash validity alone is insufficient.
        foreach (var substitute in new[] { parent, mixed })
        {
            var js = new Interop();
            await using var query = new JsSyncTeXQuery(js, new Provider(substitute));
            Assert.Equal(SyncTeXQueryStatus.Unavailable, (await query.QueryAsync(Request(origin.CompilerBinding))).Status);
            Assert.Equal(0, js.Imports);
        }
    }

    private static SyncTeXQueryRequest Request(SyncTeXRuntimeBinding? binding)
    {
        var map = Encoding.UTF8.GetBytes("SyncTeX Version:1\nopaque managed transport fixture\n");
        return new(map, NativeRuntimePackage.Digest(map).Hex, SyncTeXDirection.Forward, "main.tex", 1, RuntimeBinding: binding);
    }
    private sealed class Provider(SyncTeXRuntimePackage package) : ISyncTeXRuntimeProvider
    { public ValueTask<SyncTeXRuntimePackage?> GetAsync(CancellationToken token) => ValueTask.FromResult<SyncTeXRuntimePackage?>(package); }
    private sealed class Interop : IJSRuntime, IJSObjectReference
    {
        internal int Imports;
        internal int? QueryProfile;
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args)
        {
            object? value = null;
            if (id == "import") { Imports++; value = this; }
            else if (id == "createSyncTeXQueryPort") value = this;
            else if (id == "query")
            {
                using var document = JsonDocument.Parse((string)args![0]!);
                var root = document.RootElement;
                QueryProfile = root.TryGetProperty("queryProfile", out var profile) ? profile.GetInt32() : null;
                value = new SyncTeXWireReply { Version = 1, RequestId = root.GetProperty("requestId").GetString(), Status = "native", NativeStatus = 1 };
            }
            return ValueTask.FromResult(value is null ? default! : (T)value);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class OriginFixture
{
    internal static string? Root => Environment.GetEnvironmentVariable("ORIGIN_COMPILER_RUNTIME_DIR");
    internal static NativeResourcePin[] Pins => LocalRuntimeSelection.ParsePins(File.ReadAllBytes(Path.Combine(Root!, "pins.json"))).ToArray();
    private static readonly Lazy<NativeRuntimePackage> Loaded = new(() =>
    {
        var pins = Pins;
        return Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(pins.Select(pin => new NativeResourceInput(pin.Name,
            File.ReadAllBytes(Path.Combine(Root!, "files", pin.Name)))).ToArray(), pins, LocalRuntimeSelection.LimitsFor(NativeOriginCompilerContract.Profile), NativeOriginCompilerContract.Profile).Package);
    });
    internal static NativeRuntimePackage Package => Loaded.Value;
    internal static bool Available => NativeCompilerOriginPins.Ready && File.Exists(Path.Combine(Root ?? "", "pins.json"));
}
public sealed class OriginFixtureFactAttribute : FactAttribute
{ public OriginFixtureFactAttribute() { if (!OriginFixture.Available) Skip = "Requires final closed P15 ORIGIN_COMPILER_RUNTIME_DIR; managed consumer checks only."; } }
public sealed class OriginAndThesisFixtureFactAttribute : FactAttribute
{ public OriginAndThesisFixtureFactAttribute() { if (!OriginFixture.Available || !File.Exists(Path.Combine(ThesisFixture.Root ?? "", "pins.json"))) Skip = "Requires exact P14 and P15 runtime fixtures; managed consumer checks only."; } }
