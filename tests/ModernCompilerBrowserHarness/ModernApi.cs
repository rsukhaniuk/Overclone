using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Assets;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.RuntimeAssets;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Storage;
using CompilerStorageBrowserHarness;
using Microsoft.JSInterop;

namespace ModernCompilerBrowserHarness;

// Test composition only. The production app's unavailable registration is untouched.
public static class ModernApi
{
    private static NativeRuntimeProfile _profile = NativeRuntimeProfile.LocalLatex2023;
    private static NativeRuntimeLimits Limits => new(512, 33554432, 67108864, _profile);
    private static RuntimeAssetBudget Budget => new(new(NativeRuntimeProfiles.InstalledCount(_profile), 256, 33554432, 67108864, 1048576, 256, 256), 1048576, 8, 16, 30000);
    private static CompilationDescriptor _engine = null!, _bundle = null!, _bibtex = null!;
    private static IJSRuntime _js = null!;
    private static HttpClient _http = null!;
    private static OwnerContext _owner = null!;
    private static RuntimeAssetStore _store = null!;
    private static InstalledNativeRuntimeProvider _provider = null!;
    private static NativeBrowserModuleLease _module = null!;
    private static WorkspaceCoordinator _workspace = null!;
    private static CompilationSelectionState _selection = null!;
    private static bool _provisioned;
    private static object? _lastRead;

