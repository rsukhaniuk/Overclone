using System.Text;
using System.Text.Json;
using Client.Compilation;
using System.Collections.Immutable;
using Client.Compilation.Assets;
using Client.Compilation.Native;
using Client.Compilation.Lifecycle;
using Client.Compilation.Presentation;
using Client.Compilation.Provisioning;
using Client.Pdf;
using Client.RuntimeAssets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Workspace.Tests.CompilationLifecycle;
using Xunit;

namespace Workspace.Tests.CompilationNative;

// Managed composition/lifetime boundaries only. JS fakes NEVER return a native/PDF success.
public sealed class LocalAuthoringConfigurationTests
{
    [Fact]
    public async Task Empty_installation_has_no_native_import_and_old_configuration_remains_unchanged()
    {
        var js = new Runtime(); var io = new SetupBackend(); var selection = new Selection();
        await using var config = new LocalCompileWorkspaceConfiguration(js, new(new(io), selection, new(new SetupBackend()), new Selection()));
        var result = await config.OpenAsync(); Assert.False(result.Compiler.Ready); Assert.Null(result.Ports); Assert.Equal("Missing", result.Compiler.Status);
        Assert.Equal(0, js.Imports); Assert.Equal(0, io.Calls);
        var legacy = await new UnavailableCompileWorkspaceConfiguration().OpenAsync(); Assert.False(legacy.Compiler.Ready); Assert.Null(legacy.Ports);
        Assert.Equal(TimeSpan.FromSeconds(25), LocalCompileWorkspaceConfiguration.JobDeadline);
    }
    [Fact]
    public async Task Explicit_local_registration_is_lazy_and_trusted_test_override_is_preserved()
    {
        var js = new Runtime();
        await using var services = new ServiceCollection().AddSingleton<IJSRuntime>(js).AddAuthoringWorkspace().BuildServiceProvider();
        Assert.IsType<LocalCompileWorkspaceConfiguration>(services.GetRequiredService<ICompileWorkspaceConfiguration>()); Assert.Equal(0, js.Imports);
        var old = new UnavailableCompileWorkspaceConfiguration();
        await using var overridden = new ServiceCollection().AddSingleton<IJSRuntime>(js).AddSingleton<ICompileWorkspaceConfiguration>(old).AddAuthoringWorkspace().BuildServiceProvider();
        Assert.Same(old, overridden.GetRequiredService<ICompileWorkspaceConfiguration>());
    }
    [Theory]
    [InlineData("{}")][InlineData("{\"approved\":true}")][InlineData("null")]
    public async Task Invalid_compiler_selection_rejects_before_bytes_or_native_JS(string text)
    {
        var js = new Runtime(); var io = new SetupBackend();
        await using var config = new LocalCompileWorkspaceConfiguration(js, new(new(io), new Selection { Text = text }, new(new SetupBackend()), new Selection()));
        var result = await config.OpenAsync(); Assert.False(result.Compiler.Ready); Assert.Equal("Corrupt", result.Compiler.Status); Assert.Equal(0, js.Imports); Assert.Equal(0, io.Calls);
    }
    [Fact]
    public async Task Arbitrary_selfconsistent_PDF_manifest_hashes_are_not_application_trust_before_IO()
    {
        byte[] loader = "// own opaque test bytes"u8.ToArray(), wasm = [0,97,115,109,1,0,0,0];
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, resources = new[] {
            new { role="loader",name="e1.js",length=loader.Length,sha256=NativeRuntimePackage.Digest(loader).Hex },
            new { role="wasm",name="e1.wasm",length=wasm.Length,sha256=NativeRuntimePackage.Digest(wasm).Hex } } });
        var resources = PdfRuntimeSelection.ParseManifest(manifest); var inventory = PdfRuntimeSelection.Inventory(resources);
        var selection = new Selection { Text = PdfRuntimeSelection.Encode(new(Guid.NewGuid(),inventory.Identity.Digest.Hex,resources)) }; var io = new SetupBackend();
        await using var providers = new LocalInstalledRuntimeProviders(new(new SetupBackend()),new Selection(),new(io),selection);
        var read = await providers.ReadPdfAsync(); Assert.Null(read.Package); Assert.Equal(RuntimeAssetStatus.Corrupt,read.Store.Status); Assert.Equal(0,io.Calls);
        Assert.False((await providers.ReadCompilerAsync()).Package is not null); // renderer cannot provide a compiler capability
    }
    [Fact]
    public async Task Cancelled_foreign_metadata_read_and_late_fault_keep_actual_admission_until_settlement()
    {
        var selection = new Selection { Entered = Signal<bool>(), Release = Signal<bool>(), Fault = true }; var io = new SetupBackend();
        var providers = new LocalInstalledRuntimeProviders(new(io),selection,new(new SetupBackend()),new Selection()); using var cancel = new CancellationTokenSource();
        var first = providers.ReadCompilerAsync(cancel.Token);
        try
        {
            await selection.Entered.Task; cancel.Cancel(); Assert.Equal(RuntimeAssetStatus.Cancelled,(await first).Store.Status);
            Assert.Equal(RuntimeAssetStatus.Busy,(await providers.ReadCompilerAsync()).Store.Status); Assert.False(providers.CompilerWhenSettled.IsCompleted);
            await providers.DisposeAsync(); Assert.Equal(RuntimeAssetStatus.Disposed,(await providers.ReadCompilerAsync()).Store.Status);
        }
        finally { selection.Release.SetResult(true); await providers.WhenSettled; await providers.DisposeAsync(); }
        Assert.Equal(0,io.Calls);
    }

    [Theory]
    [InlineData(RuntimeAssetStatus.PermissionDenied)][InlineData(RuntimeAssetStatus.QuotaExceeded)]
    [InlineData(RuntimeAssetStatus.Unavailable)][InlineData(RuntimeAssetStatus.Busy)]
    public async Task Typed_storage_failures_are_actionable_without_native_import(RuntimeAssetStatus status)
    {
        var js=new Runtime();await using var config=new LocalCompileWorkspaceConfiguration(js,new(new(new SetupBackend()),new Selection {Failure=status},new(new SetupBackend()),new Selection()));
        var result=await config.OpenAsync();Assert.False(result.Compiler.Ready);Assert.Equal(status.ToString(),result.Compiler.Status);Assert.Equal(0,js.Imports);
    }
    [Fact]
    public async Task Job_plan_fits_actual_unchanged_app_lifecycle_deadline()
    {
        await using var fixture = await CompilationTestWorkspace.Open();
        await using var session = new CompileWorkspaceSession(fixture.Workspace, new UnavailableCompileWorkspaceConfiguration());
        var property=typeof(CompileWorkspaceSession).GetProperty("Options",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        var options=Assert.IsType<Client.Compilation.Lifecycle.CompileLifecycleOptions>(property.GetValue(session));
        var plan=NativeSelectedEntryContract.CreatePlan(deadline:LocalCompileWorkspaceConfiguration.JobDeadline);
        Assert.Equal(TimeSpan.FromSeconds(30),options.Deadline);Assert.Equal(TimeSpan.FromSeconds(25),plan.JobDeadline);
        Assert.Equal(6,plan.MaxLatexPasses);Assert.True(plan.JobDeadline<options.Deadline);Assert.Equal(5,plan.EffectivePhaseVersion);
    }
    [LocalAuthoringFact]
    public async Task Fresh_fixed_packages_descriptors_and_v5_options_without_renderer_are_independent()
    {
        var f=await Fixture.Value; var js=new Runtime(); var native=new Selection {Text=f.Compiler}; var pdf=new Selection();
        await using var config=new LocalCompileWorkspaceConfiguration(js,Providers(f,native,pdf));
        var result=await config.OpenAsync(); Assert.True(result.Compiler.Ready); var ports=Assert.IsType<CompileWorkspacePorts>(result.Ports);
        Assert.Equal("pdftex",ports.Engine.Id); Assert.Equal("local-latex-authoring",Assert.Single(ports.Bundles).Id);
        Assert.Equal("bibtex",Assert.Single(ports.Tools).Id); Assert.True(ports.SelectableBibliography); Assert.Equal(NativeSelectedEntryContract.SelectionOptions,ports.SelectionOptions);
        var preview=await ports.Inspector!.InspectAsync(Request()); Assert.Equal(PdfInspectionStatus.Unavailable,preview.Status); Assert.Equal("RuntimeMissing",preview.Code);
        Assert.Equal(0,js.PdfImports); Assert.True((await ports.Readiness()).Ready); Assert.Equal(1,js.Imports); Assert.Equal(2,js.Module.Provisions);
        Assert.Same(ports,(await config.OpenAsync()).Ports);
    }
    [LocalAuthoringFact]
    public async Task Removal_corruption_and_valid_replacement_are_fresh_on_every_readiness_and_execution_read()
    {
        var f=await Fixture.Value; var selection=new Selection {Text=f.Compiler}; var resources=Providers(f,selection,new Selection()); var js=new Runtime();
        await using var config=new LocalCompileWorkspaceConfiguration(js,resources);
        Assert.True((await config.OpenAsync()).Compiler.Ready); selection.Text=null; Assert.Equal("Missing",(await config.CheckAsync()).Status);
        Assert.Null(await resources.GetAsync()); selection.Text="{}"; Assert.Equal("Corrupt",(await config.CheckAsync()).Status);
        selection.Text=f.Compiler; var mapping=LocalRuntimeSelection.Decode(f.Compiler); var bytes=f.Native.Files[mapping.InstallationId][0]; var original=bytes[0];
        try { bytes[0]^=1; Assert.Equal("Corrupt",(await config.CheckAsync()).Status); Assert.Null(await resources.GetAsync()); }
        finally { bytes[0]=original; }
        selection.Text=f.Replacement; Assert.True((await config.CheckAsync()).Ready);
        Assert.NotNull(await resources.GetAsync()); Assert.Equal(1,js.Imports); Assert.Equal(0,js.Module.GlobalDisposals);
    }
    [LocalAuthoringFact]
    public async Task Selection_change_during_actual_byte_read_is_not_claimed_current()
    {
        var f=await Fixture.Value; var selection=new Selection {Text=f.Compiler}; var io=f.Native;
        var providers=Providers(f,selection,new Selection()); io.ReadEntered=Signal<bool>();io.ReadRelease=Signal<bool>();
        var check=providers.ReadCompilerAsync();
        try { await io.ReadEntered.Task; selection.Text=f.Replacement; Assert.Equal(RuntimeAssetStatus.Busy,(await providers.ReadCompilerAsync()).Store.Status); }
        finally {io.ReadRelease.SetResult(true);}
        var result=await check; Assert.Null(result.Package);Assert.Equal(RuntimeAssetStatus.Uncertain,result.Store.Status);
        io.ReadEntered=null;io.ReadRelease=null;
        Assert.NotNull((await providers.ReadCompilerAsync()).Package);await providers.DisposeAsync();await providers.WhenSettled;
    }
    [LocalAuthoringFact]
    public async Task Selection_change_during_provision_is_rechecked_before_ready_without_another_full_load()
    {
        var f=await Fixture.Value;var selection=new Selection {Text=f.Compiler};var js=new Runtime();js.Module.Entered=Signal<bool>();js.Module.Release=Signal<bool>();
        await using var config=new LocalCompileWorkspaceConfiguration(js,Providers(f,selection,new Selection()));
        var opening=config.OpenAsync();await js.Module.Entered.Task;selection.Text=f.Replacement;
        js.Module.Release.SetResult(true);var result=await opening;Assert.False(result.Compiler.Ready);Assert.Equal("Uncertain",result.Compiler.Status);
        Assert.True((await config.CheckAsync()).Ready);Assert.Equal(1,js.Imports);
    }
    [LocalAuthoringFact]
    public async Task Failed_provision_retains_same_module_for_retry_and_only_final_lifetime_disposes_it()
    {
        var f=await Fixture.Value;var js=new Runtime();js.Module.Provisioned=false;
        var config=new LocalCompileWorkspaceConfiguration(js,Providers(f,new Selection {Text=f.Compiler},new Selection()));
        Assert.Equal("ControllerUpdate",(await config.OpenAsync()).Compiler.Status);Assert.Equal(1,js.Imports);Assert.Equal(0,js.Module.GlobalDisposals);
        js.Module.Provisioned=true;js.Module.ThrowProvision=true;Assert.False((await config.CheckAsync()).Ready);js.Module.ThrowProvision=false;
        var ports=(await config.OpenAsync()).Ports!;Assert.NotNull(ports);Assert.True((await config.CheckAsync()).Ready);
        Assert.Same(ports,(await config.OpenAsync()).Ports);Assert.Equal(1,js.Imports);
        await config.DisposeAsync();await config.DisposeAsync();await config.WhenSettled;Assert.Equal(1,js.Module.GlobalDisposals);Assert.Equal(1,js.Module.ReferenceDisposals);
        Assert.Equal("Disposed",(await config.CheckAsync()).Status);Assert.Equal(1,js.Imports);
    }
    [LocalAuthoringFact]
    public async Task Cancelled_import_retains_admission_late_lease_and_retries_without_reimport()
    {
        var f=await Fixture.Value;var js=new Runtime {ImportGate=Signal<IJSObjectReference>()};using var cancel=new CancellationTokenSource();
        var config=new LocalCompileWorkspaceConfiguration(js,Providers(f,new Selection {Text=f.Compiler},new Selection()));
        var opening=config.OpenAsync(cancel.Token);
        try
        {
            await js.ImportEntered.Task;cancel.Cancel();Assert.Equal("Cancelled",(await opening).Compiler.Status);
            Assert.Equal("Busy",(await config.CheckAsync()).Status);Assert.Equal(1,js.Imports);Assert.False(config.WhenSettled.IsCompleted);
        }
        finally {js.ImportGate.SetResult(js.Module);await config.WhenSettled;}
        Assert.True((await config.CheckAsync()).Ready);Assert.Equal(1,js.Imports);Assert.Equal(0,js.Module.GlobalDisposals);
        await config.DisposeAsync();await config.WhenSettled;Assert.Equal(1,js.Module.GlobalDisposals);
    }
    [LocalAuthoringFact]
    public async Task Late_provision_cancel_and_app_disposal_are_observed_without_early_module_dispose()
    {
        var f=await Fixture.Value;var js=new Runtime();js.Module.Entered=Signal<bool>();js.Module.Release=Signal<bool>();
        var config=new LocalCompileWorkspaceConfiguration(js,Providers(f,new Selection {Text=f.Compiler},new Selection()));using var cancel=new CancellationTokenSource();
        var opening=config.OpenAsync(cancel.Token);await js.Module.Entered.Task;cancel.Cancel();Assert.Equal("Cancelled",(await opening).Compiler.Status);
        Assert.Equal("Busy",(await config.CheckAsync()).Status);await config.DisposeAsync();Assert.Equal(0,js.Module.GlobalDisposals);Assert.False(config.WhenSettled.IsCompleted);
        js.Module.Release.SetResult(true);await config.WhenSettled;Assert.Equal(1,js.Module.GlobalDisposals);Assert.Equal(1,js.Module.ReferenceDisposals);
    }
    [LocalAuthoringFact]
    public async Task Failed_import_is_retryable_and_late_disposal_joins_a_successful_import()
    {
        var f=await Fixture.Value;var js=new Runtime {FailImport=true};var config=new LocalCompileWorkspaceConfiguration(js,Providers(f,new Selection {Text=f.Compiler},new Selection()));
        Assert.False((await config.OpenAsync()).Compiler.Ready);js.FailImport=false;Assert.True((await config.CheckAsync()).Ready);Assert.Equal(2,js.Imports);
        await config.DisposeAsync();await config.WhenSettled;
        var late=new Runtime {ImportGate=Signal<IJSObjectReference>()};var second=new LocalCompileWorkspaceConfiguration(late,Providers(f,new Selection {Text=f.Compiler},new Selection()));
        var pending=second.OpenAsync();await late.ImportEntered.Task;await second.DisposeAsync();Assert.Equal("Disposed",(await pending).Compiler.Status);
        Assert.False(second.WhenSettled.IsCompleted);late.ImportGate.SetResult(late.Module);await second.WhenSettled;Assert.Equal(1,late.Module.GlobalDisposals);
    }
    [LocalAuthoringFact]
    public async Task Permanently_disposed_bridge_is_not_reimported_or_reverified_after_late_failure()
    {
        var f=await Fixture.Value;var selection=new Selection {Text=f.Compiler};var js=new Runtime();js.Module.Entered=Signal<bool>();js.Module.Release=Signal<bool>();js.Module.DisposedProvision=true;
        var config=new LocalCompileWorkspaceConfiguration(js,Providers(f,selection,new Selection()));using var cancel=new CancellationTokenSource();
        var pending=config.OpenAsync(cancel.Token);
        try {await js.Module.Entered.Task;cancel.Cancel();Assert.Equal("Cancelled",(await pending).Compiler.Status);}
        finally {js.Module.Release.SetResult(true);await config.WhenSettled;}
        int reads=selection.Reads;Assert.Equal("Disposed",(await config.CheckAsync()).Status);Assert.Equal(reads,selection.Reads);Assert.Equal(1,js.Imports);
        await config.DisposeAsync();await config.WhenSettled;
    }
    [LocalAuthoringFact]
    public async Task PDF_inspection_late_cancel_keeps_actual_byte_admission_and_reports_real_busy_corrupt_status()
    {
        var f=await Fixture.Value;var js=new Runtime();var pdf=new Selection {Text=f.Pdf};var providers=Providers(f,new Selection {Text=f.Compiler},pdf);
        await using var config=new LocalCompileWorkspaceConfiguration(js,providers);var inspector=(await config.OpenAsync()).Ports!.Inspector!;
        var io=f.PdfIo;io.ReadEntered=Signal<bool>();io.ReadRelease=Signal<bool>();using var cancel=new CancellationTokenSource();
        var first=inspector.InspectAsync(Request(),cancel.Token);
        try
        {
            await io.ReadEntered.Task;cancel.Cancel();Assert.Equal(PdfInspectionStatus.Cancelled,(await first).Status);
            var busy=await inspector.InspectAsync(Request());Assert.Equal(PdfInspectionStatus.Busy,busy.Status);Assert.Equal("RuntimeBusy",busy.Code);Assert.Equal(0,js.PdfImports);
        }
        finally {io.ReadRelease.SetResult(true);await providers.WhenSettled;io.ReadEntered=null;io.ReadRelease=null;}
        Assert.Equal(PdfInspectionStatus.TransportError,(await inspector.InspectAsync(Request())).Status);Assert.Equal(1,js.PdfImports); // fake stops at approved port import, not rendering
        pdf.Text="{}";var bad=await inspector.InspectAsync(Request());Assert.Equal(PdfInspectionStatus.Unavailable,bad.Status);Assert.Equal("RuntimeCorrupt",bad.Code);Assert.Equal(1,js.PdfImports);
        Assert.True((await config.CheckAsync()).Ready); // PDF failure never disables the compiler
    }
    [LocalAuthoringFact]
    public async Task Old_self_consistent_E1_metadata_is_rejected_before_byte_store_IO_and_does_not_disable_compiler()
    {
        var f=await Fixture.Value;
        // Self-consistent old-pair selection: valid schema and matching inventory digest, but the OLD
        // wasm hash/length the app no longer trusts for 512-capable execution. TrustedPdf rejects BEFORE IO.
        var resources=ImmutableArray.Create(
            new PdfResourceSelection("loader",AssetId.New(),"e1.js",13562,LocalInstalledRuntimeProviders.PdfLoaderHash),
            new PdfResourceSelection("wasm",AssetId.New(),"e1.wasm",3434938,"ea5a9a81d6e200fb59ccaced8ecd510d84999c648acf4a13e4ebbef07b1e2e8b"));
        var inventory=PdfRuntimeSelection.Inventory(resources);
        var selection=new Selection {Text=PdfRuntimeSelection.Encode(new(Guid.NewGuid(),inventory.Identity.Digest.Hex,resources))};
        var io=new SetupBackend();var providers=new LocalInstalledRuntimeProviders(new(new SetupBackend()),new Selection(),new(io),selection);
        var read=await providers.ReadPdfAsync();Assert.Null(read.Package);Assert.Equal(RuntimeAssetStatus.Corrupt,read.Store.Status);Assert.Equal(0,io.Calls);
        // Compiler capability is independent of renderer trust.
        Assert.NotNull((await new LocalInstalledRuntimeProviders(new(f.Native),new Selection {Text=f.Compiler},new(new SetupBackend()),new Selection()).ReadCompilerAsync()).Package);
    }
    [LocalAuthoringFact]
    public async Task Actual_fixed_E1_pair_is_fresh_and_selfconsistent_wrong_name_is_rejected_before_store_IO()
    {
        var f=await Fixture.Value;var selection=new Selection {Text=f.Pdf};await using var providers=Providers(f,new Selection(),selection);
        var first=await providers.ReadPdfAsync();Assert.True(first.Package!.IsValid);
        var record=PdfRuntimeSelection.Decode(f.Pdf);var bytes=f.PdfIo.Files[record.InstallationId][0];var old=bytes[0];
        try {bytes[0]^=1;Assert.Equal(RuntimeAssetStatus.Corrupt,(await providers.ReadPdfAsync()).Store.Status);}
        finally {bytes[0]=old;}
        var resources=record.Resources.SetItem(0,record.Resources[0] with {Name="other.js"});var inventory=PdfRuntimeSelection.Inventory(resources);
        selection.Text=PdfRuntimeSelection.Encode(record with {Resources=resources,InventoryDigest=inventory.Identity.Digest.Hex});int calls=f.PdfIo.Calls;
        Assert.Equal(RuntimeAssetStatus.Corrupt,(await providers.ReadPdfAsync()).Store.Status);Assert.Equal(calls,f.PdfIo.Calls);
        selection.Text=f.Pdf;Assert.NotNull((await providers.ReadPdfAsync()).Package);selection.Text=null;Assert.Equal(RuntimeAssetStatus.Missing,(await providers.ReadPdfAsync()).Store.Status);
    }
    [LocalAuthoringFact]
    public void Compile_factory_rechecks_current_bytes_and_plain_v5_dispatch_does_not_require_PDF_or_force_Bibtex()
        => LifecycleTestHost.Run(async () =>
    {
        var f=await Fixture.Value;var js=new Runtime();var selection=new Selection {Text=f.Compiler};await using var config=new LocalCompileWorkspaceConfiguration(js,Providers(f,selection,new Selection()));
        var ports=(await config.OpenAsync()).Ports!;await using var workspace=await CompilationTestWorkspace.Open();Assert.True(workspace.Workspace.RenameFile(workspace.Main,"main.tex"));
        workspace.Selection.SetSelection(CompilationSelection.Create(new(workspace.Main,ports.Engine,ports.Bundles,[],ports.SelectionOptions!),65536).Selection!);
        var context=CompileRequestContext.Create(await workspace.Snapshot(),1,65536).Context!;var limits=new CompileOutputBudget(1048576,65536,32,128,4096,8192);
        selection.Text=null;var id=new CompileJobId(Guid.NewGuid());var absent=await Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(ports.Factory.Create(id,1)!.ExecuteAsync(id,context,limits,default));
        Assert.Equal(CompileOutputStatus.Unsupported,absent!.Status);Assert.Null(js.Module.Request);
        selection.Text=f.Replacement;id=new(Guid.NewGuid());var stopped=await Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(ports.Factory.Create(id,1)!.ExecuteAsync(id,context,limits,default));
        Assert.NotEqual(CompileOutputStatus.Succeeded,stopped!.Status); // fake JS deliberately stops at dispatch: no native acceptance
        Assert.Equal(NativeSelectedEntryContract.Version,js.Module.Request!.Tag.Version);Assert.Equal("latex",js.Module.Request.Tag.Tool);Assert.Equal(0,js.PdfImports);
    });

    private static LocalInstalledRuntimeProviders Providers(FixtureState f,Selection compiler,Selection pdf)=>new(new(f.Native),compiler,new(f.PdfIo),pdf);
    private static PdfInspectRequest Request()=>new(new PdfDocument([37,80,68,70]),0,new(DeadlineMs:2000),raster:false);
    private static TaskCompletionSource<T> Signal<T>()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Selection : ILocalRuntimeSelectionStore,IPdfRuntimeSelectionStore
    {
        public string? Text;public int Reads;public bool Fault;public RuntimeAssetStatus? Failure;public TaskCompletionSource<bool>? Entered,Release;
        public async Task<string?> ReadAsync(){Reads++;Entered?.TrySetResult(true);if(Release is not null)await Release.Task;if(Fault)throw new IOException("finite test metadata failure");if(Failure is {} status)throw new RuntimeAssetIoException(status);return Text;}
        public Task WriteAsync(string text){Text=text;return Task.CompletedTask;}
    }
    private sealed class Runtime : IJSRuntime
    {
        public int Imports,PdfImports;public bool FailImport;public Module Module=new();public TaskCompletionSource<IJSObjectReference>? ImportGate;
        public TaskCompletionSource<bool> ImportEntered=Signal<bool>();
        public ValueTask<T> InvokeAsync<T>(string name,object?[]? args)=>InvokeAsync<T>(name,default,args);
        public async ValueTask<T> InvokeAsync<T>(string name,CancellationToken token,object?[]? args)
        {
            Assert.Equal("import",name);if((string)args![0]! == "./js/pdf-port-bridge.js"){PdfImports++;throw new JSException("TEST: no PDF/native execution");}
            Assert.Equal("./js/compile-port-bridge.js",args[0]);Assert.False(token.CanBeCanceled);Imports++;ImportEntered.TrySetResult(true);
            if(FailImport)throw new JSException("finite TEST import failure");return (T)(object)(ImportGate is null?Module:await ImportGate.Task);
        }
    }
    private sealed class Module : IJSInProcessObjectReference
    {
        public bool Provisioned=true,ThrowProvision,DisposedProvision;public int Provisions,GlobalDisposals,ReferenceDisposals;public TaskCompletionSource<bool>? Entered,Release;public NativePhaseRequest? Request;
        public T Invoke<T>(string name,params object?[]? args){if(name=="dispose")GlobalDisposals++;else Assert.Equal("terminate",name);return default!;}
        public ValueTask<T> InvokeAsync<T>(string name,object?[]? args)=>InvokeAsync<T>(name,default,args);
        public async ValueTask<T> InvokeAsync<T>(string name,CancellationToken token,object?[]? args)
        {
            Assert.False(token.CanBeCanceled);
            if(name=="provision"){Provisions++;Entered?.TrySetResult(true);if(Release is not null)await Release.Task;if(DisposedProvision)throw new JSException("disposed");if(ThrowProvision)throw new JSException("finite TEST provision failure");return (T)(object)Provisioned;}
            Assert.Equal("execute",name);Request=Assert.IsType<NativePhaseRequest>(args![0]);throw new JSException("TEST dispatch boundary, not native execution");
        }
        public void Dispose(){}public ValueTask DisposeAsync(){ReferenceDisposals++;return ValueTask.CompletedTask;}
    }
    private sealed record FixtureState(SetupBackend Native,SetupBackend PdfIo,string Compiler,string Replacement,string Pdf);
    private static readonly Lazy<Task<FixtureState>> Fixture=new(async()=>
    {
        var native=new SetupBackend();var pdf=new SetupBackend();var compilerSelection=new Selection();var pdfSelection=new Selection();
        string nativeRoot=Environment.GetEnvironmentVariable("AUTHORING_RUNTIME_DIR")!;var pins=File.ReadAllBytes(Path.Combine(nativeRoot,"pins.json"));
        var resources=LocalRuntimeSelection.ParsePins(pins);var files=resources.Select(p=>new LocalRuntimeFile(p.Name,p.Length,_=>Task.FromResult(File.ReadAllBytes(Path.Combine(nativeRoot,"files",p.Name))))).ToArray();
        await using var installer=new NativeRuntimeSetup(new(native),compilerSelection);await installer.InstallAsync(pins,files);Assert.Equal(RuntimeSetupState.Installed,installer.State);string original=compilerSelection.Text!;
        await installer.InstallAsync(pins,files);Assert.Equal(RuntimeSetupState.Installed,installer.State);string replacement=compilerSelection.Text!;
        string pdfRoot=Environment.GetEnvironmentVariable("LOCAL_AUTHORING_PDF_RUNTIME_DIR")!;var manifest=File.ReadAllBytes(Path.Combine(pdfRoot,"manifest.json"));
        var pdfFiles=PdfRuntimeSelection.ParseManifest(manifest).Select(p=>new LocalRuntimeFile(p.Name,p.Length,_=>Task.FromResult(File.ReadAllBytes(Path.Combine(pdfRoot,"files",p.Name))))).ToArray();
        await using var pdfInstaller=new PdfRuntimeSetup(new(pdf),pdfSelection);await pdfInstaller.InstallAsync(manifest,pdfFiles);Assert.Equal(PdfRuntimeSetupState.Installed,pdfInstaller.State);
        return new(native,pdf,original,replacement,pdfSelection.Text!);
    });
}
public sealed class LocalAuthoringFactAttribute : FactAttribute
{
    public LocalAuthoringFactAttribute(){if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUTHORING_RUNTIME_DIR"))||string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOCAL_AUTHORING_PDF_RUNTIME_DIR")))Skip="Explicit actual compiler and local-authoring E1 byte fixtures required; no vendor execution proof.";}
}
