using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Client.Compilation;
using Client.Compilation.Assets;
using Client.Compilation.History;
using Client.RuntimeAssets;
using Client.Pdf;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Storage;
using Microsoft.JSInterop;
using Hash = Client.Workspace.Domain.SHA256;

namespace ArtifactBrowserHarness;

public static class HarnessApi
{
    public static string Last { get; private set; } = "";
    private const string Boundary = "EXPLICIT_HISTORICAL_NATIVE_FIXTURE_NOT_COMPILED_FROM_HARNESS_SOURCE_OR_N11";
    private const string LoaderHash = "040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b", WasmHash = "ea5a9a81d6e200fb59ccaced8ecd510d84999c648acf4a13e4ebbef07b1e2e8b";
    private static readonly AssetId Loader = new(new Guid("11111111-1111-1111-1111-111111111111")), Wasm = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private static readonly CompileOutputDescriptor Format = new("pdf", "fixture-v1", null), Adapter = new("historical-fixture-transport", "test-v1", null);
    private static RuntimeAssetBudget RuntimeBudget => new(new(4, 256, 8388608, 16777216, 16384, 256, 256), 32768, 16, 32, 15000);
    private static ArtifactHistoryBudget Budget => new(new(16, 1048576, 8388608, 65536), 8388608, 65536, new(new(2, 256, 8388608, 16777216, 4096, 256, 256), 16384, 16, 32, 15000));
    private static IJSRuntime _js = null!; private static HttpClient _http = null!; private static IJSObjectReference _test = null!; private static WorkspaceStorage _repo = null!;
    private static RuntimeAssetStore _runtime = null!; private static CompileArtifactHistoryStore _history = null!; private static WorkspaceCoordinator? _workspace;
    private static CompilationSelectionState _selection = new(); private static CompilationSnapshotFactory? _capture; private static AcceptedCompileOutput? _accepted; private static byte[]? _fixturePdf;
    private static ArtifactStorageBackend? _holder; private static IRuntimeAssetLease? _hold;
    private sealed record Control(HistoricalArtifactKey Key, ProjectId Project, RevisionId OriginalRevision, RevisionId CurrentRevision, Guid RuntimeInstall, HistoricalArtifactKey? UnsavedKey);
    private static AssetInventory RuntimeDeclaration() => AssetInventoryFactory.Create(new(new[] { new DeclaredAssetInput { AssetId = Loader, RelativePath = "runtime/e1.js", DeclaredLength = 13562, DeclaredHash = new(LoaderHash), Version = "private-n10c1-test" }, new DeclaredAssetInput { AssetId = Wasm, RelativePath = "runtime/e1.wasm", DeclaredLength = 3434938, DeclaredHash = new(WasmHash), Version = "private-n10c1-test" } }), RuntimeBudget.Inventory).Inventory!;
    public static async Task Initialize(IJSRuntime js, HttpClient http)
    { _js = js; _http = http; _test = await js.InvokeAsync<IJSObjectReference>("import", "./test-host.js"); _repo = new(js); _runtime = new(new JsRuntimeAssetBackend(js)); _history = new(new ArtifactStorageBackend(js), _repo, Format, Adapter); Last = JsonSerializer.Serialize(new { ready = true, onlineAcceptedPresent = _accepted is not null, boundary = Boundary }); }
    private static Hash HashBytes(byte[] b) => Hash.FromBytes(System.Security.Cryptography.SHA256.HashData(b));
    private static async Task<object> Show(object v) { Last = JsonSerializer.Serialize(v); await _test.InvokeVoidAsync("show", Last); return v; }
    private static object Summary(ArtifactHistoryResult r) => new { status = r.Status.ToString(), r.Key, r.PublicationUncertain, cause = r.Cause?.ToString(), classification = r.Artifact?.Classification, associationStatus = r.Association?.Status.ToString(), associationScope = r.Association?.Scope, settingsAssociation = r.Association?.CompileSettingsAssociation };
    private static async Task<Control> Selected() => JsonSerializer.Deserialize<Control>(await _test.InvokeAsync<string>("control")) ?? throw new InvalidOperationException("TEST control");
    private static Task SaveControl(Control c) => _test.InvokeVoidAsync("saveControl", JsonSerializer.Serialize(c)).AsTask();
    private static async Task OpenWorking(ProjectId project)
    {
        if (_capture is not null) await _capture.DisposeAsync(); if (_workspace is not null) await _workspace.DisposeAsync();
        _workspace = new(_repo, new JsWorkspaceLocks(_js), new HistoryService(_repo)); await _workspace.OpenAsync(project, CancellationToken.None);
        var main = _workspace.Files.Single(f => f.RelativePath == "main.tex").Id;
        var selected = CompilationSelection.Create(new(main, new("fixture-only-not-a-compiler", "test1", new Hash(new string('a', 64))), [], [], [new("fixture-kind", "N6 passive PDF transport, no compilation")]), 65536).Selection!;
        _selection = new(); _selection.SetSelection(selected); _capture = new(_workspace, _selection);
    }
    private static async Task<AcceptedCompileOutput> AcceptedFixture()
    {
        _fixturePdf ??= await _http.GetByteArrayAsync("private-inputs/regular.pdf"); if (HashBytes(_fixturePdf).Hex != "44a48c241ff55bd29197767d746b732d5e1e86cf32c6389ad7a56c2e0735318f") throw new InvalidOperationException("Fixture pin");
        var snapshot = (await _capture!.CaptureAsync(Budget.Input, CancellationToken.None) as CompilationCaptureResult.Captured)?.Snapshot ?? throw new InvalidOperationException("Actual B1 capture");
        var context = CompileRequestContext.Create(snapshot, 1, 4096, Format, Adapter).Context!;
        var input = new CompileOutputEnvelopeInput { RequestId = context.RequestId, SnapshotIdentity = context.SnapshotIdentity, InputContentDigest = context.InputContentDigest, WorkerEpoch = 1, Status = CompileOutputStatus.Succeeded, Payload = _fixturePdf, ClaimedLength = _fixturePdf.Length, ClaimedHash = HashBytes(_fixturePdf), OutputFormat = Format, OutputAdapter = Adapter };
        _accepted = CompileOutputEnvelope.Create(input, new(8388608, 65536, 8, 64, 1024, 4096)).Envelope!.TryAccept(context).Output ?? throw new InvalidOperationException("B2 fixture acceptance"); return _accepted;
    }
    private static async Task<string> Sentinel(ProjectId project)
    {
        var open = await _repo.OpenAsync(project, CancellationToken.None); var file = open.Revision?.Files.Single(f => f.RelativePath == "sentinel.txt") ?? throw new InvalidOperationException("Current document sentinel");
        return Encoding.UTF8.GetString(await _repo.ReadContentAsync(file.ContentHash, CancellationToken.None));
    }
    [JSInvokable]
    public static async Task<object> Setup()
    {
        byte[] loader = await _http.GetByteArrayAsync("private-inputs/e1.js.bin"), wasm = await _http.GetByteArrayAsync("private-inputs/e1.wasm.bin");
        var verified = (await new AssetInventoryVerifier().VerifyAsync(RuntimeDeclaration(), new RuntimeReader(loader, wasm), RuntimeBudget.Verification, CancellationToken.None) as AssetVerificationResult.Verified)?.Inventory ?? throw new InvalidOperationException("Private runtime verification");
        var install = await _runtime.InstallAsync(verified, RuntimeBudget); if (install.Status != RuntimeAssetStatus.Installed) throw new InvalidOperationException("Private runtime install");
        byte[] main = Encoding.UTF8.GetBytes("% HISTORICAL TRANSPORT FIXTURE ONLY; never compiled here\n\\input{include.tex}\n"), include = Encoding.UTF8.GetBytes("original include fixture\n"), binary = [0, 255, 128, 42], sentinel = Encoding.UTF8.GetBytes("artifact document sentinel Привіт");
        var staged = new StagedProject("N14 private project", new[] { new StagedFile("main.tex", "text", false, main, HashBytes(main), main.Length), new StagedFile("include.tex", "text", false, include, HashBytes(include), include.Length), new StagedFile("asset.bin", "binary", false, binary, HashBytes(binary), binary.Length), new StagedFile("sentinel.txt", "text", false, sentinel, HashBytes(sentinel), sentinel.Length) }.ToImmutableArray(), []);
        var published = await _repo.PublishImportAsync(staged, CancellationToken.None); if (!published.Published || published.Project is null || published.Head is null) throw new InvalidOperationException("Actual M2 fixture import");
        await OpenWorking(published.Project.Id); var accepted = await AcceptedFixture(); var before = _workspace!.Project!.HeadRevisionId;
        var stored = await _history.PersistAsync(accepted, published.Head, Budget); if (stored.Status != RuntimeAssetStatus.Installed) throw new InvalidOperationException("Artifact persistence");
        var control = new Control(stored.Key!, published.Project.Id, published.Head.Value, published.Head.Value, install.InstallationId!.Value, null); await SaveControl(control);
        var revision = await _repo.ReadRevisionAsync(control.Project, control.OriginalRevision, CancellationToken.None);
        return await Show(new { operation = "EXPLICIT_B1_B2_ACCEPTED_FIXTURE_PERSIST", boundary = Boundary, result = Summary(stored), acceptedInputDigest = accepted.InputContentDigest, acceptedContentIdentity = accepted.ContentIdentity, acceptedSnapshot = accepted.SnapshotIdentity, compiledFiles = stored.Artifact!.Files, selection = stored.Artifact.Selection, revisionVerified = revision.Verified, revisionFiles = revision.Revision.Files, headBefore = before, headAfter = _workspace.Project.HeadRevisionId, sentinel = await Sentinel(control.Project) });
    }
    [JSInvokable]
    public static async Task<object> ReadAndNative()
    {
        var control = await Selected(); var read = await _history.ReadAsync(control.Key, control.Project, control.OriginalRevision, Budget);
        var provider = new InstalledPdfRuntimeProvider(_runtime, new(control.RuntimeInstall, RuntimeDeclaration().Identity.Digest.Hex, Loader, 13562, LoaderHash, Wasm, 3434938, WasmHash), RuntimeBudget); var runtime = await provider.ReadPackageAsync();
        var before = await _test.InvokeAsync<WorkerState>("workers"); string pdfStatus = "NotInvokedHistoricalUnavailable"; string? text = null, raster = null; int? nativeStatus = null; long rasterBytes = 0; IReadOnlyList<string>? calls = null; string? pdfHash = null;
        if (read.Artifact is { } artifact)
        { await using var inspector = new JsPdfInspector(_js, provider); var bytes = artifact.CopyPdf(); var result = await inspector.InspectAsync(new(new PdfDocument(bytes), 0, new(), "Україно", true, 240, 340)); pdfStatus = result.Status.ToString(); text = result.Page?.Text; var pixels = result.Page?.CopyBgra() ?? []; raster = pixels.Length == 0 ? null : PdfHashes.Of(pixels); rasterBytes = pixels.Length; nativeStatus = result.NativeStatus; calls = result.NativeCalls; pdfHash = result.DocumentSha256; }
        var after = await _test.InvokeAsync<WorkerState>("workers"); var current = await _repo.OpenAsync(control.Project, CancellationToken.None);
        return await Show(new
        {
            operation = "HISTORICAL_PDF_READ_NOT_CURRENT",
            boundary = Boundary,
            onlineAcceptedPresent = _accepted is not null,
            result = Summary(read),
            files = read.Artifact?.Files,
            selection = read.Artifact?.Selection,
            identity = read.Artifact?.ContentIdentity,
            originalRequest = read.Artifact?.OriginalRequestId,
            originalSnapshot = read.Artifact?.OriginalSnapshot,
            hasLiveContextApi = typeof(HistoricalCompileArtifact).GetProperty("RequestContext") is not null,
            runtimeStatus = runtime.Store.Status.ToString(),
            runtimeLoaderHash = runtime.Package?.LoaderSha256,
            runtimeWasmHash = runtime.Package?.WasmSha256,
            pdfStatus,
            nativeStatus,
            text,
            rasterSha256 = raster,
            rasterBytes,
            pdfHash,
            nativeCalls = calls,
            before,
            after,
            currentHead = current.Project.HeadRevisionId,
            expectedHistoricalRevision = control.OriginalRevision,
            sentinel = await Sentinel(control.Project)
        });
    }
    [JSInvokable] public static async Task<object> HoldArtifactLock() { _holder = new(_js); _hold = await _holder.TryAcquireAsync(); return new { held = _hold is not null }; }
    [JSInvokable] public static async Task<object> ProbeArtifactLock() { await using var io = new ArtifactStorageBackend(_js); var lease = await io.TryAcquireAsync(); if (lease is not null) await lease.DisposeAsync(); return new { status = lease is null ? "Busy" : "Acquired" }; }
    [JSInvokable] public static async Task ReleaseArtifactLock() { if (_hold is not null) await _hold.DisposeAsync(); if (_holder is not null) await _holder.DisposeAsync(); _hold = null; _holder = null; }
    [JSInvokable]
    public static async Task<object> SaveMutation(string mode)
    {
        var control = await Selected(); if (_workspace is null || !_workspace.IsOpen) await OpenWorking(control.Project);
        if (mode == "include") _workspace!.EditText(_workspace.Files.Single(f => f.RelativePath == "include.tex").Id, "changed include fixture\n");
        else { _workspace!.EditText(_workspace.Files.Single(f => f.RelativePath == "include.tex").Id, "original include fixture\n"); _workspace.ReplaceFileBytes(_workspace.Files.Single(f => f.RelativePath == "asset.bin").Id, [1, 2, 3, 4, 5]); }
        var saved = await _workspace.SaveAsync(CancellationToken.None); var revision = _workspace.CurrentRevision!.Id; var read = await _history.ReadAsync(control.Key, control.Project, revision, Budget); var history = await _repo.ReadHistoryAsync(control.Project, null, CancellationToken.None); await SaveControl(control with { CurrentRevision = revision });
        return await Show(new { operation = "ACTUAL_M2_SAVE_WHILE_ARTIFACT_LOCK_HELD", mode, saveStatus = saved.Status.ToString(), revision, result = Summary(read), historyCount = history.Checkpoints.Count, sentinel = await Sentinel(control.Project) });
    }
    [JSInvokable]
    public static async Task<object> PersistUnsaved()
    {
        var control = await Selected(); _workspace!.EditText(_workspace.Files.Single(f => f.RelativePath == "main.tex").Id, "UNSAVED FIXTURE INPUT, not PDF producer\n"); var before = _workspace.Project!.HeadRevisionId; var output = await AcceptedFixture();
        var result = await _history.PersistAsync(output, null, Budget); await SaveControl(control with { UnsavedKey = result.Key });
        return await Show(new { operation = "UNSAVED_ACCEPTED_FIXTURE_NO_AUTO_SAVE", boundary = Boundary, result = Summary(result), headBefore = before, headAfter = _workspace.Project.HeadRevisionId, stillDirty = _workspace.IsDirty, sentinel = await Sentinel(control.Project) });
    }
    [JSInvokable]
    public static async Task<object> DamageRevision()
    {
        var control = await Selected(); var read = await _history.ReadAsync(control.Key, control.Project, null, Budget); var binary = read.Artifact!.Files.Single(f => f.MediaKind == "binary"); await _test.InvokeVoidAsync("damageRevisionBlob", binary.ContentHash.Hex); return await ReadAndNative();
    }
    [JSInvokable]
    public static async Task<object> MutateArtifact(string mode) { var control = await Selected(); await _test.InvokeVoidAsync("mutateArtifact", control.Key.RecordId.ToString("N"), mode); return await ReadAndNative(); }
    [JSInvokable]
    public static async Task<object> ReadOtherArtifact() { var c = await Selected(); var result = await _history.ReadAsync(c.UnsavedKey!, c.Project, null, Budget); return new { result = Summary(result), sentinel = await Sentinel(c.Project) }; }
    [JSInvokable]
    public static async Task<object> Recover()
    {
        var c = await Selected(); await OpenWorking(c.Project); var accepted = await AcceptedFixture(); var head = _workspace!.CurrentRevision!.Id; var value = await _history.PersistAsync(accepted, head, Budget); await SaveControl(c with { Key = value.Key!, OriginalRevision = head, CurrentRevision = head }); return await Show(new { operation = "EXPLICIT_NEW_FIXTURE_RECORD_NOT_COMPILE", boundary = Boundary, result = Summary(value), sentinel = await Sentinel(c.Project) });
    }
    [JSInvokable]
    public static async Task<object> InjectQuota()
    {
        var c = await Selected(); await using var store = new CompileArtifactHistoryStore(new FaultBackend(new ArtifactStorageBackend(_js)), _repo, Format, Adapter); var result = await store.PersistAsync(await AcceptedFixture(), null, Budget); var incomplete = await store.ReadAsync(result.Key!, c.Project, null, Budget); var prior = await _history.ReadAsync(c.Key, c.Project, c.OriginalRevision, Budget); var discard = await store.DiscardAsync(result.Key!, Budget);
        return await Show(new { operation = "INJECTED_ARTIFACT_WRITE_QUOTA_AFTER_REAL_WRITE", result = Summary(result), incomplete = Summary(incomplete), prior = Summary(prior), discard = Summary(discard), sentinel = await Sentinel(c.Project) });
    }
    [JSInvokable]
    public static async Task<object> InjectUncertainty()
    {
        var c = await Selected(); var io = new DelayBackend(new ArtifactStorageBackend(_js)); var store = new CompileArtifactHistoryStore(io, _repo, Format, Adapter); using var caller = new CancellationTokenSource(); var first = store.PersistAsync(await AcceptedFixture(), null, Budget, caller.Token);
        try { await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel(); var result = await first.WaitAsync(TimeSpan.FromSeconds(3)); var blocked = await store.DiscardAsync(result.Key!, Budget); var resolved = await _history.ReadAsync(result.Key!, c.Project, null, Budget); bool pending = !io.Release.Task.IsCompleted && !store.WhenSettled.IsCompleted; io.Release.TrySetResult(true); await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(5)); return await Show(new { operation = "INJECTED_ARTIFACT_POSTCOMMIT_DELAY", result = Summary(result), blocked = Summary(blocked), resolved = Summary(resolved), actualPendingBeforeRelease = pending, sentinel = await Sentinel(c.Project) }); }
        finally { io.Release.TrySetResult(true); await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(5)); await store.DisposeAsync(); }
    }
    private sealed class RuntimeReader(byte[] loader, byte[] wasm) : IAssetByteReader { public Task<byte[]?> ReadAsync(DeclaredAsset a, CancellationToken ct) => Task.FromResult<byte[]?>(a.AssetId == Loader ? loader : wasm); }
    public sealed class WorkerState { public int Created { get; set; } public int Terminated { get; set; } public int Active { get; set; } }
    private class ForwardBackend(IRuntimeAssetBackend inner) : IRuntimeAssetBackend
    { public Task<IRuntimeAssetLease?> TryAcquireAsync() => inner.TryAcquireAsync(); public Task CreateStageAsync(IRuntimeAssetLease l, Guid id, int m) => inner.CreateStageAsync(l, id, m); public virtual Task WriteAsync(IRuntimeAssetLease l, Guid id, int i, byte[] b) => inner.WriteAsync(l, id, i, b); public Task<StoredAssetBytes?> ReadAsync(Guid id, int i, long n, long m) => inner.ReadAsync(id, i, n, m); public Task<string?> CatalogAsync(Guid id, int m) => inner.CatalogAsync(id, m); public Task<bool> StageExistsAsync(Guid id) => inner.StageExistsAsync(id); public virtual Task PublishAsync(IRuntimeAssetLease l, Guid id, string s, int m) => inner.PublishAsync(l, id, s, m); public Task<bool> DiscardAsync(IRuntimeAssetLease l, Guid id) => inner.DiscardAsync(l, id); public ValueTask DisposeAsync() => inner.DisposeAsync(); }
    private sealed class FaultBackend(IRuntimeAssetBackend inner) : ForwardBackend(inner) { public override async Task WriteAsync(IRuntimeAssetLease l, Guid id, int i, byte[] b) { await base.WriteAsync(l, id, i, b); throw new RuntimeAssetIoException(RuntimeAssetStatus.QuotaExceeded); } }
    private sealed class DelayBackend(IRuntimeAssetBackend inner) : ForwardBackend(inner) { public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously); public override async Task PublishAsync(IRuntimeAssetLease l, Guid id, string s, int m) { await base.PublishAsync(l, id, s, m); Entered.TrySetResult(true); await Release.Task; } }
}
