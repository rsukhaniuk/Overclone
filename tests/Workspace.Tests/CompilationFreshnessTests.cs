using System.Collections.Immutable;
using Client.Compilation;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;

namespace Workspace.Tests;

public sealed class CompilationFreshnessTests
{
    public static IEnumerable<object[]> Changes()
    {
        foreach (var laterFile in new[] { false, true })
            foreach (var change in new[] { "other-edit", "binary", "rename", "delete", "create", "main", "engine", "profile-A-B-A", "open", "close", "close-open", "head-only" })
                yield return [laterFile, change];
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task Full_identity_is_rechecked_after_each_read_including_other_file_and_A_B_A_changes(bool laterFile, string change)
    {
        await using var f = await CompilationTestWorkspace.Open();
        var main = f.Main; var other = f.Workspace.Files.Single(w => w.RelativePath == "chapter.tex").Id;
        var binary = f.Workspace.Files.Single(w => w.MediaKind == "binary").Id;
        var generation = f.Workspace.WorkingGeneration; var original = f.Selection.Current.Selection!;
        CommitBarrier? committing = null; Task<CommitResult>? saving = null;
        if (change == "head-only")
        {
            // Start a real dirty save BEFORE capture, hold its commit, then let
            // only the durable head advance across the later capture read.
            Assert.True(f.Workspace.EditText(f.Workspace.Files.Single(w => w.RelativePath == "empty.txt").Id, "queued before capture"));
            generation = f.Workspace.WorkingGeneration;
            committing = f.Repo.CommitGate = new(); saving = f.Workspace.SaveAsync(None); await committing.WaitReached();
        }
        var gate = f.Hold(laterFile ? other : main);
        var pending = f.Capture.CaptureAsync(Budget, None); await gate.WaitReached();
        switch (change)
        {
            case "other-edit": Assert.True(f.Workspace.EditText(other, "changed other file")); break;
            case "binary": Assert.True(f.Workspace.ReplaceFileBytes(binary, [255, 0, 9])); break;
            case "rename": Assert.True(f.Workspace.RenameFile(other, "moved.tex")); break;
            case "delete": Assert.True(f.Workspace.DeleteFile(other)); break;
            case "create": Assert.True(f.Workspace.CreateFile("added.tex", "added")); break;
            case "main": f.Selection.SetSelection(Select(other)); break;
            case "engine": f.Selection.SetSelection(Select(main, "different-synthetic-engine")); break;
            case "profile-A-B-A":
                f.Selection.SetSelection(Select(main, options: [new("synthetic-profile", "B")]));
                f.Selection.SetSelection(original); break;
            case "open": await f.Workspace.OpenAsync(f.Repo.Project.Id, None); break;
            case "close": await f.Workspace.CloseAsync(None); break;
            case "close-open": await f.Workspace.CloseAsync(None); await f.Workspace.OpenAsync(f.Repo.Project.Id, None); break;
            case "head-only":
                committing!.Release.SetResult(); Assert.Equal(CommitStatus.Saved, (await saving!).Status);
                Assert.Equal(generation, f.Workspace.WorkingGeneration); break;
        }
        gate.Release.SetResult(f.Repo.Content[gate.Hash]);
        Assert.IsType<CompilationCaptureResult.Superseded>(await pending);
    }

    [Fact]
    public async Task Real_restore_replaces_bytes_and_advances_local_epoch_without_generation_change()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var current = f.Workspace.CurrentRevision!; var oldMain = current.Files.Single(x => x.Id == f.Main);
        byte[] oldBytes = [239, 187, 191, .. Bytes("historical\r\n")];
        f.Repo.Content[Hash(oldBytes)] = oldBytes;
        var historical = current with
        {
            Id = RevisionId.New(),
            Files = current.Files.Select(file => file.Id == oldMain.Id
            ? file with { ContentHash = Hash(oldBytes), ByteLength = oldBytes.Length } : file).ToImmutableArray()
        };
        f.Repo.Revisions[historical.Id] = historical;
        var preview = await f.Workspace.CaptureRestorePreviewAsync(historical.Id, None);
        var gate = f.Hold(); var epoch = f.Capture.CurrentWorkspaceEpoch; var generation = f.Workspace.WorkingGeneration;
        var pending = f.Capture.CaptureAsync(Budget, None); await gate.WaitReached();
        Assert.Equal(CommitStatus.Saved, (await f.Workspace.AcceptRestoreAsync(preview, None)).Status);
        Assert.Equal(generation, f.Workspace.WorkingGeneration); Assert.True(f.Capture.CurrentWorkspaceEpoch > epoch);
        gate.Release.SetResult(f.Repo.Content[gate.Hash]);
        Assert.IsType<CompilationCaptureResult.Superseded>(await pending);
        var fresh = await f.Snapshot(); Assert.Equal(oldBytes, fresh.Files.Single(x => x.FileId == oldMain.Id).ToArray());
    }

