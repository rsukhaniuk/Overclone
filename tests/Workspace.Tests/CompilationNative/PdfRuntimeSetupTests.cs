using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Client.Compilation.Provisioning;
using Client.RuntimeAssets;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.CompilationNative;

// Own tiny opaque bytes only: NOT a renderer, native ABI or compatibility proof.
public sealed class PdfRuntimeSetupTests
{
    private static readonly byte[] Loader = "// synthetic opaque managed workflow bytes"u8.ToArray();
    private static readonly byte[] Wasm = [0, 97, 115, 109, 1, 0, 0, 0];
    private static string Hash(byte[] bytes) => Client.Compilation.Native.NativeRuntimePackage.Digest(bytes).Hex;
    internal static byte[] Manifest(byte[] loader, byte[] wasm) => JsonSerializer.SerializeToUtf8Bytes(new { version = 1, resources = new[] {
        new { role = "loader", name = "local.js", length = loader.Length, sha256 = Hash(loader) },
        new { role = "wasm", name = "local.wasm", length = wasm.Length, sha256 = Hash(wasm) } } });
    private static LocalRuntimeFile[] Files() => [new("local.js", Loader.Length, _ => Task.FromResult(Loader)), new("local.wasm", Wasm.Length, _ => Task.FromResult(Wasm))];
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [Fact]
    public async Task Empty_default_is_inert_and_empty_recheck_does_not_touch_runtime_store()
    {
        var io = new SetupBackend(); var selection = new PdfSelection();
        await using var setup = new PdfRuntimeSetup(new(io), selection);
        Assert.Equal(PdfRuntimeSetupState.Empty, setup.State); Assert.False(setup.Busy); Assert.Equal(0, io.Calls); Assert.Equal(0, selection.Reads);
        await setup.RecheckAsync(); Assert.Equal(PdfRuntimeSetupState.Empty, setup.State); Assert.Equal(0, io.Calls);
    }
    [Fact]
    public async Task Pair_install_reopen_freshprovider_and_actual_corruption_missing_bytes_are_distinct()
    {
        var io = new SetupBackend(); var selection = new PdfSelection();
        await using (var setup = new PdfRuntimeSetup(new(io), selection))
        {
            await setup.InstallAsync(Manifest(Loader, Wasm), Files()); Assert.Equal(PdfRuntimeSetupState.Installed, setup.State); Assert.Equal(2, setup.Completed);
        }
        var mapping = PdfRuntimeSelection.Decode(selection.Value!); await using var store = new RuntimeAssetStore(io);
        var provider = await new InstalledPdfRuntimeProvider(store, mapping.TechnicalMapping(), PdfRuntimeSelection.Budget).ReadPackageAsync();
        Assert.True(provider.Package!.IsValid); var copy = provider.Package.CopyWasm(); copy[0] ^= 1; Assert.True(provider.Package.IsValid);
        await using var reopened = new PdfRuntimeSetup(store, selection);
        await reopened.RecheckAsync(); Assert.Equal(PdfRuntimeSetupState.Installed, reopened.State);
        io.Files[mapping.InstallationId][0][0] ^= 1;
        await reopened.RecheckAsync(); Assert.Equal(PdfRuntimeSetupState.Corrupt, reopened.State);
        io.Files[mapping.InstallationId].Remove(0);
        await reopened.RecheckAsync(); Assert.Equal(PdfRuntimeSetupState.Missing, reopened.State);
        io.Files.Remove(mapping.InstallationId);
        await reopened.RecheckAsync(); Assert.Equal(PdfRuntimeSetupState.Missing, reopened.State);
    }
    [Theory]
    [InlineData("version")][InlineData("unknown")][InlineData("duplicateRoot")][InlineData("duplicateField")]
    [InlineData("duplicateRole")][InlineData("unknownRole")][InlineData("duplicateName")][InlineData("path")]
    [InlineData("nameLength")][InlineData("hashUpper")][InlineData("hashLength")][InlineData("loaderZero")]
    [InlineData("loaderLarge")][InlineData("wasmShort")][InlineData("wasmLarge")][InlineData("missingField")]
    [InlineData("extraResource")][InlineData("null")][InlineData("oversized")][InlineData("depth")]
    public async Task Complete_manifest_preflight_rejects_before_resource_or_storage_IO(string kind)
    {
        string valid = Encoding.UTF8.GetString(Manifest(Loader, Wasm)); var root = JsonNode.Parse(valid)!;
        var first = root["resources"]![0]!; var second = root["resources"]![1]!;
        switch (kind)
        {
            case "version": root["version"] = 2; break;
            case "unknown": root["approved"] = true; break;
            case "duplicateRole": second["role"] = "loader"; break;
            case "unknownRole": first["role"] = "script"; break;
            case "duplicateName": second["name"] = "local.js"; break;
            case "path": first["name"] = "../local.js"; break;
            case "nameLength": first["name"] = new string('a', 201); break;
            case "hashUpper": first["sha256"] = Hash(Loader).ToUpperInvariant(); break;
            case "hashLength": first["sha256"] = "bad"; break;
            case "loaderZero": first["length"] = 0; break;
            case "loaderLarge": first["length"] = PdfRuntimeSelection.MaxLoaderBytes + 1; break;
            case "wasmShort": second["length"] = 7; break;
            case "wasmLarge": second["length"] = PdfRuntimeSelection.MaxWasmBytes + 1; break;
            case "missingField": first.AsObject().Remove("sha256"); break;
            case "extraResource": root["resources"]!.AsArray().Add(first.DeepClone()); break;
        }
        var text = kind switch {
            "duplicateRoot" => valid.Replace("\"version\":1", "\"version\":1,\"version\":1"),
            "duplicateField" => valid.Replace("\"role\":\"loader\"", "\"role\":\"loader\",\"role\":\"loader\""),
            "null" => "null", "oversized" => new string(' ', PdfRuntimeSelection.MaxManifestBytes + 1),
            "depth" => "{\"version\":1,\"resources\":[[[[[[[[[[]]]]]]]]]]}", _ => root.ToJsonString() };
        var io = new SetupBackend(); var selection = new PdfSelection { Value = "prior" }; int reads = 0;
        await using var setup = new PdfRuntimeSetup(new(io), selection);
        await setup.InstallAsync(Encoding.UTF8.GetBytes(text), Files().Select(f => f with { Read = _ => { reads++; throw new Exception("Unlisted read"); } }).ToArray());
        Assert.Equal(PdfRuntimeSetupState.InvalidSelection, setup.State); Assert.Equal(0, reads); Assert.Equal(0, io.Calls); Assert.Equal("prior", selection.Value);
    }
    [Theory]
    [InlineData("missing")][InlineData("extra")][InlineData("duplicate")][InlineData("unlisted")][InlineData("length")]
    public async Task Entire_file_selection_checked_before_any_stream(string kind)
    {
        int reads = 0; var files = Files().Select(f => f with { Read = _ => { reads++; throw new Exception(); } }).ToArray();
        var input = kind switch {
            "missing" => files[..1], "extra" => files.Concat([files[0]]).ToArray(),
            "duplicate" => [files[0], files[0]], "unlisted" => [files[0], files[1] with { Name = "other.wasm" }],
            _ => [files[0], files[1] with { Length = 9 }] };
        var io = new SetupBackend(); await using var setup = new PdfRuntimeSetup(new(io), new PdfSelection());
        await setup.InstallAsync(Manifest(Loader, Wasm), input);
        Assert.Equal(PdfRuntimeSetupState.InvalidSelection, setup.State); Assert.Equal(0, reads); Assert.Equal(0, io.Calls);
    }
    [Theory]
    [InlineData("hash")][InlineData("bytes")][InlineData("magic")]
    public async Task Declaredwrong_actualcorrupt_and_selfconsistent_bad_wasm_magic_do_not_publish(string kind)
    {
        var files = Files(); var bytes = Wasm.ToArray(); bytes[0] ^= 1;
        byte[] manifest = Manifest(Loader, Wasm);
        if (kind == "hash") { var node = JsonNode.Parse(manifest)!; node["resources"]![0]!["sha256"] = new string('a',64); manifest = Encoding.UTF8.GetBytes(node.ToJsonString()); }
        else { files[1] = files[1] with { Read = _ => Task.FromResult(bytes) }; if (kind == "magic") manifest = Manifest(Loader, bytes); }
        var io = new SetupBackend(); var selection = new PdfSelection { Value = "prior" };
        await using var setup = new PdfRuntimeSetup(new(io), selection); await setup.InstallAsync(manifest, files);
        Assert.Equal(PdfRuntimeSetupState.Corrupt, setup.State); Assert.Equal(0, io.Calls); Assert.Equal("prior", selection.Value);
    }
    [Theory]
    [InlineData("flag")][InlineData("version")][InlineData("kind")][InlineData("digest")][InlineData("id")]
    [InlineData("duplicateId")][InlineData("role")][InlineData("length")][InlineData("unknownField")][InlineData("oversized")]
    [InlineData("count")][InlineData("path")][InlineData("hash")][InlineData("duplicateRoot")][InlineData("duplicateField")]
    public async Task Persisted_metadata_is_strict_before_any_runtime_reads(string kind)
    {
        var resources = PdfRuntimeSelection.ParseManifest(Manifest(Loader, Wasm)); var inventory = PdfRuntimeSelection.Inventory(resources);
        string valid = PdfRuntimeSelection.Encode(new(Guid.NewGuid(), inventory.Identity.Digest.Hex, resources)); var node = JsonNode.Parse(valid)!;
        switch (kind)
        {
            case "flag": node["approved"] = true; break;
            case "version": node["version"] = 99; break;
            case "kind": node["kind"] = "compiler"; break;
            case "digest": node["inventoryDigest"] = new string('a',64); break;
            case "id": node["installationId"] = new string('0',32); break;
            case "duplicateId": node["resources"]![1]!["assetId"] = node["resources"]![0]!["assetId"]!.GetValue<string>(); break;
            case "role": node["resources"]![0]!["role"] = "wasm"; break;
            case "length": node["resources"]![0]!["length"] = int.MaxValue; break;
            case "unknownField": node["resources"]![0]!["url"] = "https://invalid.example"; break;
            case "count": node["resources"]!.AsArray().RemoveAt(0); break;
            case "path": node["resources"]![0]!["name"] = "../outside"; break;
            case "hash": node["resources"]![0]!["sha256"] = "invalid"; break;
        }
        string text = kind switch {
            "oversized" => new string('x', PdfRuntimeSelection.MaxMetadataBytes + 1),
            "duplicateRoot" => valid.Replace("\"version\":1", "\"version\":1,\"version\":1"),
            "duplicateField" => valid.Replace("\"role\":\"loader\"", "\"role\":\"loader\",\"role\":\"loader\""),
            _ => node.ToJsonString() };
        var selection = new PdfSelection { Value = text };
        var io = new SetupBackend(); await using var setup = new PdfRuntimeSetup(new(io), selection);
        await setup.RecheckAsync(); Assert.Equal(PdfRuntimeSetupState.InvalidSelection, setup.State); Assert.Equal(0, io.Calls);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Cancel_retains_admission_and_observes_late_faulted_selected_reads(bool completedFault)
    {
        var entered = Signal<bool>(); var release = Signal<byte[]>(); var files = Files(); var io = new SetupBackend(); var selection = new PdfSelection { Value = "prior" };
        await using var setup = new PdfRuntimeSetup(new(io), selection);
        files[0] = files[0] with { Read = _ => { entered.SetResult(true); if (completedFault) { setup.Cancel(); return Task.FromException<byte[]>(new IOException("own bounded read failure")); } return release.Task; } };
        var install = setup.InstallAsync(Manifest(Loader, Wasm), files); await entered.Task;
        if (!completedFault)
        {
            setup.Cancel(); await Task.Yield(); Assert.True(setup.Busy);
            await setup.InstallAsync(Manifest(Loader, Wasm), Files()); await setup.RecheckAsync(); Assert.Equal(0, selection.Reads);
            release.SetException(new IOException("late owned failure"));
        }
        await install; Assert.False(setup.Busy); Assert.Equal(PdfRuntimeSetupState.Cancelled, setup.State); Assert.Equal(0, io.Calls); Assert.Equal("prior", selection.Value);
    }
    [Fact]
    public async Task Caller_descriptors_and_bytes_cannot_change_owned_receipt_during_store_IO()
    {
        var io = new SetupBackend { WriteEntered = Signal<bool>(), WriteRelease = Signal<bool>() }; var selection = new PdfSelection();
        var loader = Loader.ToArray(); var wasm = Wasm.ToArray(); var files = new[] { new LocalRuntimeFile("local.js", loader.Length, _ => Task.FromResult(loader)), new LocalRuntimeFile("local.wasm", wasm.Length, _ => Task.FromResult(wasm)) };
        var manifest = Manifest(loader, wasm); await using var setup = new PdfRuntimeSetup(new(io), selection);
        var install = setup.InstallAsync(manifest, files); await io.WriteEntered.Task;
        Array.Fill(manifest, (byte)0); loader[0] ^= 1; wasm[0] ^= 1; files[0] = files[0] with { Name = "other", Read = _ => throw new Exception() };
        Assert.True(setup.Busy); Assert.False(setup.CanCancel); setup.Cancel(); io.WriteRelease.SetResult(true); await install;
        Assert.Equal(PdfRuntimeSetupState.Installed, setup.State); await setup.RecheckAsync(); Assert.Equal(PdfRuntimeSetupState.Installed, setup.State);
    }
    [Fact]
    public async Task Descriptor_mutation_during_preparation_does_not_redirect_later_reads()
    {
        var entered = Signal<bool>(); var release = Signal<bool>(); var files = Files(); var first = files[0]; var manifest = Manifest(Loader, Wasm);
        files[0] = first with { Read = async token => { entered.SetResult(true); await release.Task; return await first.Read(token); } };
        await using var setup = new PdfRuntimeSetup(new(new SetupBackend()), new PdfSelection());
        var install = setup.InstallAsync(manifest, files); await entered.Task;
        Array.Fill(manifest,(byte)0); files[1] = new("unlisted",1,_ => throw new Exception("Caller redirect"));
        release.SetResult(true); await install; Assert.Equal(PdfRuntimeSetupState.Installed,setup.State);
    }
    [Theory]
    [InlineData(RuntimeAssetStatus.QuotaExceeded, PdfRuntimeSetupState.QuotaExceeded)]
    [InlineData(RuntimeAssetStatus.PermissionDenied, PdfRuntimeSetupState.PermissionDenied)]
    [InlineData(RuntimeAssetStatus.Unavailable, PdfRuntimeSetupState.Unavailable)]
    [InlineData(RuntimeAssetStatus.StorageFailure, PdfRuntimeSetupState.StorageFailure)]
    public async Task Failed_replacement_preserves_previous_selection(RuntimeAssetStatus failure, PdfRuntimeSetupState expected)
    {
        var io = new SetupBackend(); var selection = new PdfSelection(); await using var setup = new PdfRuntimeSetup(new(io), selection);
        await setup.InstallAsync(Manifest(Loader, Wasm), Files()); var prior = selection.Value;
        io.Failure = failure; await setup.InstallAsync(Manifest(Loader, Wasm), Files());
        Assert.Equal(expected, setup.State); Assert.Equal(prior, selection.Value); Assert.Equal(1, selection.Writes);
        io.Failure = null; await setup.RecheckAsync(); Assert.Equal(PdfRuntimeSetupState.Installed, setup.State);
    }
    [Fact]
    public async Task Storage_busy_and_recheck_cancel_keep_real_admission_until_settlement()
    {
        var io = new SetupBackend { Locked = true }; var selection = new PdfSelection(); var store = new RuntimeAssetStore(io);
        await using var setup = new PdfRuntimeSetup(store, selection);
        await setup.InstallAsync(Manifest(Loader, Wasm), Files()); Assert.Equal(PdfRuntimeSetupState.Busy, setup.State); Assert.Null(selection.Value);
        io.Locked = false; await setup.InstallAsync(Manifest(Loader, Wasm), Files()); var prior = selection.Value;
        io.ReadEntered = Signal<bool>(); io.ReadRelease = Signal<bool>(); var recheck = setup.RecheckAsync(); await io.ReadEntered.Task;
        setup.Cancel(); await Task.Yield(); Assert.True(setup.Busy); Assert.False(store.WhenSettled.IsCompleted);
        int calls = io.Calls; await setup.InstallAsync(Manifest(Loader, Wasm), Files()); Assert.Equal(calls, io.Calls);
        io.ReadRelease.SetResult(true); await recheck; Assert.Equal(PdfRuntimeSetupState.Cancelled, setup.State); Assert.Equal(prior, selection.Value);
    }
    [Fact]
    public async Task Disposal_is_prompt_and_suppresses_late_selectedread_and_store_callbacks()
    {
        var gate = Signal<byte[]>(); var entered = Signal<bool>(); var files = Files(); files[0] = files[0] with { Read = _ => { entered.SetResult(true); return gate.Task; } };
        var io = new SetupBackend(); var selection = new PdfSelection(); var setup = new PdfRuntimeSetup(new(io), selection);
        var install = setup.InstallAsync(Manifest(Loader, Wasm), files); await entered.Task;
        await setup.DisposeAsync(); int callbacks = 0; setup.Changed += () => callbacks++;
        gate.SetException(new IOException("late disposal read")); await install; Assert.Equal(0, callbacks); Assert.Null(selection.Value);
        var held = new SetupBackend { WriteEntered = Signal<bool>(), WriteRelease = Signal<bool>() }; var other = new PdfRuntimeSetup(new(held), new PdfSelection());
        var writing = other.InstallAsync(Manifest(Loader, Wasm), Files()); await held.WriteEntered.Task;
        await other.DisposeAsync(); other.Changed += () => callbacks++; Assert.True(other.Busy); held.WriteRelease.SetResult(true); await writing;
        Assert.False(held.Locked); Assert.Equal(0, callbacks);
    }
    [Fact]
    public async Task Uncertain_runtime_publication_preserves_old_selection_and_never_scans_or_adopts()
    {
        var io = new SetupBackend(); var selection = new PdfSelection(); await using var setup = new PdfRuntimeSetup(new(io), selection);
        await setup.InstallAsync(Manifest(Loader, Wasm), Files()); var prior = selection.Value; io.FailAfterPublish = true;
        await setup.InstallAsync(Manifest(Loader, Wasm), Files()); Assert.Equal(PdfRuntimeSetupState.Uncertain, setup.State);
        Assert.Equal(prior, selection.Value); Assert.Equal(2, io.Files.Count); Assert.Equal(1, selection.Writes);
        await setup.RecheckAsync(); Assert.Equal(PdfRuntimeSetupState.Installed, setup.State); Assert.Equal(prior, selection.Value);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Uncertain_selection_commit_has_no_rollback_promise_and_explicit_recheck_reconciles(bool committed)
    {
        var selection = new PdfSelection(); await using var setup = new PdfRuntimeSetup(new(new SetupBackend()), selection);
        await setup.InstallAsync(Manifest(Loader, Wasm), Files()); var old = selection.Value;
        selection.Entered = Signal<bool>(); selection.Release = Signal<bool>(); selection.Fail = true; selection.CommitOnFailure = committed;
        var installing = setup.InstallAsync(Manifest(Loader, Wasm), Files()); await selection.Entered.Task;
        Assert.True(setup.Busy); Assert.False(setup.CanCancel); setup.Cancel(); selection.Release.SetResult(true); await installing;
        Assert.Equal(PdfRuntimeSetupState.Uncertain, setup.State);
        Assert.Equal(committed, selection.Value != old);
        await setup.RecheckAsync(); Assert.Equal(PdfRuntimeSetupState.Installed, setup.State);
    }
    [Fact]
    public async Task Browser_adapters_use_only_fixed_separate_operations_and_validate_before_import()
    {
        var js = new SelectionJs(); var native = new JsLocalRuntimeSelectionStore(js); var pdf = new JsPdfRuntimeSelectionStore(js);
        Assert.Null(await pdf.ReadAsync()); Assert.Equal("native-selection", await native.ReadAsync());
        int calls = js.Imports; await Assert.ThrowsAsync<FormatException>(() => pdf.WriteAsync("{}")); Assert.Equal(calls, js.Imports);
        var resources = PdfRuntimeSelection.ParseManifest(Manifest(Loader, Wasm)); string record = PdfRuntimeSelection.Encode(new(Guid.NewGuid(), PdfRuntimeSelection.Inventory(resources).Identity.Digest.Hex, resources));
        await pdf.WriteAsync(record); Assert.Equal(record, await pdf.ReadAsync()); Assert.Equal("native-selection", await native.ReadAsync());
        Assert.DoesNotContain(js.Operations, op => op == "writeSelection");
    }
    private sealed class SelectionJs : IJSRuntime, IJSObjectReference
    {
        public int Imports; public string? Pdf; public List<string> Operations = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args)
        {
            if (identifier == "import") { Imports++; Assert.Equal("./js/native-runtime-selection.js", args![0]); return ValueTask.FromResult((TValue)(object)this); }
            Operations.Add(identifier); object? value = identifier switch { "readPdfSelection" => Pdf, "readSelection" => "native-selection", "writePdfSelection" => null, _ => throw new Exception("Unexpected adapter operation") };
            if (identifier == "writePdfSelection") Pdf = (string)args![0]!;
            return ValueTask.FromResult(value is null ? default! : (TValue)value);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
internal sealed class PdfSelection : IPdfRuntimeSelectionStore
{
    public string? Value; public int Reads, Writes; public bool Fail, CommitOnFailure;
    public TaskCompletionSource<bool>? Entered, Release;
    public Task<string?> ReadAsync() { Reads++; return Task.FromResult(Value); }
    public async Task WriteAsync(string value)
    {
        Writes++; Entered?.TrySetResult(true); if (Release is not null) await Release.Task;
        if (!Fail || CommitOnFailure) Value = value;
        if (Fail) throw new IOException("Modeled metadata commit uncertainty");
    }
}
public sealed class PdfRuntimeFixtureFactAttribute : FactAttribute
{
    public PdfRuntimeFixtureFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PDF_RUNTIME_DIR"))) Skip = "Explicit PDF_RUNTIME_DIR E1 byte fixture required; no renderer shipped/executed."; }
}
public sealed class PdfRuntimeActualPairTests
{
    [PdfRuntimeFixtureFact]
    public async Task Explicit_cached_E1_pair_installs_and_reopens_as_opaque_bytes_only()
    {
        var root = Environment.GetEnvironmentVariable("PDF_RUNTIME_DIR")!; var manifest = File.ReadAllBytes(Path.Combine(root,"manifest.json"));
        var resources = PdfRuntimeSelection.ParseManifest(manifest); var files = resources.Select(r => new LocalRuntimeFile(r.Name, r.Length, _ => Task.FromResult(File.ReadAllBytes(Path.Combine(root,"files",r.Name))))).ToArray();
        var io = new SetupBackend(); var selection = new PdfSelection();
        await using (var setup = new PdfRuntimeSetup(new(io),selection)) { await setup.InstallAsync(manifest,files); Assert.Equal(PdfRuntimeSetupState.Installed,setup.State); }
        var record = PdfRuntimeSelection.Decode(selection.Value!); await using var store = new RuntimeAssetStore(io);
        var result = await new InstalledPdfRuntimeProvider(store,record.TechnicalMapping(),PdfRuntimeSelection.Budget).ReadPackageAsync();
        Assert.True(result.Package!.IsValid);
        Assert.Equal("040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b",result.Package.LoaderSha256);
        Assert.Equal("ea5a9a81d6e200fb59ccaced8ecd510d84999c648acf4a13e4ebbef07b1e2e8b",result.Package.WasmSha256);
        await using var reopened = new PdfRuntimeSetup(store,selection); await reopened.RecheckAsync(); Assert.Equal(PdfRuntimeSetupState.Installed,reopened.State);
    }
}
