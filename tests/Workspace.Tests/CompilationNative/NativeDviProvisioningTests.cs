using Client.Compilation;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.Compilation.Provisioning;
using Client.RuntimeAssets;
using Microsoft.JSInterop;
using Xunit;

namespace Workspace.Tests.CompilationNative;

// Actual installed507 bytes and C# composition. JS never executes a compiler.
public sealed class NativeDviProvisioningTests
{
    private static byte[] Pins => File.ReadAllBytes(Path.Combine(NativeLatexDviTests.Root, "pins.json"));
    private static LocalRuntimeFile[] Files() => LocalRuntimeSelection.ParsePins(Pins).Select(p => new LocalRuntimeFile(p.Name, p.Length,
        _ => Task.FromResult(File.ReadAllBytes(Path.Combine(NativeLatexDviTests.Root, "files", p.Name))))).ToArray();
    private sealed class PdfSelection : IPdfRuntimeSelectionStore
    {
        public Task<string?> ReadAsync() => Task.FromResult<string?>(null);
        public Task WriteAsync(string text) => throw new InvalidOperationException("No PDF installation used");
    }
    private static LocalInstalledRuntimeProviders Providers(SetupBackend io, ILocalRuntimeSelectionStore selection) =>
        new(new(io), selection, new(new SetupBackend()), new PdfSelection());

    [DviFixtureFact]
    public async Task ZIP507_manual_install_recheck_and_fresh_selection_are_supported()
    {
        var io = new SetupBackend(); var selection = new SetupSelection();
        await using var setup = new NativeRuntimeSetup(new(io), selection);
        await using var session = new ZipRuntimeSetupSession(setup);
        string path = Path.Combine(NativeLatexDviTests.Root, "compiler-dvi.zip");
        await session.InstallZipAsync(new FileInfo(path).Length, _ => Task.FromResult<Stream>(File.OpenRead(path)));
        Assert.Equal(RuntimeSetupState.Installed, session.State);
        var first = LocalRuntimeSelection.Decode(selection.Value!); Assert.Equal(NativeRuntimeProfile.LocalLatex2023Dvi, first.Profile); Assert.Equal(507, first.Resources.Length);
        await session.RecheckAsync(); Assert.Equal(RuntimeSetupState.Installed, session.State);
        await using var provider = Providers(io, selection); var read = await provider.ReadCompilerAsync();
        Assert.Equal(NativeLatexDviTests.Package.Identity, read.Package!.Identity);
        Assert.Equal(RuntimeAssetStatus.Ready, await provider.ConfirmCompilerSelectionAsync(read));
        await session.InstallManualAsync(_ => Task.FromResult(Pins), Files()); Assert.Equal(RuntimeSetupState.Installed, session.State);
        Assert.NotEqual(first.InstallationId, LocalRuntimeSelection.Decode(selection.Value!).InstallationId);
        Assert.Equal(RuntimeAssetStatus.Uncertain, await provider.ConfirmCompilerSelectionAsync(read));
        Assert.NotNull((await provider.ReadCompilerAsync()).Package);
    }

    [DviFixtureFact]
    public async Task Missing_changed_extra_and_duplicate_selection_never_publish_or_read_resources()
    {
        var io = new SetupBackend(); var selection = new SetupSelection();
        await using var setup = new NativeRuntimeSetup(new(io), selection);
        var files = Files(); int reads = 0;
        var observed = files.Select(f => f with { Read = _ => { reads++; throw new Exception("Invalid descriptor must fail before resource IO"); } }).ToArray();
        foreach (var invalid in new[] { observed.Skip(1).ToArray(), observed.Append(observed[0]).ToArray(),
            observed.Select((f, i) => i == 1 ? observed[0] : f).ToArray(),
            observed.Select((f, i) => i == 0 ? f with { Length = f.Length + 1 } : f).ToArray() })
        {
            await setup.InstallAsync(Pins, invalid); Assert.Equal(RuntimeSetupState.InvalidSelection, setup.State);
        }
        Assert.Equal(0, reads); Assert.Equal(0, io.Calls); Assert.Null(selection.Value);
    }