    [Fact]
    public async Task Published_import_then_open_does_not_relabel_a_pending_old_capture()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var gate = f.Hold(); var pending = f.Capture.CaptureAsync(Budget, None); await gate.WaitReached();
        var bytes = Bytes("new imported project");
        var staged = new StagedProject("new", ImmutableArray.Create(new StagedFile("main.tex", "text", false, bytes, Hash(bytes), bytes.Length)), []);
        var published = await f.Workspace.PublishImportAsync(staged, "new", None); Assert.True(published.Published);
        await f.Workspace.OpenAsync(published.Project!.Id, None);
        f.Selection.SetSelection(Select(f.Main));
        gate.Release.SetResult(f.Repo.Content[gate.Hash]);
        Assert.IsType<CompilationCaptureResult.Superseded>(await pending);
        Assert.Equal(published.Project.Id, (await f.Snapshot()).Identity.ProjectId);
    }

    [Fact]
    public async Task Out_of_order_captures_never_publish_old_input_as_new_input()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var gate = f.Hold(); var older = f.Capture.CaptureAsync(Budget, None); await gate.WaitReached();
        var other = f.Workspace.Files.Single(w => w.RelativePath == "chapter.tex");
        Assert.True(f.Workspace.EditText(other.Id, "newer input🙂"));
        var newer = await f.Snapshot(); Assert.True(f.Capture.IsCurrent(newer));
        gate.Release.SetResult(f.Repo.Content[gate.Hash]); Assert.IsType<CompilationCaptureResult.Superseded>(await older);
        Assert.Equal(Bytes("newer input🙂"), newer.Files.Single(x => x.FileId == other.Id).ToArray());
        Assert.True(f.Capture.IsCurrent(newer));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_or_dispose_settles_without_waiting_for_noncooperative_storage(bool dispose)
    {
        await using var f = await CompilationTestWorkspace.Open(); using var caller = new CancellationTokenSource();
        var gate = f.Hold(); var pending = f.Capture.CaptureAsync(Budget, caller.Token); await gate.WaitReached();
        if (dispose) await f.Capture.DisposeAsync(); else caller.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(gate.Release.Task.IsCompleted);
        if (dispose) Assert.IsType<CompilationCaptureResult.Disposed>(result); else Assert.IsType<CompilationCaptureResult.Cancelled>(result);
        gate.Release.SetResult(f.Repo.Content[gate.Hash]);
        Assert.Same(result, await pending);
        if (dispose) Assert.IsType<CompilationCaptureResult.Disposed>(await f.Capture.CaptureAsync(Budget, None));
        else Assert.IsType<CompilationCaptureResult.Captured>(await f.Capture.CaptureAsync(Budget, None));
        Assert.Equal(0, f.Repo.Commits);
    }

    [Fact]
    public async Task Disposal_does_not_dispose_injected_services_and_releases_its_subscription()
    {
        await using var f = await CompilationTestWorkspace.Open(); var snapshot = await f.Snapshot();
        var epoch = f.Capture.CurrentWorkspaceEpoch;
        await f.Capture.DisposeAsync(); await f.Capture.DisposeAsync();
        Assert.False(f.Capture.IsCurrent(snapshot));
        Assert.True(f.Workspace.EditText(f.Main, "still usable"));
        Assert.Equal(epoch, f.Capture.CurrentWorkspaceEpoch); // event subscription removed
        Assert.Equal(0, f.Repo.Commits); Assert.Equal(0, f.Lock.Releases);
        var hash = snapshot.Files[0].Hash; Assert.Equal(hash, Hash(snapshot.Files[0].ToArray()));
    }

    [Fact]
    public async Task Snapshot_freshness_is_scoped_to_its_factory_and_changes_on_head_only_save()
    {
        await using var f = await CompilationTestWorkspace.Open();
        await using var another = new CompilationSnapshotFactory(f.Workspace, f.Selection);
        Assert.True(f.Workspace.EditText(f.Workspace.Files.Single(w => w.RelativePath == "empty.txt").Id, "queued before capture"));
        var committing = f.Repo.CommitGate = new(); var saving = f.Workspace.SaveAsync(None); await committing.WaitReached();
        var snapshot = await f.Snapshot();
        Assert.Equal(f.Capture.CurrentWorkspaceEpoch, another.CurrentWorkspaceEpoch);
        Assert.False(another.IsCurrent(snapshot)); Assert.True(f.Capture.IsCurrent(snapshot));
        var generation = f.Workspace.WorkingGeneration; var epoch = f.Capture.CurrentWorkspaceEpoch;
        committing.Release.SetResult(); Assert.Equal(CommitStatus.Saved, (await saving).Status);
        Assert.Equal(generation, f.Workspace.WorkingGeneration); Assert.Equal(epoch, f.Capture.CurrentWorkspaceEpoch);
        Assert.False(f.Capture.IsCurrent(snapshot)); var current = await f.Snapshot();
        Assert.NotEqual(snapshot.Identity.BaseHeadRevisionId, current.Identity.BaseHeadRevisionId);
        Assert.Equal(snapshot.ContentDigest, current.ContentDigest); Assert.True(f.Capture.IsCurrent(current));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_identity_takes_precedence_over_late_corruption_or_storage_error(bool fault)
    {
        await using var f = await CompilationTestWorkspace.Open(); var gate = f.Hold();
        var pending = f.Capture.CaptureAsync(Budget, None); await gate.WaitReached();
        f.Selection.SetSelection(Select(f.Main, "B"));
        if (fault) gate.Release.SetException(new IOException("secret wrong-project error")); else gate.Release.SetResult([]);
        Assert.IsType<CompilationCaptureResult.Superseded>(await pending);
    }

    [Fact]
    public async Task Later_file_failure_does_not_expose_previously_materialized_partial_files()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var second = f.Workspace.Files.Single(w => w.RelativePath == "chapter.tex").DurableHash;
        f.Repo.ReadOverride = hash => hash == second ? throw new IOException("secret second-file failure") : f.Repo.Content[hash];
        var result = Assert.IsType<CompilationCaptureResult.Unavailable>(await f.Capture.CaptureAsync(Budget, None));
        Assert.Equal(second, f.Workspace.Files.Single(w => w.Id == result.File!.FileId).DurableHash);
        Assert.Equal(2, f.Repo.Reads); Assert.Equal(0, f.Repo.Commits);
    }
}
