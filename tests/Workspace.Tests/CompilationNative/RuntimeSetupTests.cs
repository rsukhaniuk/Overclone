using System.Text;
using System.Text.Json;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Client.RuntimeAssets;
using Xunit;

namespace Workspace.Tests.CompilationNative;

public sealed class RuntimeSetupTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[{\"name\":\"../x\",\"length\":0,\"sha256\":\"x\"}]")]
    public async Task Invalid_pins_never_open_resource_streams_or_touch_storage(string pins)
    {
        var backend = new SetupBackend(); var selection = new SetupSelection { Value = "prior selection" };
        await using var setup = new NativeRuntimeSetup(new(backend), selection);
        int reads = 0;
        await setup.InstallAsync(Encoding.UTF8.GetBytes(pins), [new("unlisted", 0, _ => { reads++; throw new Exception(); })]);
        Assert.Equal(RuntimeSetupState.InvalidSelection, setup.State); Assert.False(setup.Busy);
        Assert.Equal(0, reads); Assert.Equal(0, backend.Calls); Assert.Equal("prior selection", selection.Value);
    }
    [Fact]
    public async Task Oversized_json_and_forged_metadata_reject_before_runtime_IO()
    {
        var backend = new SetupBackend(); var selection = new SetupSelection { Value = "{\"version\":1,\"approved\":true}" };
        await using var setup = new NativeRuntimeSetup(new(backend), selection);
        await setup.InstallAsync(new byte[LocalRuntimeSelection.MaxPinsBytes + 1], []);
        Assert.Equal(RuntimeSetupState.InvalidSelection, setup.State);
        await setup.RecheckAsync(); Assert.Equal(RuntimeSetupState.InvalidSelection, setup.State); Assert.Equal(0, backend.Calls);
    }
    [Fact]
    public async Task Default_is_inert_and_empty_recheck_does_not_read_runtime_store()
    {
        var backend = new SetupBackend(); var selection = new SetupSelection();
        await using var setup = new NativeRuntimeSetup(new(backend), selection);
        Assert.Equal(0, backend.Calls); Assert.Equal(0, selection.Reads); Assert.Equal(RuntimeSetupState.Empty, setup.State);
        await setup.RecheckAsync(); Assert.Equal(RuntimeSetupState.Empty, setup.State); Assert.Equal(0, backend.Calls);
    }
}