    [DviFixtureFact]
    public async Task Registered_profile4_ports_share_lifetime_inspector_and_recheck_after_both_provisions()
    {
        var io = new SetupBackend(); var selection = new SetupSelection();
        await using var setup = new NativeRuntimeSetup(new(io), selection); await setup.InstallAsync(Pins, Files());
        Assert.Equal(RuntimeSetupState.Installed, setup.State);
        var js = new Runtime(); await using var config = new LocalCompileWorkspaceConfiguration(js, Providers(io, selection));
        var opened = await config.OpenAsync(); Assert.True(opened.Compiler.Ready); var pdf = opened.Ports!;
        var dvi = Assert.Single(opened.AdditionalEngines!);
        Assert.Equal(new[] { "provision", "provisionDvi" }, js.Module.Provisions);
        Assert.Equal("local-authoring-v4", pdf.CapabilityId); Assert.Equal("pdftex", pdf.Engine.Id);
        Assert.Equal(NativeSyncTeXContract.SelectionOptions, pdf.SelectionOptions); Assert.Equal(NativeSyncTeXContract.OutputAdapter, pdf.OutputAdapter);
        Assert.Equal("latex-dvi", dvi.CapabilityId); Assert.Equal("LaTeX (DVI to PDF)", dvi.Label); Assert.Equal(NativeLatexDviTests.Package.DviEngine, dvi.Engine);
        Assert.Equal(NativeLatexDviContract.SelectionOptions, dvi.SelectionOptions); Assert.Equal(NativeLatexDviContract.OutputAdapter, dvi.OutputAdapter);
        Assert.Equal(pdf.Bundles, dvi.Bundles); Assert.Equal(pdf.Tools, dvi.Tools); Assert.True(dvi.SelectableBibliography);
        Assert.Same(pdf.Inspector, dvi.Inspector); Assert.Same(config, dvi.Lifetime); Assert.Same(pdf.Lifetime, dvi.Lifetime);
        Assert.NotSame(pdf.Factory, dvi.Factory);
        // Final selection confirmation occurs after DVI worker provisioning too.
        var original = selection.Value;
        js.Module.AfterDvi = () => selection.Value = null;
        Assert.False((await config.OpenAsync()).Compiler.Ready);
        selection.Value = original; js.Module.AfterDvi = null;
        Assert.Same(dvi, Assert.Single((await config.OpenAsync()).AdditionalEngines!)); Assert.Equal(1, js.Imports);
    }

    [DviFixtureFact]
    public async Task Changed_selection_during_actual_installed_read_never_yields_a_current_package()
    {
        var io = new SetupBackend(); var selection = new SetupSelection();
        await using var setup = new NativeRuntimeSetup(new(io), selection); await setup.InstallAsync(Pins, Files());
        await using var provider = Providers(io, selection);
        io.ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); io.ReadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = provider.ReadCompilerAsync();
        await io.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { selection.Value = null; }
        finally { io.ReadRelease.SetResult(true); }
        var read = await reading; Assert.Equal(RuntimeAssetStatus.Uncertain, read.Store.Status); Assert.Null(read.Package);
    }

    private sealed class Runtime : IJSRuntime
    {
        public readonly Module Module = new(); public int Imports;
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            Assert.Equal("import", identifier); Assert.Equal("./js/compile-port-bridge.js", args![0]); Imports++;
            return ValueTask.FromResult((T)(object)Module);
        }
    }
    private sealed class Module : IJSInProcessObjectReference
    {
        public List<string> Provisions = []; public Action? AfterDvi;
        public T Invoke<T>(string identifier, params object?[]? args) { Assert.Equal("dispose", identifier); return default!; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            Assert.False(token.CanBeCanceled); Assert.Contains(identifier, new[] { "provision", "provisionDvi" });
            Provisions.Add(identifier); if (identifier == "provisionDvi") AfterDvi?.Invoke();
            return ValueTask.FromResult((T)(object)true);
        }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
