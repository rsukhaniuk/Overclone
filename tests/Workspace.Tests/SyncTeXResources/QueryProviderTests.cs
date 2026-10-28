using System.Reflection;
using Client.Compilation;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.Compilation.SyncTeX;
using Client.RuntimeAssets;
using Workspace.Tests.CompilationNative;
using Xunit;

namespace Workspace.Tests.SyncTeXResources;

public sealed class QueryProviderTests
{
    [SyncTeXRuntimeFact]
    public void Actual499_package_owns_new_bytes_identity_descriptors_and_excludes_query_assets_from_compiler_VFS()
    {
        var fixture = ActualSyncTeXResources.Fixture.Value;
        var result = NativeRuntimePackage.Create(fixture.Inputs, fixture.Pins,
            LocalRuntimeSelection.LimitsFor(SyncTeXResourceFixture.Profile), SyncTeXResourceFixture.Profile);
        var package = Assert.IsType<NativeRuntimePackage>(result.Package);
        Assert.Equal(SyncTeXResourceFixture.Identity, package.Identity.Hex);
        Assert.Equal(new CompilationDescriptor("pdftex", "1.40.21", new(NativeRuntimeProfiles.SyncTeXCompilerWasmHash)), package.Engine);
        Assert.Equal(new CompilationDescriptor("bibtex", "0.99d", new(NativeRuntimeProfiles.SyncTeXCompilerWasmHash)), package.Bibtex);
        Assert.Equal(new CompilationDescriptor("local-latex-synctex", "2023-11-01-p1-synctex-profile-v1", package.Identity), package.Bundle);
        Assert.Equal(new CompileOutputDescriptor("native-pdftex", "n11-synctex-v1", new(NativeRuntimeProfiles.SyncTeXCompilerWasmHash)), package.OutputAdapter);
        Assert.Equal(new CompileOutputDescriptor("native-pdftex", "n11-v1", new(NativeRuntimePackage.WasmHash)), NativeRuntimePackage.Adapter);
        foreach (var pin in fixture.Pins)
        {
            var bytes = package.Copy(pin.Name);
            Assert.Equal(pin.Length, bytes.Length); Assert.Equal(pin.Sha256, NativeRuntimePackage.Digest(bytes).Hex);
            if (bytes.Length > 0) { bytes[0] ^= 1; Assert.Equal(pin.Sha256, NativeRuntimePackage.Digest(package.Copy(pin.Name)).Hex); }
        }
        // Inspect the existing internal phase projection, not an invented test VFS.
        var files = Assert.IsType<NativeFileTransfer[]>(typeof(NativeRuntimePackage).GetMethod("CopyFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(package, [false]));
        var lengths = Assert.IsAssignableFrom<IEnumerable<(string Name, int Length)>>(typeof(NativeRuntimePackage)
            .GetProperty("FileLengths", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(package)).ToArray();
        Assert.Equal(495, files.Length); Assert.Equal(495, lengths.Length);
        Assert.DoesNotContain(files, f => f.Name is "engine.js" or "engine.wasm" or "synctex-query.js" or "synctex-query.wasm" or "pdflatex.fmt");
        Assert.Equal(NativeRuntimeProfiles.SyncTeXFormatLength, Assert.Single(files, f => f.Name == NativeRuntimePackage.NativeFormatName).Bytes.Length);
        Assert.Equal(files.Select(f => (f.Name, f.Bytes.Length)).OrderBy(f => f.Name), lengths.OrderBy(f => f.Name));
        Assert.Equal(NativeRuntimeIssue.ResourceLimit, NativeRuntimePackage.Create(fixture.Inputs, fixture.Pins,
            LocalRuntimeSelection.Limits, LocalRuntimeSelection.Profile).Issue);
    }

    [SyncTeXRuntimeFact]
    public async Task Actual499_manual_install_reopen_and_query_provider_verify_fresh_full_package_each_time()
    {
        var backend = new SetupBackend(); var selection = new Selection();
        await using (var session = new ZipRuntimeSetupSession(new(new(backend), selection)))
        {
            await session.InstallManualAsync(_ => Task.FromResult(ActualSyncTeXResources.Fixture.Value.Json), ActualSyncTeXResources.Files());
            Assert.Equal(RuntimeSetupState.Installed, session.State); Assert.Equal(499, session.Completed);
            await session.RecheckAsync(); Assert.Equal(RuntimeSetupState.Installed, session.State);
        }
        var mapping = LocalRuntimeSelection.Decode(selection.Text!); Assert.Equal(SyncTeXResourceFixture.Profile, mapping.Profile);
        var pdf = new SetupBackend(); await using var providers = Providers(backend, selection, pdf);
        ISyncTeXRuntimeProvider queries = providers;
        var runtime = Assert.IsType<SyncTeXRuntimePackage>(await queries.GetAsync(default));
        Assert.True(runtime.IsValid); Assert.Equal(NativeRuntimeProfiles.SyncTeXQueryLoaderHash, runtime.LoaderSha256);
        Assert.Equal(NativeRuntimeProfiles.SyncTeXQueryWasmHash, runtime.WasmSha256);
        Assert.Equal(NativeRuntimeProfiles.SyncTeXQueryLoaderLength, runtime.CopyLoader().Length);
        Assert.Equal(NativeRuntimeProfiles.SyncTeXQueryWasmLength, runtime.CopyWasm().Length);
        var copy = runtime.CopyWasm(); copy[0] ^= 1; Assert.True(runtime.IsValid);
        int calls = backend.Calls; Assert.NotNull(await queries.GetAsync(default)); Assert.True(backend.Calls > calls);
        // Corruption of an unrelated compiler resource still invalidates queries:
        // this provider verifies the whole exact499, not only the two query files.
        var resource = mapping.Resources.First(r => r.Pin.Name == "pdflatex.fmt");
        int index = Array.FindIndex(LocalRuntimeSelection.Inventory(mapping.Resources).Assets.ToArray(), a => a.AssetId == resource.AssetId);
        backend.Files[mapping.InstallationId][index][0] ^= 1;
        Assert.Null(await queries.GetAsync(default));
        backend.Files[mapping.InstallationId][index][0] ^= 1;
        Assert.NotNull(await queries.GetAsync(default));
        backend.Files[mapping.InstallationId].Remove(index); Assert.Null(await queries.GetAsync(default));
        Assert.Equal(0, pdf.Calls);
    }

    [AuthoringRuntimeSetupFact]
    public async Task Actual_old497_install_keeps_old_identity_adapter_and_has_no_query_capability()
    {
        var root = Environment.GetEnvironmentVariable("AUTHORING_RUNTIME_DIR")!;
        var json = File.ReadAllBytes(Path.Combine(root, "pins.json")); var pins = LocalRuntimeSelection.ParsePins(json);
        Assert.Equal(NativeRuntimeProfile.LocalLatex2023Authoring, LocalRuntimeSelection.ProfileFor(pins));
        var files = pins.Select(p => new LocalRuntimeFile(p.Name, p.Length, _ => Task.FromResult(File.ReadAllBytes(Path.Combine(root, "files", p.Name))))).ToArray();
        var backend = new SetupBackend(); var selection = new Selection();
        await using (var setup = new NativeRuntimeSetup(new(backend), selection))
        { await setup.InstallAsync(json, files); Assert.Equal(RuntimeSetupState.Installed, setup.State); }
        await using var providers = Providers(backend, selection);
        var package = Assert.IsType<NativeRuntimePackage>(await providers.GetAsync());
        Assert.Equal("8e9071ff18e5f93203266e7a85a6c4bba0597f9fba2e692c5651f68997e31894", package.Identity.Hex);
        Assert.Equal(NativeRuntimePackage.Adapter, package.OutputAdapter);
        Assert.Equal(NativeRuntimePackage.WasmHash, package.Engine.Hash.Hex);
        Assert.Null(await ((ISyncTeXRuntimeProvider)providers).GetAsync(default));
        Assert.NotNull(await providers.GetAsync());
    }

    [Fact]
    public async Task No_selection_invalid_selection_and_missing_installation_return_no_query_without_fallback()
    {
        var backend = new SetupBackend(); var pdf = new SetupBackend(); var selection = new Selection();
        await using var providers = Providers(backend, selection, pdf);
        ISyncTeXRuntimeProvider queries = providers;
        Assert.Null(await queries.GetAsync(default)); Assert.Equal(0, backend.Calls);
        selection.Text = "{}"; Assert.Null(await queries.GetAsync(default)); Assert.Equal(0, backend.Calls);
        foreach (var pins in new[] { SyncTeXResourceFixture.OldPins(), SyncTeXResourceFixture.Pins() })
        {
            var resources = LocalRuntimeSelection.Resources(System.Collections.Immutable.ImmutableArray.CreateRange(pins));
            selection.Text = LocalRuntimeSelection.Encode(new(Guid.NewGuid(), LocalRuntimeSelection.Inventory(resources).Identity.Digest.Hex,
                resources, LocalRuntimeSelection.ProfileFor(pins)));
            Assert.Null(await queries.GetAsync(default));
        }
        Assert.Equal(0, pdf.Calls);
    }

    [Fact]
    public async Task Query_and_compiler_share_admission_through_cancelled_unfinished_metadata_read()
    {
        var backend = new SetupBackend(); var selection = new Selection { Entered = SyncTeXResourceFixture.Signal<bool>(), Release = SyncTeXResourceFixture.Signal<bool>() };
        var providers = Providers(backend, selection); using var cancellation = new CancellationTokenSource();
        var query = ((ISyncTeXRuntimeProvider)providers).GetAsync(cancellation.Token).AsTask();
        try
        {
            await selection.Entered.Task; cancellation.Cancel(); Assert.Null(await query);
            Assert.False(providers.CompilerWhenSettled.IsCompleted);
            Assert.Equal(RuntimeAssetStatus.Busy, (await providers.ReadCompilerAsync()).Store.Status);
            Assert.Null(await ((ISyncTeXRuntimeProvider)providers).GetAsync(default));
            Assert.Equal(1, selection.Reads); Assert.Equal(0, backend.Calls);
            await providers.DisposeAsync(); Assert.False(providers.WhenSettled.IsCompleted);
        }
        finally { selection.Release.TrySetResult(true); await providers.WhenSettled; await providers.DisposeAsync(); }
    }

    [SyncTeXRuntimeFact]
    public async Task Query_selection_change_and_cancelled_actual_byte_read_never_publish_a_late_package()
    {
        var backend = new SetupBackend(); var selection = new Selection();
        await using (var setup = new NativeRuntimeSetup(new(backend), selection))
        { await setup.InstallAsync(ActualSyncTeXResources.Fixture.Value.Json, ActualSyncTeXResources.Files()); Assert.Equal(RuntimeSetupState.Installed, setup.State); }
        var prior = selection.Text; await using var providers = Providers(backend, selection);
        ISyncTeXRuntimeProvider queries = providers;
        backend.ReadEntered = SyncTeXResourceFixture.Signal<bool>(); backend.ReadRelease = SyncTeXResourceFixture.Signal<bool>();
        var changing = queries.GetAsync(default).AsTask();
        try { await backend.ReadEntered.Task; selection.Text = null; }
        finally { backend.ReadRelease.TrySetResult(true); }
        Assert.Null(await changing); await providers.CompilerWhenSettled;
        selection.Text = prior; backend.ReadEntered = SyncTeXResourceFixture.Signal<bool>(); backend.ReadRelease = SyncTeXResourceFixture.Signal<bool>();
        using var cancellation = new CancellationTokenSource(); var pending = queries.GetAsync(cancellation.Token).AsTask();
        try
        {
            await backend.ReadEntered.Task; cancellation.Cancel(); Assert.Null(await pending);
            Assert.False(providers.CompilerWhenSettled.IsCompleted);
            Assert.Equal(RuntimeAssetStatus.Busy, (await providers.ReadCompilerAsync()).Store.Status);
        }
        finally { backend.ReadRelease.TrySetResult(true); await providers.CompilerWhenSettled; backend.ReadEntered = null; backend.ReadRelease = null; }
        Assert.NotNull(await queries.GetAsync(default));
    }

    private static LocalInstalledRuntimeProviders Providers(SetupBackend backend, Selection selection, SetupBackend? pdf = null)
        => new(new(backend), selection, new(pdf ?? new SetupBackend()), new Selection());
    private sealed class Selection : ILocalRuntimeSelectionStore, IPdfRuntimeSelectionStore
    {
        internal string? Text; internal int Reads;
        internal TaskCompletionSource<bool>? Entered, Release;
        public async Task<string?> ReadAsync() { Reads++; Entered?.TrySetResult(true); if (Release is not null) await Release.Task; return Text; }
        public Task WriteAsync(string metadata) { Text = metadata; return Task.CompletedTask; }
    }
}