    public static async Task Initialize(IJSRuntime js, HttpClient http)
    {
        _js = js; _http = http;
        var port = (IJSInProcessObjectReference)await js.InvokeAsync<IJSObjectReference>("import", "./owner.js");
        _owner = new(() => port.InvokeVoid("schedule"));
        _store = new(new JsRuntimeAssetBackend(js));
    }
    [JSInvokable] public static void Pump() => _owner.Pump();
    private static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    [JSInvokable]
    public static Task<object> Provision() => _owner.RunAsync(() => ProvisionCore(NativeRuntimeProfile.LocalLatex2023));
    [JSInvokable]
    public static Task<object> ProvisionAuthoring() => _owner.RunAsync(() => ProvisionCore(NativeRuntimeProfile.LocalLatex2023Authoring));
    private static async Task<object> ProvisionCore(NativeRuntimeProfile profile)
    {
        Need(!_provisioned, "One explicit fixture installation per page");
        _profile = profile;
        var pins = await _http.GetFromJsonAsync<NativeResourcePin[]>("private-inputs/pins.json") ?? throw new InvalidOperationException("explicit pins");
        Need(pins.Length == NativeRuntimeProfiles.InstalledCount(_profile), "trusted explicit profile count");
        // Package/installed provider additionally require the independently fixed
        // profile closure digest. This received file cannot choose trusted hashes.
        var resources = pins.Select((p, i) => new InstalledNativeResource(new(new Guid(i + 1, 0, 0, new byte[8])), "runtime/" + p.Name, p)).ToImmutableArray();
        var declared = AssetInventoryFactory.Create(new(resources.Select(r => new DeclaredAssetInput
        { AssetId = r.AssetId, RelativePath = r.RelativePath, DeclaredLength = r.Pin.Length, DeclaredHash = new(r.Pin.Sha256), Version = "modern-profile-v1" }).ToArray()), Budget.Inventory).Inventory!;
        var data = new Dictionary<AssetId, byte[]>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        foreach (var r in resources)
        {
            var bytes = await _http.GetByteArrayAsync("private-inputs/files/" + r.Pin.Name, deadline.Token);
            Need(bytes.Length == r.Pin.Length && NativeRuntimePackage.Digest(bytes).Hex == r.Pin.Sha256, "fixture bytes");
            data.Add(r.AssetId, bytes);
        }
        var receipt = (await new AssetInventoryVerifier().VerifyAsync(declared, new Reader(data), Budget.Verification, deadline.Token) as AssetVerificationResult.Verified)?.Inventory;
        Need(receipt is not null, "full asset receipt");
        var install = await _store.InstallAsync(receipt!, Budget, deadline.Token);
        Need(install.Status == RuntimeAssetStatus.Installed, "real runtime install " + install.Status);
        _provider = new(_store, new(install.InstallationId!.Value, declared.Identity.Digest.Hex, resources, _profile), Budget, Limits);
        var read = await _provider.ReadPackageAsync(deadline.Token);
        Need(read.Package is not null, "explicit profile load " + read.Store.Status);
        var package = read.Package!;
        _engine = package.Engine; _bundle = package.Bundle; _bibtex = package.Bibtex;
        var repo = new WorkspaceStorage(_js);
        var source = "\\documentclass{article}\n\\usepackage{amsmath}\n\\makeatletter\\typeout{PROFILE-KERNEL=\\fmtversion;PATCH=\\patch@level}\\makeatother\n\\begin{document}\n\\section{Modern CSharp pipeline}\\label{sec:p}\nSection~\\ref{sec:p} is resolved. $x^2+y^2=z^2$.\\end{document}\n";
        var bytesSource = Encoding.UTF8.GetBytes(source);
        var imported = await repo.PublishImportAsync(new("Modern technical profile only", [new StagedFile("main.tex", "text", false, bytesSource, NativeRuntimePackage.Digest(bytesSource), bytesSource.Length)], []), default);
        Need(imported.Published, "real local project import");
        _workspace = new(repo, new JsWorkspaceLocks(_js), new HistoryService(repo));
        await _workspace.OpenAsync(imported.Project!.Id, default);
        _selection = new();
        var main = _workspace.Files.Single(f => f.RelativePath == "main.tex").Id;
        _selection.SetSelection(CompilationSelection.Create(new(main, package.Engine, [package.Bundle], [], []), 65536).Selection!);
        var loader = new NativeBrowserModuleLoader(_js);
        _module = await loader.LoadAsync(default);
        Need(await _module.ProvisionAsync(), "compiler bridge explicitly provisioned");
        _provisioned = true;
        return new { status = read.Store.Status.ToString(), installation = install.InstallationId, count = read.Store.Snapshot!.AssetCount,
            profile = package.Profile.ToString(), packageIdentity = package.Identity, package.Bundle, package.Engine,
            format = resources.Single(r => r.Pin.Name == "pdflatex.fmt").Pin, productionRegistered = false };
    }
    [JSInvokable]
    public static Task<object> Compile() => _owner.RunAsync(CompileCurrent);
    [JSInvokable]
    public static Task<object> CompileAuthoring() => _owner.RunAsync<object>(async () =>
    {
        Need(_provisioned && _profile == NativeRuntimeProfile.LocalLatex2023Authoring, "Explicit authoring installation required");
        var main = _workspace.Files.Single(f => f.RelativePath == "main.tex").Id;
        Need(_workspace.EditText(main, AuthoringFixture.Main), "authoring main edit");
        Need(_workspace.CreateFileFromBytes("chapter.tex", Encoding.UTF8.GetBytes(AuthoringFixture.Chapter)), "chapter creation");
        Need(_workspace.CreateFileFromBytes("references.bib", Encoding.UTF8.GetBytes(AuthoringFixture.Bibliography)), "bib creation");
        _selection.SetSelection(CompilationSelection.Create(new(main, _engine, [_bundle], [_bibtex], []), 65536).Selection!);
        return await CompileCurrent();
    });
    private static async Task<object> CompileCurrent()
    {
        Need(_provisioned, "explicit provision required");
        var observations = new List<object>();
        var factory = new NativeCompilationWorkerFactory(new FreshInstalled(), _module.Transport,
            new(false, 4, TimeSpan.FromSeconds(90), MaxInputBytes: 67108864, MaxFileBytes: 33554432, UseSelectionTools: true, Profile: _profile));
        factory.PhaseObserved += p => observations.Add(new { p.Tag, p.Sent, p.Reply.Outcome, p.Reply.NativeReturn, p.Reply.Entered, p.Reply.Log, p.Reply.Inputs, p.Reply.LoaderSha256, p.Reply.WasmSha256, files = p.Reply.Files.Select(f => new { f.Name, length = f.Bytes.Length, hash = NativeRuntimePackage.Digest(f.Bytes), carryBase64 = f.Name.EndsWith(".aux", StringComparison.Ordinal) || f.Name is "main.bbl" or "main.blg" ? Convert.ToBase64String(f.Bytes) : null }) });
        await using var life = new CompilationLifecycleService(_workspace, _selection, factory, new(2, 100), TimeProvider.System);
        var options = new CompileLifecycleOptions(new(32, 1048576, 8388608, 65536), new(8388608, 2097152, 32, 128, 4096, 1048576), 65536,
            TimeSpan.FromSeconds(100), NativeRuntimePackage.Pdf, NativeRuntimePackage.Adapter);
        var ticket = life.Request(options); var result = await ticket.Completion;
        await life.WhenIdle; await factory.Operations.WhenIdle;
        Need(result.Reason == CompileLifecycleReason.Succeeded && result.Output is not null, "real C# compile " + result.Reason + " " + JsonSerializer.Serialize(observations));
        var output = result.Output!;
        return new { reason = result.Reason.ToString(), installedRead = _lastRead, phases = observations, browser = _module.Observations(),
            snapshot = new { ticket.Snapshot!.Identity, ticket.Snapshot.ContentDigest, ticket.Snapshot.Selection, files = ticket.Snapshot.Files.Select(f => new { f.RelativePath, f.ByteLength, f.Hash, bytesBase64 = Convert.ToBase64String(f.ToArray()) }) },
            output = new { output.RequestId, output.SnapshotIdentity, output.InputContentDigest, output.ContentIdentity, output.OutputHash,
                output.OutputLength, output.OutputFormat, output.OutputAdapter, pdf = Convert.ToBase64String(output.ToPayloadArray()!),
                boundSnapshot = ReferenceEquals(output.Snapshot, ticket.Snapshot), boundContext = ReferenceEquals(output.RequestContext.Snapshot, ticket.Snapshot) },
            isCurrent = life.State.IsCurrent };
    }
    private sealed class FreshInstalled : INativeRuntimeProvider
    {
        public async Task<NativeRuntimePackage?> GetAsync()
        {
            var read = await _provider.ReadPackageAsync();
            _lastRead = new { status = read.Store.Status.ToString(), count = read.Store.Snapshot?.AssetCount, identity = read.Package?.Identity, profile = read.Package?.Profile.ToString() };
            return read.Package;
        }
    }
    private sealed class Reader(Dictionary<AssetId, byte[]> bytes) : IAssetByteReader
    { public Task<byte[]?> ReadAsync(DeclaredAsset asset, CancellationToken token) => Task.FromResult<byte[]?>(bytes.GetValueOrDefault(asset.AssetId)); }
}
