using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Assets;
using Client.Compilation.History;
using Client.Compilation.Lifecycle;
using Client.Compilation.Native;
using Client.Pdf;
using Client.RuntimeAssets;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Storage;
using Microsoft.JSInterop;
using Hash = Client.Workspace.Domain.SHA256;
namespace CompilerStorageBrowserHarness;

// TEST orchestration only: real M2/B3/N13/native/N14 implementations own behavior.
public static class HarnessApi
{
    public static string Last { get; private set; } = "";
    private static IJSRuntime _js = null!; private static IJSObjectReference _test = null!; private static HttpClient _http = null!; private static OwnerContext _owner = null!;
    private static WorkspaceStorage _repo = null!; private static WorkspaceCoordinator? _workspace; private static RuntimeAssetStore _runtime = null!; private static CompileArtifactHistoryStore _history = null!;
    private static NativeBrowserModuleLease? _module; private static NativeBrowserModuleLoader _loader = null!; private static CompilationLifecycleService? _life; private static NativeCompilationWorkerFactory? _factory;
    private static CompilationSelectionState _selection = new(); private static InstalledNativeRuntimeProvider? _installed; private static readonly NativeRuntimePackage? _onlinePackage = null;
    private static readonly List<object> _facts = []; private static readonly Queue<NativePhaseObservation> _recent = []; private static int _phases, _loads; private static bool _long; private static object? _receipt;
    private static readonly AssetId PdfLoader = new(new Guid("11111111-1111-1111-1111-111111111111")), PdfWasm = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private const string PdfLoaderHash = "040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b", PdfWasmHash = "ea5a9a81d6e200fb59ccaced8ecd510d84999c648acf4a13e4ebbef07b1e2e8b";
    private static RuntimeAssetBudget RuntimeBudget => new(new(378, 256, 16777216, 33554432, 1048576, 256, 256), 262144, 8, 16, 30000);
    private static NativeRuntimeLimits Limits => new(512, 16777216, 33554432);
    private static ArtifactHistoryBudget HistoryBudget => new(new(32, 1048576, 8388608, 65536), 8388608, 65536, new(new(2, 256, 8388608, 16777216, 65536, 256, 256), 16384, 16, 32, 30000));
    private static CompileLifecycleOptions Options => new(new(32, 1048576, 8388608, 65536), new(8388608, 2097152, 32, 128, 4096, 1048576), 65536, TimeSpan.FromSeconds(60), NativeRuntimePackage.Pdf, NativeRuntimePackage.Adapter);
    private sealed record Control(ProjectId Project, Guid CompilerInstall, Guid PdfInstall, HistoricalArtifactKey? Historical, RevisionId Revision);
    private static Hash Digest(byte[] b) => NativeRuntimePackage.Digest(b);
    private static ImmutableArray<InstalledNativeResource> Mapped => TrustedCompilerPins.All.Select(p => new InstalledNativeResource(new(new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("N16:" + p.Name)).AsSpan(0, 16))), "runtime/" + p.Name, p)).ToImmutableArray();
    private static AssetInventory Declaration() => AssetInventoryFactory.Create(new(Mapped.Select(r => new DeclaredAssetInput { AssetId = r.AssetId, RelativePath = r.RelativePath, DeclaredLength = r.Pin.Length, DeclaredHash = new(r.Pin.Sha256), Version = "n1-technical-selected" }).ToArray()), RuntimeBudget.Inventory).Inventory!;
    private static InstalledNativeRuntimeMapping Mapping(Guid id) => new(id, Declaration().Identity.Digest.Hex, Mapped);
    private static async Task<Control> Selected() => JsonSerializer.Deserialize<Control>(await _test.InvokeAsync<string>("control")) ?? throw new InvalidOperationException("TEST selection");
    private static Task Save(Control c) => _test.InvokeVoidAsync("saveControl", JsonSerializer.Serialize(c)).AsTask();
    private static object Summary(ArtifactHistoryResult r) => new { status = r.Status.ToString(), r.Key, classification = r.Artifact?.Classification, association = r.Association?.Status.ToString(), scope = r.Association?.Scope, settings = r.Association?.CompileSettingsAssociation };
    private static void Need(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); }
    public static async Task Initialize(IJSRuntime js, HttpClient http)
    { _js = js; _http = http; _test = await js.InvokeAsync<IJSObjectReference>("import", "./test-host.js"); _owner = new(() => ((IJSInProcessObjectReference)_test).InvokeVoid("schedule")); _repo = new(js); _runtime = new(new JsRuntimeAssetBackend(js)); _history = new(new ArtifactStorageBackend(js), _repo, NativeRuntimePackage.Pdf, NativeRuntimePackage.Adapter); _loader = new(js); Last = "ready"; }
    [JSInvokable] public static void Pump() => _owner.Pump();
    private static async Task<object> Show(object value) { Last = JsonSerializer.Serialize(value); await _test.InvokeVoidAsync("show", Last); return value; }
    private static async Task<string> Sentinel(ProjectId project) { var open = await _repo.OpenAsync(project, CancellationToken.None); var f = open.Revision!.Files.Single(f => f.RelativePath == "sentinel.txt"); return Encoding.UTF8.GetString(await _repo.ReadContentAsync(f.ContentHash, CancellationToken.None)); }
    private static async Task<RuntimeAssetResult> InstallCompiler()
    {
        var bytes = new Dictionary<AssetId, byte[]>(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var r in Mapped) { var b = await _http.GetByteArrayAsync("private-inputs/compiler/" + r.Pin.Name, deadline.Token); Need(b.Length == r.Pin.Length && Digest(b).Hex == r.Pin.Sha256, "explicit native pin"); bytes.Add(r.AssetId, b); }
        var owned = (await new AssetInventoryVerifier().VerifyAsync(Declaration(), new Reader(bytes), RuntimeBudget.Verification, deadline.Token) as AssetVerificationResult.Verified)?.Inventory ?? throw new InvalidOperationException("full378 verification");
        var value = await _runtime.InstallAsync(owned, RuntimeBudget, deadline.Token); Need(value.Status == RuntimeAssetStatus.Installed, "full378 install"); return value;
    }
    [JSInvokable]
    public static Task<object> Setup() => _owner.RunAsync(async () =>
    {
        var install = await InstallCompiler(); byte[] l = await _http.GetByteArrayAsync("private-inputs/e1.js.bin"), w = await _http.GetByteArrayAsync("private-inputs/e1.wasm.bin");
        var declaration = AssetInventoryFactory.Create(new(new[] { new DeclaredAssetInput { AssetId = PdfLoader, RelativePath = "runtime/e1.js", DeclaredLength = l.Length, DeclaredHash = new(PdfLoaderHash), Version = "n10c1" }, new DeclaredAssetInput { AssetId = PdfWasm, RelativePath = "runtime/e1.wasm", DeclaredLength = w.Length, DeclaredHash = new(PdfWasmHash), Version = "n10c1" } }), RuntimeBudget.Inventory).Inventory!;
        var pdfOwned = (await new AssetInventoryVerifier().VerifyAsync(declaration, new Reader(new() { { PdfLoader, l }, { PdfWasm, w } }), RuntimeBudget.Verification, CancellationToken.None) as AssetVerificationResult.Verified)!.Inventory; var pdf = await _runtime.InstallAsync(pdfOwned, RuntimeBudget); Need(pdf.Status == RuntimeAssetStatus.Installed, "independent PDF runtime");
        var text = new[] { ("main.tex", AuthoredFixture.MainSource("SAVED")), ("chapter.tex", AuthoredFixture.Chapter("SAVED")), ("refs.bib", AuthoredFixture.Bib), ("notes-І😀.txt", "Україна 😀 separate UTF8 VFS filename\n"), ("sentinel.txt", "N16 local document sentinel Привіт") }; var files = text.Select(f => { var b = Encoding.UTF8.GetBytes(f.Item2); return new StagedFile(f.Item1, "text", false, b, Digest(b), b.Length); }).Append(new("asset.bin", "binary", false, new byte[] { 0, 255, 128, 42 }, Digest([0, 255, 128, 42]), 4)).ToImmutableArray();
        var published = await _repo.PublishImportAsync(new("N16 actual native project", files, []), CancellationToken.None); Need(published.Published, "real M2 import"); var c = new Control(published.Project!.Id, install.InstallationId!.Value, pdf.InstallationId!.Value, null, published.Head!.Value); await Save(c);
        return await Show(new { operation = "EXPLICIT_FULL378_INSTALL_AND_M2_BASE", installation = c.CompilerInstall, inventory = install.Snapshot!.Inventory, storedCount = install.Snapshot.AssetCount, onlinePackagePresent = _onlinePackage is not null, sentinel = await Sentinel(c.Project) });
    });
    private static async Task Working()
    {
        if (_workspace is not null) return; var c = await Selected(); _installed = new(_runtime, Mapping(c.CompilerInstall), RuntimeBudget, Limits); var read = await _installed.ReadPackageAsync(); _loads++; Need(read.Package is not null, "fresh installed full closure for selection"); var package = read.Package!;
        _workspace = new(_repo, new JsWorkspaceLocks(_js), new HistoryService(_repo)); await _workspace.OpenAsync(c.Project, CancellationToken.None); _selection = new(); SetSelection(package, true);
        _module = await _loader.LoadAsync(CancellationToken.None); Need(await _module.ProvisionAsync(), "explicit worker code provisioning");
        await Lifecycle(new InstalledCounter(), true);
    }
    private static void SetSelection(NativeRuntimePackage package, bool bib) { var main = _workspace!.Files.Single(f => f.RelativePath == "main.tex").Id; _selection.SetSelection(CompilationSelection.Create(new(main, package.Engine, [package.Bundle], bib ? [package.Bibtex] : [], []), 65536).Selection!); }
    private static async Task Lifecycle(INativeRuntimeProvider provider, bool bib, INativePhaseTransport? transport = null)
    {
        if (_life is not null) { await _life.DisposeAsync(); await _life.WhenIdle; await _factory!.Operations.WhenIdle; }
        _factory = new(provider, transport ?? _module!.Transport, new(bib, bib ? 4 : 2, TimeSpan.FromSeconds(50))); _factory.PhaseObserved += Observe;
        _life = new(_workspace!, _selection, _factory, new(2, 100), TimeProvider.System);
    }
    private static void Observe(NativePhaseObservation p)
    {
        _phases++; _recent.Enqueue(p); while (_recent.Count > 8) _recent.Dequeue(); if (_long) return; Need(_facts.Count < 64, "bounded selected native facts");
        _facts.Add(new { p.Tag, p.Sent, p.Reply.Outcome, p.Reply.NativeReturn, p.Reply.Entered, p.Reply.Log, p.Reply.Inputs, p.Reply.LoaderSha256, p.Reply.WasmSha256, files = p.Reply.Files.Select(f => new { f.Name, length = f.Bytes.Length, sha256 = Digest(f.Bytes).Hex, generated = f.Name.EndsWith(".aux", StringComparison.Ordinal) || f.Name == "main.bbl" ? Convert.ToBase64String(f.Bytes) : null }) });
    }
    private static void Edit(string marker, bool minimal = false)
    { Need(_workspace!.EditText(_workspace.Files.Single(f => f.RelativePath == "main.tex").Id, minimal ? "\\documentclass{article}\n\\pdfpagewidth=612bp\\pdfpageheight=792bp\n\\begin{document}\nN16-LONG-" + marker + ".\\end{document}\n" : AuthoredFixture.MainSource(marker).Replace("\\begin{document}", "\\pdfpagewidth=612bp\\pdfpageheight=792bp\n\\begin{document}", StringComparison.Ordinal)), "main edit"); if (!minimal) { _workspace.EditText(_workspace.Files.Single(f => f.RelativePath == "chapter.tex").Id, AuthoredFixture.Chapter(marker)); _workspace.EditText(_workspace.Files.Single(f => f.RelativePath == "refs.bib").Id, AuthoredFixture.Bib + "% N16 dirty bibliography " + marker + "\n"); _workspace.ReplaceFileBytes(_workspace.Files.Single(f => f.RelativePath == "asset.bin").Id, Encoding.ASCII.GetBytes("N16-ASSET-" + marker)); } }
    private static async Task<(CompileLifecycleTicket Ticket, CompileLifecycleResult Result)> Compile(CancellationToken token = default)
    { var ticket = _life!.Request(Options, token); var result = await ticket.Completion; await _life.WhenIdle; await _factory!.Operations.WhenIdle; return (ticket, result); }
    [JSInvokable]
    public static Task<object> CompileAndPersist(string marker) => _owner.RunAsync(async () =>
    {
        bool onlinePackageBefore = _onlinePackage is not null; await Working(); var c = await Selected(); var before = _workspace!.Project!.HeadRevisionId; int first = _phases; Edit(marker); var (ticket, result) = await Compile(); Need(result.Reason == CompileLifecycleReason.Succeeded && result.Output is not null, "actual native graph accepted " + result.Reason);
        var output = result.Output!; var stored = await _history.PersistAsync(output, null, HistoryBudget); Need(stored.Status == RuntimeAssetStatus.Installed, "actual compiled historical persist"); await Save(c with { Historical = stored.Key });
        return await Show(new { operation = "ACTUAL_NATIVE_B3_ACCEPTED_NOT_PASSIVE_FIXTURE", marker, reason = result.Reason.ToString(), onlinePackageBefore, installedLoads = _loads, receipt = _receipt, liveCurrent = _life!.State.IsCurrent, phaseCount = _phases - first, phases = _facts.ToArray(), output = new { output.RequestId, output.SnapshotIdentity, output.InputContentDigest, output.ContentIdentity, output.OutputHash, output.OutputLength, output.OutputFormat, output.OutputAdapter, pdf = Convert.ToBase64String(output.ToPayloadArray()!), boundSnapshot = ReferenceEquals(output.Snapshot, ticket.Snapshot), boundContext = ReferenceEquals(output.RequestContext.Snapshot, ticket.Snapshot) }, snapshot = new { ticket.Snapshot!.Identity, ticket.Snapshot.ContentDigest, selection = ticket.Snapshot.Selection, files = ticket.Snapshot.Files.Select(f => new { f.FileId, f.RelativePath, f.MediaKind, f.HasBom, f.Hash, f.ByteLength, bytes = Convert.ToBase64String(f.ToArray()) }) }, historical = Summary(stored), dirty = _workspace.IsDirty, headBefore = before, headAfter = _workspace.Project.HeadRevisionId, browser = _module!.Observations(), sentinel = await Sentinel(c.Project) });
    });
    [JSInvokable]
    public static Task<object> SaveAndAssociate() => _owner.RunAsync(async () =>
    { var c = await Selected(); var output = _life!.State.LastSuccessful!; var saved = await _workspace!.SaveAsync(CancellationToken.None); Need(saved.Status == CommitStatus.Saved, "explicit M2 save"); var revision = _workspace.CurrentRevision!.Id; var stored = await _history.PersistAsync(output, revision, HistoryBudget); var read = await _repo.ReadRevisionAsync(c.Project, revision, CancellationToken.None); await Save(c with { Historical = stored.Key, Revision = revision }); return await Show(new { operation = "EXPLICIT_SAVE_THEN_FILE_MANIFEST_ASSOCIATION", saveStatus = saved.Status.ToString(), historical = Summary(stored), revisionVerified = read.Verified, revisionFiles = read.Revision.Files, compiledFiles = stored.Artifact!.Files }); });
    [JSInvokable]
    public static Task<object> LongRun() => _owner.RunAsync(async () =>
    {
        await Working(); var fresh = await _installed!.ReadPackageAsync(); _loads++; Need(fresh.Package is not null, "longrun ONE freshly verified package"); SetSelection(fresh.Package!, false); await Lifecycle(new FixedNativeRuntimeProvider(fresh.Package), false); var before = _module!.Observations(); int start = _phases; var jobs = new List<object>(); byte[]? last = null; _long = true;
        try { for (int i = 0; i < 33; i++) { Edit("J" + i, true); var (t, r) = await Compile(); Need(r.Reason == CompileLifecycleReason.Succeeded && r.Output is not null, "longrun job " + i + " " + r.Reason); var o = r.Output!; last = o.ToPayloadArray(); jobs.Add(new { index = i, job = t.JobId, request = o.RequestId, input = o.InputContentDigest, hash = o.OutputHash, length = o.OutputLength, phases = _phases - start, foreignActive = _factory!.Operations.Active }); } }
        finally { _long = false; }
        return await Show(new { operation = "REAL_NATIVE_SAME_MODULE_MORE_THAN64_PHASES", fixedFreshPackage = true, jobs, before, after = _module.Observations(), nativePhases = _phases - start, recentObservations = _recent.Count, lastPdf = Convert.ToBase64String(last!) });
    });
    [JSInvokable]
    public static Task<object> Failures() => _owner.RunAsync(async () =>
    {
        await Working(); var fresh = await _installed!.ReadPackageAsync(); _loads++; SetSelection(fresh.Package!, true); await Lifecycle(new InstalledCounter(), true); var list = new List<object>();
        foreach (string mode in new[] { "bad-source", "bad-bib", "before-entry-cancel" }) { Edit("FAIL-" + mode); if (mode == "bad-source") _workspace!.EditText(_workspace.Files.Single(f => f.RelativePath == "main.tex").Id, "\\documentclass{article}\\begin{document}\\undefinedN16\\end{document}"); if (mode == "bad-bib") _workspace!.EditText(_workspace.Files.Single(f => f.RelativePath == "main.tex").Id, AuthoredFixture.MainSource("FAIL-bad-bib").Replace("\\bibliography{refs}", "\\bibliography{absent-database}", StringComparison.Ordinal)); int before = _phases; using var cancel = new CancellationTokenSource(); if (mode == "before-entry-cancel") cancel.Cancel(); var (_, r) = await Compile(cancel.Token); Need(r.Output is null, "no failed artifact"); list.Add(new { mode, reason = r.Reason.ToString(), hasOutput = r.Output is not null, phases = _phases - before }); }
        // Hold an ACTUAL successful first phase, then supersede. No native-blocking probe.
        var hold = new HeldTransport(_module!.Transport); await Lifecycle(new InstalledCounter(), true, hold); Edit("OLD-HELD"); var old = _life!.Request(Options); Exception? primary = null;
        try { await hold.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20)); var actual = hold.Actual ?? throw new InvalidOperationException("Actual reply missing at reached barrier"); Need(actual.NativeReturn == 0, "actual reply barrier"); Edit("LATEST"); var latest = _life.Request(Options); var superseded = await old.Completion; Need(superseded.Reason == CompileLifecycleReason.Superseded && superseded.Output is null, "stale rejected"); hold.Release.TrySetResult(true); var recovered = await latest.Completion; await _life.WhenIdle; await _factory!.Operations.WhenIdle; Need(recovered.Reason == CompileLifecycleReason.Succeeded, "fresh latest"); list.Add(new { mode = "superseded-after-native-reply", reason = superseded.Reason.ToString(), hasOutput = false, heldNative = actual.NativeReturn, recovery = recovered.Reason.ToString() }); }
        catch (Exception error) { primary = error; throw; }
        finally { hold.Release.TrySetResult(true); try { await _life.WhenIdle.WaitAsync(TimeSpan.FromSeconds(20)); await _factory!.Operations.WhenIdle.WaitAsync(TimeSpan.FromSeconds(20)); } catch (Exception cleanup) when (primary is not null) { throw new AggregateException(primary, cleanup); } }
        return await Show(new { operation = "ORDINARY_FAILURE_CANCEL_STALE_CONTROLS", cases = list, browser = _module!.Observations(), facts = _facts.ToArray() });
    });
    [JSInvokable]
    public static Task<object> DamageRuntime(string mode) => _owner.RunAsync(async () =>
    {
        var c = await Selected(); var good = await _runtime.ReadAsync(c.CompilerInstall, Declaration().Identity.Digest.Hex, RuntimeBudget); string name = mode == "corrupt" ? NativeRuntimePackage.FormatResourceName : "article.cls"; int index = good.Snapshot is { } s ? Enumerable.Range(0, s.AssetCount).Single(i => s.DeclaredAssets[i].RelativePath == "runtime/" + name) : Array.FindIndex(Mapped.OrderBy(r => r.RelativePath, StringComparer.Ordinal).ToArray(), r => r.Pin.Name == name);
        await _test.InvokeVoidAsync("damageRuntime", c.CompilerInstall.ToString("N"), index, mode); var read = await _installed!.ReadPackageAsync(); var before = _module!.Observations(); Edit("UNAVAILABLE"); var (_, r) = await Compile(); Need(r.Output is null && r.Reason == CompileLifecycleReason.Unsupported, "unavailable no artifact"); var historical = await _history.ReadAsync(c.Historical!, c.Project, null, HistoryBudget); var pdf = await Inspect(c, historical.Artifact!); var saved = await _workspace!.SaveAsync(CancellationToken.None);
        return await Show(new { operation = "ACTUAL_INSTALLED_RUNTIME_DAMAGE", mode, resource = name, status = read.Store.Status.ToString(), reason = r.Reason.ToString(), hasOutput = r.Output is not null, before, after = _module.Observations(), historical = Summary(historical), pdf, saveStatus = saved.Status.ToString(), sentinel = await Sentinel(c.Project) });
    });
    private static async Task<object> Inspect(Control c, HistoricalCompileArtifact artifact)
    { var mapping = new InstalledPdfRuntimeMapping(c.PdfInstall, PdfDeclaration().Identity.Digest.Hex, PdfLoader, 13562, PdfLoaderHash, PdfWasm, 3434938, PdfWasmHash); await using var inspector = new JsPdfInspector(_js, new InstalledPdfRuntimeProvider(_runtime, mapping, RuntimeBudget)); var result = await inspector.InspectAsync(new(new PdfDocument(artifact.CopyPdf()), 0, new(), "DIRTY", true, 240, 340)); return new { status = result.Status.ToString(), result.DocumentSha256, text = result.Page?.Text, rasterSha256 = result.Page is { } p ? PdfHashes.Of(p.CopyBgra()) : null }; }
    private static AssetInventory PdfDeclaration() => AssetInventoryFactory.Create(new(new[] { new DeclaredAssetInput { AssetId = PdfLoader, RelativePath = "runtime/e1.js", DeclaredLength = 13562, DeclaredHash = new(PdfLoaderHash), Version = "n10c1" }, new DeclaredAssetInput { AssetId = PdfWasm, RelativePath = "runtime/e1.wasm", DeclaredLength = 3434938, DeclaredHash = new(PdfWasmHash), Version = "n10c1" } }), RuntimeBudget.Inventory).Inventory!;
    [JSInvokable] public static Task<object> Recover() => _owner.RunAsync(async () => { var c = await Selected(); var value = await InstallCompiler(); await Save(c with { CompilerInstall = value.InstallationId!.Value }); _installed = new(_runtime, Mapping(value.InstallationId.Value), RuntimeBudget, Limits); var read = await _installed.ReadPackageAsync(); Need(read.Package is not null, "explicit new installation"); SetSelection(read.Package!, true); await Lifecycle(new InstalledCounter(), true); return await Show(new { operation = "EXPLICIT_NEW_INSTALL_NOT_REPAIR", previous = c.CompilerInstall, current = value.InstallationId, storedCount = value.Snapshot!.AssetCount, status = read.Store.Status.ToString() }); });
    [JSInvokable] public static Task<object> ReadHistorical() => _owner.RunAsync(async () => { var c = await Selected(); var read = await _history.ReadAsync(c.Historical!, c.Project, c.Revision, HistoryBudget); return await Show(new { historical = Summary(read), pdf = await Inspect(c, read.Artifact!), sentinel = await Sentinel(c.Project) }); });
    [JSInvokable] public static Task<object> Close() => _owner.RunAsync<object>(async () => { if (_life is not null) { await _life.DisposeAsync(); await _life.WhenIdle; await _factory!.Operations.WhenIdle; } if (_workspace is not null) await _workspace.DisposeAsync(); if (_module is not null) await _module.DisposeAsync(); return new { remainingImports = _loader.Reserved, remainingForeign = _factory?.Operations.Active }; });
    private sealed class Reader(Dictionary<AssetId, byte[]> data) : IAssetByteReader { public Task<byte[]?> ReadAsync(DeclaredAsset a, CancellationToken ct) => Task.FromResult<byte[]?>(data.GetValueOrDefault(a.AssetId)); }
    private sealed class InstalledCounter : INativeRuntimeProvider { public async Task<NativeRuntimePackage?> GetAsync() { var read = await _installed!.ReadPackageAsync(); _loads++; _receipt = new { status = read.Store.Status.ToString(), count = read.Store.Snapshot?.AssetCount, identity = read.Store.Snapshot?.Inventory.Identity, actual = read.Store.Snapshot is { } s ? s.DeclaredAssets.Select((a, i) => new { a.AssetId, a.RelativePath, length = s.ActualLength(i), hash = s.ActualHash(i) }).ToArray() : null }; return read.Package; } }
    private sealed class HeldTransport(INativePhaseTransport inner) : INativePhaseTransport
    { public readonly TaskCompletionSource<bool> Reached = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously); public NativePhaseReply? Actual; private bool _used; public async Task<NativePhaseReply> ExecuteAsync(NativePhaseRequest request) { var reply = await inner.ExecuteAsync(request); if (!_used) { _used = true; Actual = reply; Reached.TrySetResult(true); await Release.Task; } return reply; } public void RequestTermination(string job, long epoch) => inner.RequestTermination(job, epoch); }
}
