using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Client.Compilation;
using Client.Compilation.Assets;
using Client.Compilation.History;
using Client.Compilation.Native;
using Client.Compilation.Presentation;
using Client.RuntimeAssets;
using Client.Workspace;
using Client.Workspace.Domain;
using Xunit;
using Hash = Client.Workspace.Domain.SHA256;

namespace Workspace.Tests.CompileArtifactHistory;

public sealed class ArtifactHistoryTests
{
    private static readonly CompileOutputDescriptor Format = new("pdf", "fixture-v1", null), Adapter = new("historical-fixture-transport", "test-v1", null);
    private static ArtifactHistoryBudget Budget(int deadline = 30000) => new(new(32, 1048576, 8388608, 65536), 1048576, 65536, new(new(2, 256, 1048576, 2097152, 4096, 256, 256), 16384, 16, 32, deadline));
    private static async Task<AcceptedCompileOutput> Accepted(CompilationTestWorkspace fixture, byte[]? pdf = null, CompileOutputStatus status = CompileOutputStatus.Succeeded, CompileOutputDescriptor? adapter = null)
    {
        adapter ??= Adapter;
        var snapshot = await fixture.Snapshot(); var context = CompileRequestContext.Create(snapshot, 1, 4096, Format, adapter).Context!; byte[] bytes = pdf ?? [1, 2, 3, 4];
        var input = new CompileOutputEnvelopeInput
        {
            RequestId = context.RequestId,
            SnapshotIdentity = context.SnapshotIdentity,
            InputContentDigest = context.InputContentDigest,
            WorkerEpoch = 1,
            Status = status,
            Payload = status == CompileOutputStatus.Succeeded ? bytes : null,
            ClaimedLength = bytes.Length,
            ClaimedHash = status == CompileOutputStatus.Succeeded ? CompilationTestWorkspace.Hash(bytes) : null,
            OutputFormat = Format,
            OutputAdapter = adapter
        };
        return CompileOutputEnvelope.Create(input, new(1048576, 65536, 8, 64, 1024, 4096)).Envelope!.TryAccept(context).Output!;
    }
    [Fact]
    public async Task Registered_history_retains_every_known_engine_adapter_but_rejects_an_unregistered_hash()
    {
        var adapters = AuthoringWorkspaceRegistration.HistoricalOutputAdapters();
        Assert.InRange(adapters.Count, 5, CompileArtifactHistoryStore.MaxAdditionalAdapters);
        foreach (var profile in Enum.GetValues<NativeRuntimeProfile>().Where(p => NativeRuntimeProfiles.IsSuite(p) && NativeRuntimeProfiles.SuitePinsReady(p)))
            foreach (var engine in Enum.GetValues<NativeCompilerEngine>())
                Assert.Contains(NativeCompilerComponents.For(profile, engine).OutputAdapter, adapters);
        await using var f = await CompilationTestWorkspace.Open(); var io = new Backend();
        await using var store = new CompileArtifactHistoryStore(io, f.Repo, Format, Adapter, adapters);
        foreach (var adapter in adapters)
        {
            var accepted = await Accepted(f, adapter: adapter);
            var saved = await store.PersistAsync(accepted, null, Budget()); Assert.Equal(RuntimeAssetStatus.Installed, saved.Status);
            var loaded = await store.ReadAsync(saved.Key!, f.Repo.Project.Id, null, Budget());
            Assert.Equal(RuntimeAssetStatus.Ready, loaded.Status); Assert.Equal(accepted.ToPayloadArray(), loaded.Artifact!.CopyPdf());
            Assert.Equal("HistoricalOnly", loaded.Artifact.Classification);
        }
        var forged = await Accepted(f, adapter: adapters[0] with { Hash = new Hash(new string('0', 64)) });
        Assert.Equal(RuntimeAssetStatus.InvalidInput, (await store.PersistAsync(forged, null, Budget())).Status);
        Assert.Throws<ArgumentException>(() => new CompileArtifactHistoryStore(io, f.Repo, Format, Adapter,
            Enumerable.Repeat(Adapter, CompileArtifactHistoryStore.MaxAdditionalAdapters + 1).ToArray()));
    }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [Fact]
    public async Task Accepted_success_roundtrips_only_historical_and_rechecks_real_port_manifest()
    {
        await using var f = await CompilationTestWorkspace.Open(); var accepted = await Accepted(f); var io = new Backend(); var repo = new Repository(f.Repo); await using var store = new CompileArtifactHistoryStore(io, repo, Format, Adapter);
        var before = f.State(); var result = await store.PersistAsync(accepted, f.Repo.Revision!.Id, Budget()); Assert.Equal(RuntimeAssetStatus.Installed, result.Status); Assert.Equal(before, f.State()); Assert.Equal(0, repo.Writes);
        Assert.Equal(RevisionFileAssociationStatus.VerifiedFileManifest, result.Association!.Status); Assert.Equal("RevisionFileManifestOnly", result.Association.Scope); Assert.Contains("M2 revisions", result.Association.CompileSettingsAssociation);
        var restored = await store.ReadAsync(result.Key!, f.Repo.Project.Id, f.Repo.Revision.Id, Budget()); Assert.Equal(RuntimeAssetStatus.Ready, restored.Status); Assert.Equal("HistoricalOnly", restored.Artifact!.Classification); Assert.Equal(accepted.ContentIdentity, restored.Artifact.ContentIdentity);
        Assert.Equal(accepted.Snapshot.Files.Length, restored.Artifact.Files.Length); Assert.Equal(accepted.Snapshot.Selection.Options.ToArray(), restored.Artifact.Selection.Options.ToArray()); Assert.Equal(accepted.RequestId, restored.Artifact.OriginalRequestId);
        var copy = restored.Artifact.CopyPdf(); copy[0] = 99; Assert.Equal(1, restored.Artifact.CopyPdf()[0]); Assert.Null(typeof(HistoricalCompileArtifact).GetProperty("RequestContext")); Assert.Null(typeof(HistoricalCompileArtifact).GetProperty("IsCurrent")); Assert.Empty(typeof(HistoricalCompileArtifact).GetConstructors());
        var duplicate = await store.PersistAsync(accepted, null, Budget()); Assert.NotEqual(result.Key!.RecordId, duplicate.Key!.RecordId); Assert.Equal(result.Key.ContentDigest, duplicate.Key.ContentDigest); Assert.Equal(RevisionFileAssociationStatus.Unassociated, duplicate.Association!.Status);
    }
    [Fact]
    public async Task Equal_content_from_distinct_captures_has_distinct_historical_provenance_not_freshness()
    {
        await using var f = await CompilationTestWorkspace.Open(); await using var store = new CompileArtifactHistoryStore(new Backend(), new Repository(f.Repo), Format, Adapter);
        var first = await store.PersistAsync(await Accepted(f), null, Budget()); var second = await store.PersistAsync(await Accepted(f), null, Budget());
        Assert.Equal(first.Key!.ContentDigest, second.Key!.ContentDigest); Assert.NotEqual(first.Key.StorageInventoryDigest, second.Key.StorageInventoryDigest); Assert.NotEqual(first.Artifact!.OriginalRequestId, second.Artifact!.OriginalRequestId); Assert.NotEqual(first.Artifact.OriginalSnapshot.CaptureId, second.Artifact.OriginalSnapshot.CaptureId);
    }
    [Fact]
    public async Task Same_files_with_changed_compile_settings_only_prove_file_manifest_not_durable_settings()
    {
        await using var f = await CompilationTestWorkspace.Open(); var original = (await Accepted(f)).ContentIdentity;
        f.Selection.SetSelection(CompilationSelection.Create(new(f.Main, new("different-engine", "different-version", new Hash(new string('b', 64))), [], [], [new("mode", "unsaved-settings")]), 65536).Selection!);
        await using var store = new CompileArtifactHistoryStore(new Backend(), new Repository(f.Repo), Format, Adapter); var result = await store.PersistAsync(await Accepted(f), f.Repo.Revision!.Id, Budget());
        Assert.NotEqual(original, result.Artifact!.ContentIdentity); Assert.Equal(RevisionFileAssociationStatus.VerifiedFileManifest, result.Association!.Status); Assert.Equal("RevisionFileManifestOnly", result.Association.Scope); Assert.Contains("Unavailable", result.Association.CompileSettingsAssociation); Assert.Equal("HistoricalOnly", result.Artifact.Classification);
    }
    [Theory]
    [InlineData(CompileOutputStatus.Failed)]
    [InlineData(CompileOutputStatus.Cancelled)]
    [InlineData(CompileOutputStatus.Unsupported)]
    public async Task Non_success_or_over_budget_never_enters_storage(CompileOutputStatus status)
    {
        await using var f = await CompilationTestWorkspace.Open(); var io = new Backend(); await using var store = new CompileArtifactHistoryStore(io, new Repository(f.Repo), Format, Adapter);
        Assert.Equal(RuntimeAssetStatus.InvalidInput, (await store.PersistAsync(await Accepted(f, status: status), null, Budget())).Status); Assert.Equal(0, io.Acquires);
        Assert.Equal(RuntimeAssetStatus.InvalidInput, (await store.PersistAsync(await Accepted(f), null, Budget() with { MaxPdfBytes = 1 })).Status); Assert.Equal(0, io.Acquires);
    }
    [Theory]
    [InlineData("main")]
    [InlineData("include")]
    [InlineData("binary")]
    [InlineData("rename")]
    [InlineData("bom")]
    [InlineData("id")]
    [InlineData("missing")]
    public async Task Complete_revision_manifest_not_head_or_saved_echo_establishes_association(string mode)
    {
        await using var f = await CompilationTestWorkspace.Open(); var accepted = await Accepted(f); var repo = new Repository(f.Repo); await using var store = new CompileArtifactHistoryStore(new Backend(), repo, Format, Adapter);
        var saved = await store.PersistAsync(accepted, null, Budget()); var old = f.Repo.Revision!; var files = old.Files.ToArray(); int index = mode == "main" ? Array.FindIndex(files, x => x.Id == f.Main) : mode == "binary" ? Array.FindIndex(files, x => x.MediaKind == "binary") : Array.FindIndex(files, x => x.Id != f.Main);
        files[index] = mode switch { "main" or "include" or "binary" => files[index] with { ContentHash = new Hash(new string('f', 64)) }, "rename" => files[index] with { RelativePath = "renamed.tex" }, "bom" => files[index] with { HasBom = !files[index].HasBom }, "id" => files[index] with { Id = FileId.New() }, _ => files[index] };
        repo.RevisionOverride = old with { Files = mode == "missing" ? files.Skip(1).ToImmutableArray() : files.ToImmutableArray() };
        var result = await store.ReadAsync(saved.Key!, f.Repo.Project.Id, old.Id, Budget()); Assert.Equal(RuntimeAssetStatus.Ready, result.Status); Assert.NotNull(result.Artifact); Assert.Equal(RevisionFileAssociationStatus.Mismatch, result.Association!.Status); Assert.Equal(0, repo.Writes);
    }
    [Theory]
    [InlineData("damaged")]
    [InlineData("foreign")]
    [InlineData("wrong-project")]
    public async Task Detached_revision_failure_preserves_historical_pdf(string mode)
    {
        await using var f = await CompilationTestWorkspace.Open(); var repo = new Repository(f.Repo); await using var store = new CompileArtifactHistoryStore(new Backend(), repo, Format, Adapter); var result = await store.PersistAsync(await Accepted(f), null, Budget()); repo.Mode = mode;
        var read = await store.ReadAsync(result.Key!, f.Repo.Project.Id, f.Repo.Revision!.Id, Budget()); Assert.Equal(RuntimeAssetStatus.Ready, read.Status); Assert.NotNull(read.Artifact); Assert.NotEqual(RevisionFileAssociationStatus.VerifiedFileManifest, read.Association!.Status);
    }
    [Theory]
    [InlineData("selection")]
    [InlineData("omit-file")]
    [InlineData("context")]
    [InlineData("version")]
    [InlineData("format")]
    [InlineData("unknown-field")]
    public async Task Coherent_outer_storage_hashes_do_not_approve_stale_inner_metadata(string mode)
    {
        await using var f = await CompilationTestWorkspace.Open(); var io = new Backend(); await using var store = new CompileArtifactHistoryStore(io, new Repository(f.Repo), Format, Adapter); var saved = await store.PersistAsync(await Accepted(f), null, Budget()); var key = saved.Key!;
        var json = JsonNode.Parse(io.Files[key.RecordId][0])!;
        switch (mode) { case "selection": json["selection"]!["engine"]!["version"] = "changed"; break; case "omit-file": json["files"]!.AsArray().RemoveAt(1); break; case "context": json["originalSnapshot"]!["projectId"]!["value"] = Guid.NewGuid(); break; case "version": json["version"] = 2; break; case "format": json["format"]!["id"] = "html"; break; default: json["current"] = true; break; }
        var forged = io.RebindMetadata(key, System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), Budget()); int before = io.PdfReads;
        var result = await store.ReadAsync(forged, f.Repo.Project.Id, null, Budget()); Assert.Equal(RuntimeAssetStatus.Corrupt, result.Status); Assert.Null(result.Artifact); Assert.Equal(before, io.PdfReads);
    }
    [Fact]
    public async Task Missing_pdf_wrong_key_and_failed_install_never_publish_stale_artifact()
    {
        await using var f = await CompilationTestWorkspace.Open(); var io = new Backend(); await using var store = new CompileArtifactHistoryStore(io, new Repository(f.Repo), Format, Adapter); var accepted = await Accepted(f); var old = await store.PersistAsync(accepted, null, Budget());
        Assert.Equal(RuntimeAssetStatus.InvalidInput, (await store.ReadAsync(old.Key!, ProjectId.New(), null, Budget())).Status);
        Assert.Equal(RuntimeAssetStatus.Corrupt, (await store.ReadAsync(old.Key! with { ContentDigest = new Hash(new string('0', 64)) }, f.Repo.Project.Id, null, Budget())).Status);
        io.FailWrite = true; var failed = await store.PersistAsync(accepted, null, Budget()); Assert.Equal(RuntimeAssetStatus.QuotaExceeded, failed.Status); Assert.Equal(RuntimeAssetStatus.Ready, (await store.ReadAsync(old.Key!, f.Repo.Project.Id, null, Budget())).Status);
        io.Files[old.Key!.RecordId].Remove(1); Assert.Equal(RuntimeAssetStatus.Incomplete, (await store.ReadAsync(old.Key, f.Repo.Project.Id, null, Budget())).Status);
    }
    [Theory]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("acquire")]
    public async Task Actual_foreign_IO_retains_admission_and_lease_after_cancel(string phase)
    {
        await using var f = await CompilationTestWorkspace.Open(); var io = new Backend { HoldPhase = phase }; var store = new CompileArtifactHistoryStore(io, new Repository(f.Repo), Format, Adapter); var first = store.PersistAsync(await Accepted(f), null, Budget(1000));
        await WithCleanup(async () => { await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var timed = await first.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(RuntimeAssetStatus.TimedOut, timed.Status); Assert.False(io.Hold.Task.IsCompleted); Assert.False(store.WhenSettled.IsCompleted); Assert.Equal(phase != "acquire", io.Locked); Assert.Equal(RuntimeAssetStatus.Busy, (await store.PersistAsync(await Accepted(f), null, Budget())).Status); Assert.Equal(1, io.Acquires); await store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)); Assert.Equal(phase != "acquire", io.Locked); },
            async () => { io.Hold.TrySetResult(true); await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(3)); await store.DisposeAsync(); });
        Assert.False(io.Locked);
    }
    [Fact]
    public async Task Actual_revision_read_is_bounded_when_foreign_task_ignores_cancellation()
    {
        await using var f = await CompilationTestWorkspace.Open(); var repo = new Repository(f.Repo) { Mode = "hold" }; var io = new Backend(); var store = new CompileArtifactHistoryStore(io, repo, Format, Adapter); var first = store.PersistAsync(await Accepted(f), f.Repo.Revision!.Id, Budget(1000));
        await WithCleanup(async () => { await repo.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(RuntimeAssetStatus.TimedOut, (await first.WaitAsync(TimeSpan.FromSeconds(3))).Status); Assert.False(repo.Release.Task.IsCompleted); Assert.Equal(RuntimeAssetStatus.Busy, (await store.PersistAsync(await Accepted(f), null, Budget())).Status); Assert.Equal(1, repo.Reads); Assert.Equal(0, io.Acquires); await store.DisposeAsync(); },
            async () => { repo.Release.TrySetResult(true); await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(3)); await store.DisposeAsync(); });
        Assert.Empty(io.Catalog);
    }
    [Fact]
    public async Task Post_commit_uncertainty_reconciles_without_inventing_current_or_discarding_installed()
    {
        await using var f = await CompilationTestWorkspace.Open(); var io = new Backend { HoldPhase = "publish" }; var store = new CompileArtifactHistoryStore(io, new Repository(f.Repo), Format, Adapter); using var caller = new CancellationTokenSource(); var first = store.PersistAsync(await Accepted(f), null, Budget(), caller.Token);
        await WithCleanup(async () => { await io.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); caller.Cancel(); var value = await first.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(RuntimeAssetStatus.Uncertain, value.Status); Assert.True(value.PublicationUncertain); Assert.NotNull(value.Key); Assert.False(io.Hold.Task.IsCompleted); Assert.Equal(RuntimeAssetStatus.Busy, (await store.DiscardAsync(value.Key!, Budget())).Status); io.Hold.TrySetResult(true); await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(RuntimeAssetStatus.Ready, (await store.ReadAsync(value.Key!, f.Repo.Project.Id, null, Budget())).Status); Assert.Equal(RuntimeAssetStatus.NotOwned, (await store.DiscardAsync(value.Key!, Budget())).Status); },
            async () => { io.Hold.TrySetResult(true); await store.WhenSettled.WaitAsync(TimeSpan.FromSeconds(3)); await store.DisposeAsync(); });
    }
    [Theory]
    [InlineData("huge")]
    [InlineData("count")]
    [InlineData("duplicate")]
    public async Task Metadata_bounds_and_exact_schema_reject_before_pdf_read(string mode)
    {
        await using var f = await CompilationTestWorkspace.Open(); var io = new Backend(); await using var store = new CompileArtifactHistoryStore(io, new Repository(f.Repo), Format, Adapter);
        var saved = await store.PersistAsync(await Accepted(f), null, Budget()); var key = saved.Key!; var original = io.Files[key.RecordId][0]; byte[] bytes;
        if (mode == "huge") bytes = new byte[Budget().MaxMetadataBytes + 1];
        else if (mode == "duplicate") bytes = System.Text.Encoding.UTF8.GetBytes("{\"version\":1," + System.Text.Encoding.UTF8.GetString(original)[1..]);
        else { var root = JsonNode.Parse(original)!; var files = root["files"]!.AsArray(); while (files.Count <= Budget().Input.MaxFiles) files.Add(files[0]!.DeepClone()); bytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()); }
        var forged = io.RebindMetadata(key, bytes, Budget()); int before = io.PdfReads; var result = await store.ReadAsync(forged, f.Repo.Project.Id, null, Budget());
        Assert.Equal(RuntimeAssetStatus.Corrupt, result.Status); Assert.Null(result.Artifact); Assert.Equal(before, io.PdfReads);
    }
    [Theory]
    [InlineData("acquire")]
    [InlineData("write")]
    [InlineData("read")]
    public async Task Foreign_backend_cancellation_is_failure_not_caller_or_deadline(string phase)
    {
        await using var f = await CompilationTestWorkspace.Open(); var io = new Backend { ThrowPhase = phase }; await using var store = new CompileArtifactHistoryStore(io, new Repository(f.Repo), Format, Adapter);
        var result = await store.PersistAsync(await Accepted(f), null, Budget()).WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(RuntimeAssetStatus.StorageFailure, result.Status); Assert.False(io.Locked); Assert.Empty(io.Catalog);
    }
    private static async Task WithCleanup(Func<Task> body, Func<Task> cleanup)
    {
        Exception? primary = null, secondary = null;
        try { try { await body(); } catch (Exception error) { primary = error; } }
        finally { try { await cleanup(); } catch (Exception error) { secondary = error; } }
        if (primary is not null && secondary is not null) throw new AggregateException("Original assertion first, cleanup second", primary, secondary);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (secondary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(secondary).Throw();
    }
    private sealed class Backend : IRuntimeAssetBackend
    {
        public Dictionary<Guid, Dictionary<int, byte[]>> Files = []; public Dictionary<Guid, string> Catalog = []; public int Acquires, PdfReads; public bool Locked, FailWrite; public string HoldPhase = "", ThrowPhase = ""; private bool _used; public readonly TaskCompletionSource<bool> Entered = Signal<bool>(), Hold = Signal<bool>();
        private async Task Wait(string phase) { if (ThrowPhase == phase) throw new OperationCanceledException("foreign backend TEST cancellation", new CancellationToken(true)); if (HoldPhase == phase && !_used) { _used = true; Entered.TrySetResult(true); await Hold.Task; } }
        public async Task<IRuntimeAssetLease?> TryAcquireAsync() { Acquires++; await Wait("acquire"); if (Locked) return null; Locked = true; return new Lease(this); }
        public Task CreateStageAsync(IRuntimeAssetLease lease, Guid id, int max) { Files.Add(id, []); return Task.CompletedTask; }
        public async Task WriteAsync(IRuntimeAssetLease lease, Guid id, int i, byte[] bytes) { await Wait("write"); if (FailWrite) throw new RuntimeAssetIoException(RuntimeAssetStatus.QuotaExceeded); Files[id][i] = bytes.ToArray(); }
        public async Task<StoredAssetBytes?> ReadAsync(Guid id, int i, long expected, long max) { await Wait("read"); if (i == 1) PdfReads++; return Files.TryGetValue(id, out var files) && files.TryGetValue(i, out var bytes) ? new(bytes.Length, bytes.ToArray()) : null; }
        public Task<string?> CatalogAsync(Guid id, int max) => Task.FromResult(Catalog.GetValueOrDefault(id)); public Task<bool> StageExistsAsync(Guid id) => Task.FromResult(Files.ContainsKey(id));
        public async Task PublishAsync(IRuntimeAssetLease lease, Guid id, string metadata, int max) { Catalog.Add(id, metadata); await Wait("publish"); }
        public Task<bool> DiscardAsync(IRuntimeAssetLease lease, Guid id) => Task.FromResult(!Catalog.ContainsKey(id) && Files.Remove(id)); public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public HistoricalArtifactKey RebindMetadata(HistoricalArtifactKey key, byte[] bytes, ArtifactHistoryBudget budget)
        {
            Files[key.RecordId][0] = bytes; var root = JsonNode.Parse(Catalog[key.RecordId])!; var entries = new List<DeclaredAssetInput>();
            foreach (var node in root["assets"]!.AsArray()) { var a = node!; int i = a["relativePath"]!.GetValue<string>().EndsWith("metadata.json", StringComparison.Ordinal) ? 0 : 1; var data = Files[key.RecordId][i]; a["hash"] = CompilationTestWorkspace.Hash(data).Hex; a["declaredLength"] = data.Length; entries.Add(new() { AssetId = new(Guid.ParseExact(a["assetId"]!.GetValue<string>(), "N")), RelativePath = a["relativePath"]!.GetValue<string>(), DeclaredHash = CompilationTestWorkspace.Hash(data), DeclaredLength = data.Length, Version = a["version"]!.GetValue<string>() }); }
            var inventory = AssetInventoryFactory.Create(new(entries), budget.Storage.Inventory).Inventory!; root["inventoryDigest"] = inventory.Identity.Digest.Hex; Catalog[key.RecordId] = root.ToJsonString(); return key with { StorageInventoryDigest = inventory.Identity.Digest };
        }
        private sealed class Lease(Backend owner) : IRuntimeAssetLease { public ValueTask DisposeAsync() { owner.Locked = false; return ValueTask.CompletedTask; } }
    }
    private sealed class Repository(IProjectRepository inner) : IProjectRepository
    {
        public string Mode = ""; public ProjectRevision? RevisionOverride; public int Reads, Writes; public readonly TaskCompletionSource<bool> Entered = Signal<bool>(), Release = Signal<bool>();
        public async Task<RevisionManifest> ReadRevisionAsync(ProjectId p, RevisionId r, CancellationToken ct) { Reads++; if (Mode == "hold") { Entered.TrySetResult(true); await Release.Task; } if (Mode == "foreign") throw new OperationCanceledException("foreign TEST token", new CancellationToken(true)); var value = await inner.ReadRevisionAsync(p, r, CancellationToken.None); if (Mode == "damaged") return RevisionManifest.Damaged(value.Revision, "TEST damage"); if (Mode == "wrong-project") return RevisionManifest.Ok(value.Revision with { ProjectId = ProjectId.New() }); return RevisionManifest.Ok(RevisionOverride ?? value.Revision); }
        public Task<ProjectSnapshot> OpenAsync(ProjectId p, CancellationToken ct) => inner.OpenAsync(p, ct); public Task<byte[]> ReadContentAsync(Hash h, CancellationToken ct) => inner.ReadContentAsync(h, ct);
        public Task<CommitResult> CommitAsync(ProjectCommit c, RevisionId? e, CancellationToken ct) { Writes++; throw new InvalidOperationException("History must not save."); }
        public Task<HistoryPage> ReadHistoryAsync(ProjectId p, HistoryCursor? c, CancellationToken ct) => inner.ReadHistoryAsync(p, c, ct); public Task<Project> CreateProjectAsync(string n, CancellationToken ct) => throw new NotSupportedException(); public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) => throw new NotSupportedException(); public Task DeleteProjectAsync(ProjectId p, CancellationToken ct) => throw new NotSupportedException(); public Task<CommitResult> RepairHeadAsync(ProjectId p, RevisionId a, RevisionId b, CancellationToken ct) => throw new NotSupportedException(); public Task<ImportPublishResult> PublishImportAsync(StagedProject p, CancellationToken ct) => throw new NotSupportedException(); public Task<CommitResult> CreateRestoreRevisionAsync(ProjectId p, RevisionId a, ProjectRevision r, RevisionId b, string reason, CancellationToken ct) => throw new NotSupportedException();
    }
}
