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

// Explicit optional authoring fixture, no committed native/font/format binaries.
public sealed class AuthoringNativeFixtureTests
{
    private const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalLatex2023Authoring;
    private const string ExpectedIdentity = "8e9071ff18e5f93203266e7a85a6c4bba0597f9fba2e692c5651f68997e31894";
    private static readonly NativeRuntimeLimits Limits = new(512,33554432,67108864,Profile);
    private static readonly Lazy<(NativeResourcePin[] Pins,NativeResourceInput[] Inputs,NativeRuntimePackage Package)> Fixture = new(()=>
    {
        var root=Environment.GetEnvironmentVariable("AUTHORING_RUNTIME_DIR")??throw new InvalidOperationException("Explicit authoring fixture missing");
        var pins=JsonSerializer.Deserialize<NativeResourcePin[]>(File.ReadAllBytes(Path.Combine(root,"pins.json")),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
        var input=pins.Select(p=>new NativeResourceInput(p.Name,File.ReadAllBytes(Path.Combine(root,"files",p.Name)))).ToArray();
        var package=Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(input,pins,Limits,Profile).Package);
        Assert.Equal(ExpectedIdentity,package.Identity.Hex);Assert.Equal(497,pins.Length);
        return(pins,input,package);
    });
    [Fact]
    public void Authoring_closure_is_separate_owned_and_exact_with_unchanged_engine_and_format()
    {
        var f=Fixture.Value;Assert.Equal(Profile,f.Package.Profile);Assert.Equal("local-latex-authoring",f.Package.Bundle.Id);
        Assert.Equal("2023-11-01-p1-authoring-profile-v1",f.Package.Bundle.Version);
        Assert.Equal(NativeRuntimePackage.WasmHash,f.Package.Engine.Hash.Hex);
        var format=f.Package.Copy("pdflatex.fmt");Assert.Equal(22341856,format.Length);
        Assert.Equal(NativeRuntimeProfiles.ModernFormatHash,NativeRuntimePackage.Digest(format).Hex);
        var inputs=f.Inputs.ToArray();var index=Array.FindIndex(f.Pins,p=>p.Name=="n6.map");var bytes=inputs[index].Bytes.ToArray();inputs[index]=inputs[index] with {Bytes=bytes};
        var package=Assert.IsType<NativeRuntimePackage>(NativeRuntimePackage.Create(inputs,f.Pins,Limits,Profile).Package);bytes[0]^=1;
        Assert.Equal(f.Pins[index].Sha256,NativeRuntimePackage.Digest(package.Copy("n6.map")).Hex);
    }
    [Fact]
    public void Base481_profile_cannot_silently_expand_to497()
    {
        var f=Fixture.Value;var result=NativeRuntimePackage.Create(f.Inputs,f.Pins,new(512,33554432,67108864,NativeRuntimeProfile.LocalLatex2023),NativeRuntimeProfile.LocalLatex2023);
        Assert.Null(result.Package);Assert.Equal(NativeRuntimeIssue.ResourceLimit,result.Issue);
        Assert.Equal(NativeRuntimeIssue.InvalidLimits,NativeRuntimePackage.Create(f.Inputs,f.Pins,Limits).Issue);
        Assert.Equal(NativeRuntimeIssue.InvalidLimits,NativeRuntimePackage.Create(f.Inputs,f.Pins,Limits,(NativeRuntimeProfile)99).Issue);
    }
    [Theory]
    [InlineData("n6.map")]
    [InlineData("DejaVuSerif.ttf")]
    [InlineData("t2aenc.def")]
    [InlineData("plain.bst")]
    public void Even_self_consistent_substituted_overlay_bytes_and_expected_pins_fail_trusted_closure(string name)
    {
        var f=Fixture.Value;var inputs=f.Inputs.ToArray();var pins=f.Pins.ToArray();int i=Array.FindIndex(pins,p=>p.Name==name);Assert.True(i>=0);
        var bytes=inputs[i].Bytes.ToArray();bytes[0]^=1;inputs[i]=inputs[i] with {Bytes=bytes};pins[i]=pins[i] with {Sha256=NativeRuntimePackage.Digest(bytes).Hex};
        var result=NativeRuntimePackage.Create(inputs,pins,Limits,Profile);Assert.Null(result.Package);Assert.Equal(NativeRuntimeIssue.TechnicalHashMismatch,result.Issue);
    }
    [Fact]
    public void Omitted_overlay_and_insufficient_count_total_file_budgets_hit_the_intended_guards()
    {
        var f=Fixture.Value;int i=Array.FindIndex(f.Pins,p=>p.Name=="plain.bst");
        Assert.Equal(NativeRuntimeIssue.ResourceLimit,NativeRuntimePackage.Create(f.Inputs.Where((_,n)=>n!=i).ToArray(),f.Pins.Where((_,n)=>n!=i).ToArray(),Limits,Profile).Issue);
        Assert.Equal(NativeRuntimeIssue.ResourceLimit,NativeRuntimePackage.Create(f.Inputs,f.Pins,Limits with {MaxFiles=496},Profile).Issue);
        Assert.Equal(NativeRuntimeIssue.ResourceLimit,NativeRuntimePackage.Create(f.Inputs,f.Pins,Limits with {MaxTotalBytes=33554432},Profile).Issue);
        Assert.Equal(NativeRuntimeIssue.InvalidInventory,NativeRuntimePackage.Create(f.Inputs,f.Pins,Limits with {MaxFileBytes=16777216},Profile).Issue);
    }
    [Fact]
    public void Historical_format_and_alias_collisions_do_not_fall_back()
    {
        var f=Fixture.Value;var inputs=f.Inputs.ToArray();var pins=f.Pins.ToArray();int i=Array.FindIndex(pins,p=>p.Name=="pdflatex.fmt");
        var root=Environment.GetEnvironmentVariable("N11_RUNTIME_DIR")??throw new InvalidOperationException("Legacy fixture missing");var bytes=File.ReadAllBytes(Path.Combine(root,"files","pdflatex.fmt"));
        inputs[i]=new("pdflatex.fmt",bytes);pins[i]=new("pdflatex.fmt",bytes.Length,NativeRuntimePackage.Digest(bytes).Hex);
        Assert.Equal(NativeRuntimeIssue.TechnicalHashMismatch,NativeRuntimePackage.Create(inputs,pins,Limits,Profile).Issue);
        inputs=f.Inputs.ToArray();pins=f.Pins.ToArray();i=Array.FindIndex(pins,p=>p.Name=="n6.map");pins[i]=pins[i] with {Name=NativeRuntimePackage.NativeFormatName};inputs[i]=inputs[i] with {Name=NativeRuntimePackage.NativeFormatName};
        Assert.Equal(NativeRuntimeIssue.InvalidInventory,NativeRuntimePackage.Create(inputs,pins,Limits,Profile).Issue);
    }
    [Theory]
    [InlineData("profile")]
    [InlineData("count")]
    [InlineData("pin")]
    public async Task Bad_installed_mapping_rejects_before_any_store_io(string kind)
    {
        var resources=Fixture.Value.Pins.Select((p,i)=>new InstalledNativeResource(new(new Guid(i+1,0,0,new byte[8])),"runtime/"+p.Name,p)).ToImmutableArray();
        if(kind=="count")resources=resources.RemoveAt(0);
        if(kind=="pin"){int i=Array.FindIndex(resources.ToArray(),r=>r.Pin.Name=="plain.bst");resources=resources.SetItem(i,resources[i] with {Pin=resources[i].Pin with {Sha256=new string('a',64)}});}
        var mapping=new InstalledNativeRuntimeMapping(Guid.NewGuid(),new string('a',64),resources,kind=="profile"?NativeRuntimeProfile.LocalLatex2023:Profile);
        await using var store=new RuntimeAssetStore(new NoIo());var budget=new RuntimeAssetBudget(new(497,256,33554432,67108864,1048576,256,256),1048576,8,16,30000);
        var result=await new InstalledNativeRuntimeProvider(store,mapping,budget,Limits).ReadPackageAsync();Assert.Equal(RuntimeAssetStatus.InvalidInput,result.Store.Status);Assert.Null(result.Package);
    }
    [Fact]
    public void Version3_request_output_binding_and_plain_no_tools_behavior() => LifecycleTestHost.Run(async()=>
    {
        await using var f=await CompilationTestWorkspace.Open();Assert.True(f.Workspace.RenameFile(f.Main,"main.tex"));var package=Fixture.Value.Package;
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main,package.Engine,[package.Bundle],[],[]),65536).Selection!);
        var context=CompileRequestContext.Create(await f.Snapshot(),1,65536).Context!;var transport=new Transport();
        var plan=new NativeCompilePlan(false,3,TimeSpan.FromSeconds(10),MaxInputBytes:67108864,MaxFileBytes:33554432,UseSelectionTools:true,Profile:Profile);
        var factory=new NativeCompilationWorkerFactory(new FixedNativeRuntimeProvider(package),transport,plan);var id=new CompileJobId(Guid.NewGuid());var budget=new CompileOutputBudget(1048576,65536,32,128,4096,8192);
        var result=await Execute(factory.Create(id,1),id,context,budget);Assert.Equal(CompileOutputStatus.Succeeded,result!.Status);
        Assert.Equal(new[]{"latex","latex"},transport.Calls.Select(c=>c.Tag.Tool));Assert.All(transport.Calls,c=>{Assert.Equal(3,c.Tag.Version);Assert.Equal(Profile,c.Tag.Profile);Assert.Equal(ExpectedIdentity,c.Tag.RuntimeIdentity);});
        var accepted=CompileOutputEnvelope.Create(result,budget).Envelope!.TryAccept(context).Output;Assert.NotNull(accepted);
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main,package.Engine,[package.Bundle with {Id="local-latex-assets",Version="2023-11-01-p1-profile-v1",Hash=new("6feac49bf29674a92fb3a890524cde388587027f1c5d2f7df6905c2f5167c95d")}],[],[]),65536).Selection!);
        var otherContext=CompileRequestContext.Create(await f.Snapshot(),1,65536).Context!;Assert.NotEqual(context.InputContentDigest,otherContext.InputContentDigest);
        Assert.Null(CompileOutputEnvelope.Create(result,budget).Envelope!.TryAccept(otherContext).Output);
        transport.Calls.Clear();var otherId=new CompileJobId(Guid.NewGuid());var unsupported=await Execute(factory.Create(otherId,1),otherId,otherContext,budget);Assert.Equal(CompileOutputStatus.Unsupported,unsupported!.Status);Assert.Empty(transport.Calls);
        transport.Stale=true;otherId=new(Guid.NewGuid());var stale=await Execute(factory.Create(otherId,1),otherId,context,budget);Assert.Equal(CompileOutputStatus.Failed,stale!.Status);Assert.Single(transport.Calls);
    });
    private static Task<CompileOutputEnvelopeInput?> Execute(ICompilationWorker w,CompileJobId id,CompileRequestContext c,CompileOutputBudget b)=>Assert.IsAssignableFrom<Task<CompileOutputEnvelopeInput?>>(w.ExecuteAsync(id,c,b,default));
    private sealed class Transport:INativePhaseTransport
    {
        public List<NativePhaseRequest> Calls {get;}=[];public bool Stale;
        public Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest r){Calls.Add(r);return Task.FromResult(new NativePhaseReply(Stale?r.Tag with {Version=2,Profile=NativeRuntimeProfile.LocalLatex2023}:r.Tag,"native-return",0,true,"MODELED unit reply",[new("main.pdf","%PDF-modeled"u8.ToArray()),new("main.aux",[1])],r.Files.Select(f=>new NativeInputFact(f.Name,f.Bytes.Length,NativeRuntimePackage.Digest(f.Bytes).Hex)).ToArray(),WasmSha256:NativeRuntimePackage.WasmHash,LoaderSha256:NativeRuntimePackage.LoaderHash));}
        public void RequestTermination(string job,long epoch){}
    }
    private sealed class NoIo:IRuntimeAssetBackend
    {
        public Task<IRuntimeAssetLease?> TryAcquireAsync()=>throw new InvalidOperationException("Unexpected IO");
        public Task CreateStageAsync(IRuntimeAssetLease l,Guid id,int max)=>throw new InvalidOperationException("Unexpected IO");
        public Task WriteAsync(IRuntimeAssetLease l,Guid id,int i,byte[] b)=>throw new InvalidOperationException("Unexpected IO");
        public Task<StoredAssetBytes?> ReadAsync(Guid id,int i,long n,long max)=>throw new InvalidOperationException("Unexpected IO");
        public Task<string?> CatalogAsync(Guid id,int max)=>throw new InvalidOperationException("Unexpected IO");
        public Task<bool> StageExistsAsync(Guid id)=>throw new InvalidOperationException("Unexpected IO");
        public Task PublishAsync(IRuntimeAssetLease l,Guid id,string m,int max)=>throw new InvalidOperationException("Unexpected IO");
        public Task<bool> DiscardAsync(IRuntimeAssetLease l,Guid id)=>throw new InvalidOperationException("Unexpected IO");
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