public sealed class AuthoringRuntimeSetupFactAttribute : FactAttribute
{
    public AuthoringRuntimeSetupFactAttribute()
    { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUTHORING_RUNTIME_DIR"))) Skip = "Requires explicitly mounted AUTHORING_RUNTIME_DIR; no native resources shipped."; }
}

// Real exact-497 byte workflow with finite memory storage (not a worker/native execution test).
public sealed class AuthoringRuntimeSetupTests
{
    private static readonly Lazy<(byte[] Json, NativeResourcePin[] Pins, Dictionary<string, byte[]> Bytes)> Fixture = new(() =>
    {
        var root = Environment.GetEnvironmentVariable("AUTHORING_RUNTIME_DIR")!;
        var json = File.ReadAllBytes(Path.Combine(root, "pins.json")); var pins = LocalRuntimeSelection.ParsePins(json).ToArray();
        return (json, pins, pins.ToDictionary(p => p.Name, p => File.ReadAllBytes(Path.Combine(root, "files", p.Name)), StringComparer.Ordinal));
    });
    private static LocalRuntimeFile[] Files() => Fixture.Value.Pins.Select(p => new LocalRuntimeFile(p.Name, p.Length, _ => Task.FromResult(Fixture.Value.Bytes[p.Name]))).ToArray();
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [AuthoringRuntimeSetupFact]
    public async Task Exact497_install_reopen_fresh_bytes_then_corrupt_missing_and_selection_identity_checks()
    {
        var backend = new SetupBackend(); var selection = new SetupSelection();
        await using (var setup = new NativeRuntimeSetup(new(backend), selection))
        {
            await setup.InstallAsync(Fixture.Value.Json, Files()); Assert.Equal(RuntimeSetupState.Installed, setup.State); Assert.Equal(497, setup.Completed);
        }
        var mapping = LocalRuntimeSelection.Decode(selection.Value!); Assert.Equal(LocalRuntimeSelection.Profile, mapping.Profile);
        Assert.Equal(497, mapping.Resources.Length); Assert.Equal(1, selection.Writes);
        await using var reopened = new NativeRuntimeSetup(new(backend), selection);
        await reopened.RecheckAsync(); Assert.Equal(RuntimeSetupState.Installed, reopened.State);
        backend.Files[mapping.InstallationId][0][0] ^= 1;
        await reopened.RecheckAsync(); Assert.Equal(RuntimeSetupState.Corrupt, reopened.State);
        backend.Files[mapping.InstallationId].Remove(0);
        await reopened.RecheckAsync(); Assert.Equal(RuntimeSetupState.Missing, reopened.State);
        var calls = backend.Calls; selection.Value = selection.Value!.Replace(mapping.InventoryDigest, new string('a', 64));
        await reopened.RecheckAsync(); Assert.Equal(RuntimeSetupState.InvalidSelection, reopened.State); Assert.Equal(calls, backend.Calls);
    }
    [AuthoringRuntimeSetupFact]
    public async Task All_selection_and_pin_shape_validation_precedes_any_resource_IO()
    {
        var valid = Encoding.UTF8.GetString(Fixture.Value.Json);
        string firstName = Fixture.Value.Pins[0].Name;
        var altered = new[] {
            valid.Replace("\"name\":", "\"unknown\":0,\"name\":"),
            valid.Replace("\"name\":", "\"name\":\"duplicate-field\",\"name\":"),
            valid.Replace(firstName, NativeRuntimePackage.NativeFormatName),
            valid.Replace(firstName, "../outside"),
            valid.Replace(firstName, new string('a', 201)),
            valid.Replace(Fixture.Value.Pins[0].Sha256, new string('0',64)),
            valid.Replace(firstName, Fixture.Value.Pins[1].Name)
        };
        int reads = 0; var files = Files().Select(f => f with { Read = _ => { reads++; throw new Exception("Must not read"); } }).ToArray();
        var backend = new SetupBackend(); var selection = new SetupSelection { Value = "prior" };
        await using var setup = new NativeRuntimeSetup(new(backend), selection);
        foreach (var json in altered) { await setup.InstallAsync(Encoding.UTF8.GetBytes(json), files); Assert.Equal(RuntimeSetupState.InvalidSelection, setup.State); }
        foreach (var input in new[] { files[..^1], files.Concat([files[0]]).ToArray(), files.Select((f,i) => i == 0 ? f with { Name = files[1].Name } : f).ToArray(), files.Select((f,i) => i == 0 ? f with { Name = "extra" } : f).ToArray(), files.Select((f,i) => i == 0 ? f with { Length = f.Length + 1 } : f).ToArray() })
        { await setup.InstallAsync(Fixture.Value.Json, input); Assert.Equal(RuntimeSetupState.InvalidSelection, setup.State); }
        Assert.Equal(0, reads); Assert.Equal(0, backend.Calls); Assert.Equal("prior", selection.Value);
    }
    [AuthoringRuntimeSetupFact]
    public async Task Cancelled_wait_retains_admission_until_actual_selected_read_settles_and_rejects_stale_intent()
    {
        var entered = Signal<bool>(); var release = Signal<byte[]>(); var backend = new SetupBackend(); var selection = new SetupSelection { Value = "prior" };
        await using var setup = new NativeRuntimeSetup(new(backend), selection);
        var files = Files().Select(f => f with { Read = _ => { entered.TrySetResult(true); return release.Task; } }).ToArray();
        var install = setup.InstallAsync(Fixture.Value.Json, files); await entered.Task;
        setup.Cancel(); await Task.Yield(); Assert.True(setup.Busy);
        await setup.InstallAsync(Fixture.Value.Json, Files()); Assert.True(setup.Busy); Assert.Equal(0, backend.Calls);
        release.SetResult([0]); await install;
        Assert.Equal(RuntimeSetupState.Cancelled, setup.State); Assert.False(setup.Busy); Assert.Equal("prior", selection.Value);
    }
    [AuthoringRuntimeSetupFact]
    public async Task Corrupt_content_and_quota_preserve_previous_selection()
    {
        var backend = new SetupBackend(); var selection = new SetupSelection();
        await using var setup = new NativeRuntimeSetup(new(backend), selection);
        await setup.InstallAsync(Fixture.Value.Json, Files()); var prior = selection.Value; Assert.NotNull(prior);
        var files = Files(); var original = files[0]; var corrupt = Fixture.Value.Bytes[original.Name].ToArray(); corrupt[0] ^= 1;
        files[0] = original with { Read = _ => Task.FromResult(corrupt) };
        await setup.InstallAsync(Fixture.Value.Json, files); Assert.Equal(RuntimeSetupState.Corrupt, setup.State); Assert.Equal(prior, selection.Value);
        backend.Failure = RuntimeAssetStatus.QuotaExceeded;
        await setup.InstallAsync(Fixture.Value.Json, Files()); Assert.Equal(RuntimeSetupState.QuotaExceeded, setup.State); Assert.Equal(prior, selection.Value);
        backend.Failure = null; await setup.RecheckAsync(); Assert.Equal(RuntimeSetupState.Installed, setup.State);
    }
    [AuthoringRuntimeSetupFact]
    public async Task Commit_is_uncancellable_and_failed_selection_commit_is_uncertain_then_recheck_reconciles()
    {
        var selection = new SetupSelection { CommitRelease = Signal<bool>(), CommitEntered = Signal<bool>() }; var backend = new SetupBackend();
        await using var setup = new NativeRuntimeSetup(new(backend), selection);
        var install = setup.InstallAsync(Fixture.Value.Json, Files()); await selection.CommitEntered.Task;
        Assert.True(setup.Busy); Assert.False(setup.CanCancel); setup.Cancel(); Assert.True(setup.Busy);
        selection.FailAfterCommit = true; selection.CommitRelease.SetResult(true); await install;
        Assert.Equal(RuntimeSetupState.Uncertain, setup.State); Assert.NotNull(selection.Value);
        await setup.RecheckAsync(); Assert.Equal(RuntimeSetupState.Installed, setup.State);
    }
    [AuthoringRuntimeSetupFact]
    public async Task Store_actual_settlement_and_lease_keep_inputs_busy_and_disposal_suppresses_late_callbacks()
    {
        var backend = new SetupBackend { WriteRelease = Signal<bool>(), WriteEntered = Signal<bool>() }; var selection = new SetupSelection { Value = "prior" };
        var store = new RuntimeAssetStore(backend); var setup = new NativeRuntimeSetup(store, selection);
        var install = setup.InstallAsync(Fixture.Value.Json, Files()); await backend.WriteEntered.Task;
        Assert.True(setup.Busy); Assert.False(setup.CanCancel); Assert.False(store.WhenSettled.IsCompleted);
        Assert.Equal(RuntimeAssetStatus.Busy, (await store.ReadAsync(Guid.NewGuid(), new string('a',64), LocalRuntimeSelection.Budget)).Status);
        await setup.DisposeAsync(); int callbacks = 0; setup.Changed += () => callbacks++;
        backend.WriteRelease.SetResult(true); await install;
        Assert.Equal(0, callbacks); Assert.False(backend.Locked); Assert.Equal("prior", selection.Value);
    }
    [AuthoringRuntimeSetupFact]
    public async Task Recheck_cancellation_waits_for_actual_store_settlement_without_admitting_new_install()
    {
        var backend = new SetupBackend(); var selection = new SetupSelection();
        var store = new RuntimeAssetStore(backend); await using var setup = new NativeRuntimeSetup(store, selection);
        await setup.InstallAsync(Fixture.Value.Json, Files()); var prior = selection.Value;
        backend.ReadEntered = Signal<bool>(); backend.ReadRelease = Signal<bool>();
        var recheck = setup.RecheckAsync(); await backend.ReadEntered.Task; setup.Cancel();
        await Task.Yield(); Assert.True(setup.Busy); Assert.False(store.WhenSettled.IsCompleted);
        int calls = backend.Calls; await setup.InstallAsync(Fixture.Value.Json, Files()); Assert.Equal(calls, backend.Calls);
        backend.ReadRelease.SetResult(true); await recheck;
        Assert.False(setup.Busy); Assert.Equal(RuntimeSetupState.Cancelled, setup.State); Assert.Equal(prior, selection.Value);
    }
    [AuthoringRuntimeSetupFact]
    public async Task Runtime_publication_uncertainty_preserves_previous_selection_and_recheck_is_explicit()
    {
        var backend = new SetupBackend(); var selection = new SetupSelection();
        await using var setup = new NativeRuntimeSetup(new(backend), selection);
        await setup.InstallAsync(Fixture.Value.Json, Files()); var prior = selection.Value;
        backend.FailAfterPublish = true;
        await setup.InstallAsync(Fixture.Value.Json, Files()); Assert.Equal(RuntimeSetupState.Uncertain, setup.State);
        Assert.Equal(prior, selection.Value); Assert.Equal(1, selection.Writes);
        await setup.RecheckAsync(); Assert.Equal(RuntimeSetupState.Installed, setup.State); Assert.Equal(prior, selection.Value);
    }
    [AuthoringRuntimeSetupFact]
    public async Task Store_failures_are_typed_and_do_not_publish_a_selection()
    {
        foreach (var (failure, expected) in new[] {
            (RuntimeAssetStatus.Unavailable, RuntimeSetupState.Unavailable),
            (RuntimeAssetStatus.PermissionDenied, RuntimeSetupState.PermissionDenied),
            (RuntimeAssetStatus.StorageFailure, RuntimeSetupState.StorageFailure) })
        {
            var backend = new SetupBackend { Failure = failure }; var selection = new SetupSelection();
            await using var setup = new NativeRuntimeSetup(new(backend), selection);
            await setup.InstallAsync(Fixture.Value.Json, Files()); Assert.Equal(expected, setup.State); Assert.Null(selection.Value);
        }
        var locked = new SetupBackend { Locked = true }; var otherSelection = new SetupSelection();
        await using var busy = new NativeRuntimeSetup(new(locked), otherSelection);
        await busy.InstallAsync(Fixture.Value.Json, Files()); Assert.Equal(RuntimeSetupState.Busy, busy.State); Assert.Null(otherSelection.Value);
    }
    [AuthoringRuntimeSetupFact]
    public async Task Caller_arrays_cannot_change_validated_selection_after_preflight()
    {
        var files = Files(); var entered = Signal<bool>(); var release = Signal<bool>(); var original = files[0];
        files[0] = original with { Read = async token => { entered.SetResult(true); await release.Task; return await original.Read(token); } };
        var json = Fixture.Value.Json.ToArray(); var selection = new SetupSelection();
        await using var setup = new NativeRuntimeSetup(new(new SetupBackend()), selection);
        var install = setup.InstallAsync(json, files); await entered.Task;
        Array.Fill(json, (byte)0); files[0] = new("unrelated", 0, _ => throw new Exception("Must not read mutated caller"));
        release.SetResult(true); await install; Assert.Equal(RuntimeSetupState.Installed, setup.State);
    }
}

internal sealed class SetupSelection : ILocalRuntimeSelectionStore
{
    public string? Value; public int Reads, Writes; public bool FailAfterCommit;
    public TaskCompletionSource<bool>? CommitEntered, CommitRelease;
    public Task<string?> ReadAsync() { Reads++; return Task.FromResult(Value); }
    public async Task WriteAsync(string text)
    {
        Writes++; CommitEntered?.TrySetResult(true); if (CommitRelease is not null) await CommitRelease.Task;
        Value = text; if (FailAfterCommit) throw new IOException("Modeled uncertain metadata commit");
    }
}
// Finite real-byte backend; no fabricated receipt or compiler-ready/native replies.
internal sealed class SetupBackend : IRuntimeAssetBackend
{
    public int Calls; public bool Locked, FailAfterPublish; public RuntimeAssetStatus? Failure;
    public TaskCompletionSource<bool>? WriteEntered, WriteRelease, ReadEntered, ReadRelease;
    public Dictionary<Guid, Dictionary<int, byte[]>> Files = [];
    private readonly Dictionary<Guid, string> _catalog = [];
    public Task<IRuntimeAssetLease?> TryAcquireAsync() { Calls++; if (Failure is { } failure) throw new RuntimeAssetIoException(failure); if (Locked) return Task.FromResult<IRuntimeAssetLease?>(null); Locked = true; return Task.FromResult<IRuntimeAssetLease?>(new Lease(this)); }
    public Task CreateStageAsync(IRuntimeAssetLease lease, Guid id, int max) { Calls++; Files.Add(id, []); return Task.CompletedTask; }
    public async Task WriteAsync(IRuntimeAssetLease lease, Guid id, int i, byte[] bytes) { Calls++; WriteEntered?.TrySetResult(true); if (WriteRelease is not null) await WriteRelease.Task; Files[id][i] = bytes.ToArray(); }
    public async Task<StoredAssetBytes?> ReadAsync(Guid id, int i, long expected, long max) { Calls++; ReadEntered?.TrySetResult(true); if (ReadRelease is not null) await ReadRelease.Task; return Files.TryGetValue(id, out var f) && f.TryGetValue(i, out var b) ? new(b.Length, b.ToArray()) : null; }
    public Task<string?> CatalogAsync(Guid id, int max) { Calls++; return Task.FromResult(_catalog.GetValueOrDefault(id)); }
    public Task<bool> StageExistsAsync(Guid id) { Calls++; return Task.FromResult(Files.ContainsKey(id)); }
    public Task PublishAsync(IRuntimeAssetLease lease, Guid id, string text, int max) { Calls++; _catalog.Add(id, text); if (FailAfterPublish) throw new IOException("Modeled failure after real publication"); return Task.CompletedTask; }
    public Task<bool> DiscardAsync(IRuntimeAssetLease lease, Guid id) => throw new Exception("Setup must never delete stages");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private sealed class Lease(SetupBackend owner) : IRuntimeAssetLease { public ValueTask DisposeAsync() { owner.Locked = false; return ValueTask.CompletedTask; } }
}
