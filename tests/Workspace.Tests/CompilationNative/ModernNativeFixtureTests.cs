using System.Collections.Immutable;
using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Assets;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.RuntimeAssets;
using Workspace.Tests.CompilationLifecycle;
using Xunit;

namespace Workspace.Tests.CompilationNative;

// Optional explicitly mounted modern fixture. No binaries committed. Positive
// transport replies in unit tests are modeled, never native/PDF evidence.
public sealed class ModernNativeFixtureTests
{
    private const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalLatex2023;
    private static readonly NativeRuntimeLimits Limits = new(512, 33554432, 67108864, Profile);
    private static readonly RuntimeAssetBudget Budget = new(new(481, 256, 33554432, 67108864, 1048576, 256, 256), 1048576, 8, 16, 30000);
    private static readonly Lazy<(NativeResourcePin[] Pins, NativeResourceInput[] Inputs, NativeRuntimePackage Package)> Fixture = new(() =>
    {
        var root = Environment.GetEnvironmentVariable("MODERN_RUNTIME_DIR") ?? throw new InvalidOperationException("Explicit modern fixture missing");
        var pins = JsonSerializer.Deserialize<NativeResourcePin[]>(File.ReadAllBytes(Path.Combine(root, "pins.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var input = pins.Select(p => new NativeResourceInput(p.Name, File.ReadAllBytes(Path.Combine(root, "files", p.Name)))).ToArray();
        var package = Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(input, pins, Limits, Profile).Package);
        Assert.Equal(481, input.Length); // independent profile count, not received count
        return (pins, input, package);
    });
    [Fact]
    public void Explicit_modern_profile_accepts_exact_closure_and_owns_defensive_bytes()
    {
        var f = Fixture.Value;
        Assert.Equal(Profile, f.Package.Profile);
        Assert.Equal("2023-11-01-p1-profile-v1", f.Package.Bundle.Version);
        Assert.Equal("local-latex-assets", f.Package.Bundle.Id);
        Assert.Equal("6feac49bf29674a92fb3a890524cde388587027f1c5d2f7df6905c2f5167c95d", f.Package.Identity.Hex);
        var a = f.Package.Copy("pdflatex.fmt"); a[0] ^= 1;
        Assert.Equal(NativeRuntimeProfiles.ModernFormatHash, NativeRuntimePackage.Digest(f.Package.Copy("pdflatex.fmt")).Hex);
        Assert.Equal(NativeRuntimePackage.WasmHash, f.Package.Engine.Hash.Hex);
        Assert.Null(NativeRuntimePackage.Create(f.Inputs, f.Pins, new(512, 16777216, 33554432)).Package);
    }
    [Fact]
    public void Caller_mutation_after_creation_cannot_change_modern_package()
    {
        var f = Fixture.Value; int i = Array.FindIndex(f.Pins, p => p.Name == "article.cls");
        var input = f.Inputs.ToArray(); var bytes = input[i].Bytes.ToArray(); input[i] = input[i] with { Bytes = bytes };
        var package = Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(input, f.Pins, Limits, Profile).Package);
        bytes[0] ^= 255; input[i] = input[i] with { Name = "changed" };
        Assert.Equal(f.Pins[i].Sha256, NativeRuntimePackage.Digest(package.Copy("article.cls")).Hex);
    }
    [Theory]
    [InlineData("engine")]
    [InlineData("format")]
    [InlineData("closure")]
    [InlineData("alias")]
    [InlineData("content")]
    public void Wrong_engine_format_declared_closure_alias_or_bytes_never_create_a_package(string kind)
    {
        var f = Fixture.Value; var pins = f.Pins.ToArray(); var inputs = f.Inputs.ToArray();
        string name = kind == "engine" ? "engine.wasm" : kind == "format" ? "pdflatex.fmt" : "article.cls";
        int i = Array.FindIndex(pins, p => p.Name == name);
        if (kind == "content") { var b = inputs[i].Bytes.ToArray(); b[0] ^= 1; inputs[i] = inputs[i] with { Bytes = b }; }
        else if (kind == "alias") { pins[i] = pins[i] with { Name = NativeRuntimePackage.NativeFormatName }; inputs[i] = inputs[i] with { Name = NativeRuntimePackage.NativeFormatName }; }
        else pins[i] = pins[i] with { Sha256 = new string('a', 64) };
        var result = NativeRuntimePackage.Create(inputs, pins, Limits, Profile);
        Assert.Null(result.Package);
        Assert.Equal(kind == "alias" ? NativeRuntimeIssue.InvalidInventory : NativeRuntimeIssue.TechnicalHashMismatch, result.Issue);
    }
    [Fact]
    public void Mixed_historical_format_bytes_and_pins_cannot_enter_modern_closure()
    {
        var f = Fixture.Value; var input = f.Inputs.ToArray(); var pins = f.Pins.ToArray();
        int index = Array.FindIndex(pins,p=>p.Name=="pdflatex.fmt");
        var legacy = Environment.GetEnvironmentVariable("N11_RUNTIME_DIR") ?? throw new InvalidOperationException("Legacy fixture missing");
        var oldFormat = File.ReadAllBytes(Path.Combine(legacy,"files","pdflatex.fmt"));
        input[index] = new("pdflatex.fmt",oldFormat);
        pins[index] = new("pdflatex.fmt",oldFormat.Length,NativeRuntimePackage.Digest(oldFormat).Hex);
        var result = NativeRuntimePackage.Create(input,pins,Limits,Profile);
        Assert.Null(result.Package);Assert.Equal(NativeRuntimeIssue.TechnicalHashMismatch,result.Issue);
    }
    [Fact]
    public void Insufficient_file_total_and_count_budgets_reject_before_package_clones()
    {
        var f = Fixture.Value;
        foreach (var limits in new[] { Limits with { MaxFiles = 480 }, Limits with { MaxFileBytes = 16777216 }, Limits with { MaxTotalBytes = 33554432 } })
            Assert.Null(NativeRuntimePackage.Create(f.Inputs, f.Pins, limits, Profile).Package);
    }
    private static ImmutableArray<InstalledNativeResource> Resources() => Fixture.Value.Pins.Select((p, i) => new InstalledNativeResource(new(new Guid(i + 1, 0, 0, new byte[8])), "runtime/" + p.Name, p)).ToImmutableArray();
    [Fact]
    public async Task Real_store_install_read_and_explicit_installed_provider_support_full_modern_closure()
    {
        var resources = Resources(); var declaration = AssetInventoryFactory.Create(new(resources.Select(r => new DeclaredAssetInput
        { AssetId = r.AssetId, RelativePath = r.RelativePath, DeclaredLength = r.Pin.Length, DeclaredHash = new(r.Pin.Sha256), Version = "modern-profile-v1" }).ToArray()), Budget.Inventory).Inventory!;
        var data = resources.ToDictionary(r => r.AssetId, r => Fixture.Value.Inputs.Single(f => f.Name == r.Pin.Name).Bytes);
        var receipt = Assert.IsType<AssetVerificationResult.Verified>(await new AssetInventoryVerifier().VerifyAsync(declaration, new Reader(data), Budget.Verification, default)).Inventory;
        var io = new Backend(); await using var store = new RuntimeAssetStore(io);
        var installed = await store.InstallAsync(receipt, Budget); Assert.Equal(RuntimeAssetStatus.Installed, installed.Status);
        var mapping = new InstalledNativeRuntimeMapping(installed.InstallationId!.Value, declaration.Identity.Digest.Hex, resources, Profile);
        var read = await new InstalledNativeRuntimeProvider(store, mapping, Budget, Limits).ReadPackageAsync();
        Assert.Equal(RuntimeAssetStatus.Ready, read.Store.Status); Assert.Equal(Fixture.Value.Package.Identity, read.Package!.Identity);
        var stale = mapping with { InventoryDigest = new string('a', 64) };
        Assert.Null((await new InstalledNativeRuntimeProvider(store, stale, Budget, Limits).ReadPackageAsync()).Package);
        foreach (var bad in new[] { mapping with { Profile = (NativeRuntimeProfile)99 }, mapping with { Profile = NativeRuntimeProfile.Legacy }, mapping with { Resources = resources.RemoveAt(0) } })
        {
            int before = io.CatalogReads;
            Assert.Equal(RuntimeAssetStatus.InvalidInput, (await new InstalledNativeRuntimeProvider(store, bad, Budget, Limits).ReadPackageAsync()).Store.Status);
            Assert.Equal(before, io.CatalogReads);
        }
    }
    [Theory]
    [InlineData("legacy-plan")]
    [InlineData("stale-bundle")]
    [InlineData("wrong-version")]
    [InlineData("wrong-engine-version")]
    [InlineData("insufficient-total")]
    [InlineData("alias")]
    public void Selection_or_phase_preflight_never_dispatches_mismatched_or_overbudget_inputs(string kind) => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Assert.True(f.Workspace.RenameFile(f.Main, "main.tex"));
        var package = Fixture.Value.Package;
        var bundle = kind switch { "stale-bundle" => package.Bundle with { Hash = new(new string('a',64)) }, "wrong-version" => package.Bundle with { Version = "2015-10-01" }, _ => package.Bundle };
        var engine = kind == "wrong-engine-version" ? package.Engine with { Version = "foreign" } : package.Engine;
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, engine, [bundle], [], []),65536).Selection!);
        if (kind == "alias") Assert.True(f.Workspace.CreateFileFromBytes(NativeRuntimePackage.NativeFormatName, [1]));
        var plan = kind == "legacy-plan" ? new NativeCompilePlan(false,2,TimeSpan.FromSeconds(10))
            : new(false,2,TimeSpan.FromSeconds(10),MaxInputBytes: kind == "insufficient-total" ? 33554432 : 67108864,MaxFileBytes:33554432,UseSelectionTools:true,Profile:Profile);
        var transport = new Transport(); var factory = new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(package),transport,plan);
        var snapshot = await f.Snapshot(); var context = CompileRequestContext.Create(snapshot,1,65536).Context!; var id = new CompileJobId(Guid.NewGuid());
        var result = await Execute(factory.Create(id,1),id,context,new(1048576,65536,32,128,4096,8192));
        Assert.Null(result!.Payload); Assert.Empty(transport.Calls); Assert.NotEqual(CompileOutputStatus.Succeeded,result.Status);
    });
    [Fact]
    public void Modern_request_and_artifact_bind_profile_and_reject_stale_tag() => LifecycleTestHost.Run(async () =>
    {
        await using var f = await CompilationTestWorkspace.Open(); Assert.True(f.Workspace.RenameFile(f.Main,"main.tex")); var package=Fixture.Value.Package;
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main,package.Engine,[package.Bundle],[],[]),65536).Selection!);
        var snapshot=await f.Snapshot(); var context=CompileRequestContext.Create(snapshot,1,65536).Context!;
        var transport=new Transport(); var plan=new NativeCompilePlan(false,2,TimeSpan.FromSeconds(10),MaxInputBytes:67108864,MaxFileBytes:33554432,UseSelectionTools:true,Profile:Profile);
        var factory=new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(package),transport,plan);var id=new CompileJobId(Guid.NewGuid());var budget=new CompileOutputBudget(1048576,65536,32,128,4096,8192);
        var result=await Execute(factory.Create(id,1),id,context,budget);
        Assert.Equal(CompileOutputStatus.Succeeded,result!.Status);Assert.Equal(2,transport.Calls.Count);
        Assert.All(transport.Calls,r=>{Assert.Equal(2,r.Tag.Version);Assert.Equal(Profile,r.Tag.Profile);Assert.Equal(package.Identity.Hex,r.Tag.RuntimeIdentity);Assert.Single(r.Files,f=>f.Name==NativeRuntimePackage.NativeFormatName);});
        Assert.NotNull(CompileOutputEnvelope.Create(result,budget).Envelope!.TryAccept(context).Output);
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main,package.Engine,[package.Bundle with {Version="foreign"}],[],[]),65536).Selection!);
        var changed=await f.Snapshot();Assert.NotEqual(snapshot.ContentDigest,changed.ContentDigest);
        Assert.Null(CompileOutputEnvelope.Create(result,budget).Envelope!.TryAccept(CompileRequestContext.Create(changed,1,65536).Context!).Output);
        transport.Stale=true;var other=new CompileJobId(Guid.NewGuid());
        Assert.Equal(CompileOutputStatus.Failed,(await Execute(factory.Create(other,1),other,context,budget))!.Status);
    });
    private static Task<CompileOutputEnvelopeInput?> Execute(ICompilationWorker worker, CompileJobId id,
        CompileRequestContext context, CompileOutputBudget budget)
        => Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(worker.ExecuteAsync(id,context,budget,default));
    private sealed class Transport : INativePhaseTransport
    {
        public List<NativePhaseRequest> Calls { get; }=[];public bool Stale;
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest r)
        {
            Calls.Add(r); return Task.FromResult(new NativePhaseReply(Stale?r.Tag with { RuntimeIdentity=new string('b',64)}:r.Tag,"native-return",0,true,"modeled contract reply",
                [new("main.pdf","%PDF-modeled"u8.ToArray()),new("main.aux",[1])],r.Files.Select(f=>new NativeInputFact(f.Name,f.Bytes.Length,NativeRuntimePackage.Digest(f.Bytes).Hex)).ToArray(),WasmSha256:NativeRuntimePackage.WasmHash,LoaderSha256:NativeRuntimePackage.LoaderHash));
        }
        public void RequestTermination(string job,long epoch) { }
    }
    private sealed class Reader(Dictionary<AssetId,byte[]> data) : IAssetByteReader
    { public Task<byte[]?> ReadAsync(DeclaredAsset a,CancellationToken token)=>Task.FromResult<byte[]?>(data[a.AssetId]); }
    private sealed class Backend : IRuntimeAssetBackend
    {
        private readonly Dictionary<(Guid,int),byte[]> _files=[];private readonly Dictionary<Guid,string> _catalog=[];public int CatalogReads;
        public Task<IRuntimeAssetLease?> TryAcquireAsync()=>Task.FromResult<IRuntimeAssetLease?>(new Lease());
        public Task CreateStageAsync(IRuntimeAssetLease l,Guid s,int max)=>Task.CompletedTask;
        public Task WriteAsync(IRuntimeAssetLease l,Guid s,int i,byte[] b){_files[(s,i)]=b.ToArray();return Task.CompletedTask;}
        public Task<StoredAssetBytes?> ReadAsync(Guid s,int i,long n,long max)=>Task.FromResult(_files.TryGetValue((s,i),out var b)?new StoredAssetBytes(b.Length,b.ToArray()):null);
        public Task<string?> CatalogAsync(Guid s,int max){CatalogReads++;return Task.FromResult(_catalog.GetValueOrDefault(s));}
        public Task<bool> StageExistsAsync(Guid s)=>Task.FromResult(false);
        public Task PublishAsync(IRuntimeAssetLease l,Guid s,string m,int max){_catalog[s]=m;return Task.CompletedTask;}
        public Task<bool> DiscardAsync(IRuntimeAssetLease l,Guid s)=>Task.FromResult(false);
        public ValueTask DisposeAsync(){_files.Clear();_catalog.Clear();return ValueTask.CompletedTask;}
        private sealed class Lease : IRuntimeAssetLease { public ValueTask DisposeAsync()=>ValueTask.CompletedTask; }
    }
}
