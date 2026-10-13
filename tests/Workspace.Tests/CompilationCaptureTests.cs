using System.Collections.Immutable;
using Client.Compilation;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Client.Workspace.Transfer;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;

namespace Workspace.Tests;

public sealed class CompilationCaptureTests
{
    [Fact]
    public async Task Missing_project_selection_and_disposed_cancelled_results_are_typed()
    {
        var repo = new Repository(); var locks = new Locks();
        await using var workspace = new WorkspaceCoordinator(repo, locks, new HistoryService(repo));
        var state = new CompilationSelectionState(); await using var capture = new CompilationSnapshotFactory(workspace, state);
        Assert.IsType<CompilationCaptureResult.NoProject>(await capture.CaptureAsync(Budget, None));
        await workspace.OpenAsync(repo.Project.Id, None);
        Assert.IsType<CompilationCaptureResult.NoSelection>(await capture.CaptureAsync(Budget, None));
        state.SetSelection(Select(workspace.Files[0].Id));
        Assert.IsType<CompilationCaptureResult.InvalidInput>(await capture.CaptureAsync(null!, None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.IsType<CompilationCaptureResult.Cancelled>(await capture.CaptureAsync(Budget, cancelled.Token));
        await capture.DisposeAsync();
        Assert.IsType<CompilationCaptureResult.Disposed>(await capture.CaptureAsync(Budget, None));
        Assert.True(workspace.EditText(workspace.Files[0].Id, "injected coordinator was not disposed"));
    }

    [Fact]
    public async Task Complete_inventory_preserves_ids_paths_binary_invalid_utf8_empty_text_and_imported_bytes()
    {
        await using var f = await CompilationTestWorkspace.Open();
        byte[] imported = [239, 187, 191, .. Bytes("імпорт🙂\r\nline\rtail\n")];
        Assert.True(f.Workspace.CreateFileFromBytes("chapters/новий.tex", imported));
        Assert.True(f.Workspace.CreateFileFromBytes("empty.bin", []));
        var filesBefore = f.Workspace.Files.Where(w => !w.Deleted).ToArray(); var stateBefore = f.State();
        var acquires = f.Lock.Acquires; var opens = f.Repo.Opens; var version = f.Selection.Current.Version; int events = 0;
        f.Workspace.WorkspaceChanged += () => events++;
        var snapshot = await f.Snapshot();
        Assert.Equal(filesBefore.Length, snapshot.Files.Length);
        foreach (var working in filesBefore)
        {
            var file = snapshot.Files.Single(c => c.FileId == working.Id);
            Assert.Equal(working.RelativePath, file.RelativePath); Assert.Equal(working.MediaKind, file.MediaKind);
            Assert.Equal(working.HasBom, file.HasBom); Assert.Equal(Hash(file.ToArray()), file.Hash);
            var expected = working.ImportedBytes ?? (working.DurableHash is { } hash ? f.Repo.Content[hash] : working.BinaryContent!);
            Assert.Equal(expected, file.ToArray());
        }
        Assert.Equal(imported, snapshot.Files.Single(c => c.RelativePath == "chapters/новий.tex").ToArray());
        Assert.Contains(snapshot.Files, c => c.RelativePath == "entry" && c.MediaKind == "text"); // no mandatory .tex suffix
        Assert.Equal(0, snapshot.Files.Single(c => c.RelativePath == "empty.bin").ByteLength);
        Assert.Equal(0, snapshot.Files.Single(c => c.RelativePath == "empty.txt").ByteLength);
        Assert.Equal(stateBefore, f.State()); Assert.Equal(0, events); Assert.Equal(version, f.Selection.Current.Version);
        Assert.Equal(acquires, f.Lock.Acquires); Assert.Equal(0, f.Lock.Queries); Assert.Equal(0, f.Lock.Releases);
        Assert.Equal(opens, f.Repo.Opens); Assert.Equal(0, f.Repo.Commits); Assert.Equal(0, f.Repo.Publishes); Assert.Equal(0, f.Repo.HeadChecks);
    }

    [Fact]
    public async Task Head_null_dirty_project_and_manual_text_use_existing_LF_BOM_policy_without_saving()
    {
        await using var f = await CompilationTestWorkspace.Open(headNull: true);
        byte[] raw = [239, 187, 191, .. Bytes("one\r\ntwo\r🙂")];
        Assert.True(f.Workspace.CreateFileFromBytes("main.tex", raw)); var main = f.Main;
        Assert.True(f.Workspace.EditText(main, "manual\r\nКиїв\remoji🙂"));
        Assert.True(f.Workspace.CreateFileFromBytes("image.png", [255, 254, 0]));
        f.Selection.SetSelection(Select(main)); var state = f.State();
        var snapshot = await f.Snapshot();
        Assert.Null(snapshot.Identity.BaseHeadRevisionId); Assert.Null(f.Workspace.CurrentRevision);
        Assert.True(f.Workspace.IsDirty); Assert.Equal(state, f.State()); Assert.Equal(0, f.Repo.Commits);
        Assert.Equal(TextContent.EncodeEdited("manual\r\nКиїв\remoji🙂", true), snapshot.Files.Single(x => x.FileId == main).ToArray());
        Assert.True(snapshot.Files.Single(x => x.FileId == main).HasBom);
    }

    [Fact]
    public async Task Read_only_dirty_working_model_is_read_without_permission_acquisition_or_state_changes()
    {
        await using var f = await CompilationTestWorkspace.Open(readOnly: true);
        // Synthetic existing dirty read-only working model. This test is NOT a
        // production edit/lock bypass: capture itself never requires write access.
        var main = f.Workspace.Files.Single(w => w.Id == f.Main);
        main.Content = "retained unsaved\r\n🙂"; main.DirtyContent = true;
        var state = f.State(); var locks = f.Lock.Acquires;
        var snapshot = await f.Snapshot();
        Assert.True(f.Workspace.IsReadOnly); Assert.Null(f.Workspace.LockId); Assert.Equal(state, f.State());
        Assert.Equal(TextContent.EncodeEdited(main.Content, main.HasBom), snapshot.Files.Single(x => x.FileId == main.Id).ToArray());
        Assert.Equal(locks, f.Lock.Acquires); Assert.Equal(0, f.Lock.Queries); Assert.Equal(0, f.Repo.Commits);
    }

    [Fact]
    public async Task Deleted_files_are_omitted_and_main_must_be_active_text()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var other = f.Workspace.Files.Single(w => w.RelativePath == "chapter.tex");
        Assert.True(f.Workspace.DeleteFile(other.Id));
        var captured = await f.Snapshot(); Assert.DoesNotContain(captured.Files, c => c.FileId == other.Id);
        f.Selection.SetSelection(Select(other.Id));
        Assert.Equal(CompilationCaptureIssue.InvalidMainFile, Assert.IsType<CompilationCaptureResult.InvalidInput>(await f.Capture.CaptureAsync(Budget, None)).Issue);
        f.Selection.SetSelection(Select(f.Workspace.Files.Single(w => w.MediaKind == "binary").Id));
        Assert.IsType<CompilationCaptureResult.InvalidInput>(await f.Capture.CaptureAsync(Budget, None));
        f.Selection.SetSelection(Select(FileId.New()));
        Assert.IsType<CompilationCaptureResult.InvalidInput>(await f.Capture.CaptureAsync(Budget, None));
    }

    [Fact]
    public async Task Mutable_import_and_binary_arrays_are_owned_before_a_durable_await()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var imported = Bytes("exact imported\r\n"); var binary = new byte[] { 255, 0, 128 };
        Assert.True(f.Workspace.CreateFileFromBytes("imported.tex", imported));
        Assert.True(f.Workspace.CreateFileFromBytes("new.bin", binary));
        var bin = f.Workspace.Files.Single(w => w.RelativePath == "new.bin");
        Assert.Same(binary, bin.Clone().BinaryContent); // demonstrates why Clone alone is insufficient
        var originalText = imported.ToArray(); var originalBinary = binary.ToArray();
        var gate = f.Hold(); var pending = f.Capture.CaptureAsync(Budget, None); await gate.WaitReached();
        imported[0] = (byte)'!'; binary[0] = 1;
        gate.Release.SetResult(f.Repo.Content[gate.Hash]);
        var snapshot = Assert.IsType<CompilationCaptureResult.Captured>(await pending).Snapshot;
        Assert.Equal(originalText, snapshot.Files.Single(w => w.RelativePath == "imported.tex").ToArray());
        Assert.Equal(originalBinary, snapshot.Files.Single(w => w.RelativePath == "new.bin").ToArray());
        Assert.Equal((byte)'!', imported[0]); Assert.Equal(1, binary[0]); // service did not normalize/write live arrays
    }

    [Fact]
    public async Task Repository_buffers_and_public_defensive_copies_cannot_mutate_owned_results()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var original = f.Repo.Content[f.Workspace.Files.Single(w => w.Id == f.Main).DurableHash!.Value].ToArray();
        var other = f.Workspace.Files.Single(w => w.RelativePath == "chapter.tex");
        var gate = f.Hold(other.Id); var pending = f.Capture.CaptureAsync(Budget, None); await gate.WaitReached();
        var shared = f.Repo.Content[f.Workspace.Files.Single(w => w.Id == f.Main).DurableHash!.Value];
        shared[3] = (byte)'?'; gate.Release.SetResult(f.Repo.Content[gate.Hash]);
        var snapshot = Assert.IsType<CompilationCaptureResult.Captured>(await pending).Snapshot;
        var file = snapshot.Files.Single(c => c.FileId == f.Main);
        Assert.Equal(original, file.ToArray()); var copy = file.ToArray(); copy[0] ^= 1;
        Assert.Equal(original, file.ByteSpan.ToArray());
        var metadataCopy = snapshot.Files.ToArray(); Array.Reverse(metadataCopy);
        await f.Capture.DisposeAsync(); Assert.Equal(original, file.ToArray());
    }

    [Fact]
    public async Task Binary_sizes_are_caller_controlled_not_analyzer_text_limits_and_preflight_avoids_large_copy()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var large = new byte[9 * 1024 * 1024]; large[0] = 255;
        Assert.True(f.Workspace.CreateFileFromBytes("large.bin", large));
        var budget = Budget with { MaxFileBytes = large.Length, MaxTotalBytes = large.Length + 1024L };
        var snapshot = await f.Snapshot(budget);
        Assert.Equal(large.Length, snapshot.Files.Single(x => x.RelativePath == "large.bin").ByteLength);
        var reads = f.Repo.Reads; long allocated = GC.GetAllocatedBytesForCurrentThread();
        var result = f.Capture.CaptureAsync(budget with { MaxFileBytes = large.Length - 1 }, None);
        long captureAllocations = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(result.IsCompletedSuccessfully); Assert.IsType<CompilationCaptureResult.QuotaExceeded>(await result);
        Assert.Equal(reads, f.Repo.Reads);
        Assert.True(captureAllocations < large.Length, "quota preflight must not clone the large binary");
        Assert.Equal(255, large[0]);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("declared-file")]
    [InlineData("declared-total")]
    [InlineData("metadata")]
    public async Task Complete_preflight_rejects_before_any_durable_read(string kind)
    {
        await using var f = await CompilationTestWorkspace.Open();
        var budget = kind switch
        {
            "count" => Budget with { MaxFiles = 1 },
            "declared-file" => Budget with { MaxFileBytes = 1 },
            "declared-total" => Budget with { MaxTotalBytes = 1 },
            _ => Budget with { MaxMetadataBytes = 1 }
        };
        var gate = f.Hold(); var result = f.Capture.CaptureAsync(budget, None);
        Assert.True(result.IsCompletedSuccessfully); Assert.IsType<CompilationCaptureResult.QuotaExceeded>(await result);
        Assert.False(gate.Reached.Task.IsCompleted); Assert.Equal(0, f.Repo.Reads);
    }

    [Theory]
    [InlineData("media")]
    [InlineData("binary-bom")]
    [InlineData("binary-text")]
    [InlineData("binary-dirty-text")]
    [InlineData("dirty-binary-missing")]
    [InlineData("text-binary")]
    [InlineData("text-dirty-binary")]
    [InlineData("hash")]
    [InlineData("missing-hash")]
    [InlineData("negative-length")]
    [InlineData("surrogate")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-path")]
    [InlineData("prefix")]
    [InlineData("traversal")]
    [InlineData("absolute")]
    [InlineData("backslash")]
    [InlineData("trim")]
    [InlineData("unicode-path")]
    [InlineData("forbidden")]
    [InlineData("null-content")]
    public async Task Invalid_model_metadata_is_not_coerced_excluded_or_read(string change)
    {
        await using var f = await CompilationTestWorkspace.Open();
        var main = f.Workspace.Files.Single(w => w.Id == f.Main); var bin = f.Workspace.Files.Single(w => w.MediaKind == "binary");
        switch (change)
        {
            case "media": main.MediaKind = "unknown"; break;
            case "binary-bom": bin.HasBom = true; break;
            case "binary-text": bin.Content = "unexpected"; break;
            case "binary-dirty-text": bin.DirtyContent = true; break;
            case "dirty-binary-missing": bin.DirtyBinary = true; bin.BinaryContent = null; break;
            case "text-binary": main.BinaryContent = [1]; break;
            case "text-dirty-binary": main.DirtyBinary = true; break;
            case "hash": main.DurableHash = new("bad"); break;
            case "missing-hash": main.DurableHash = null; break;
            case "negative-length": main.DurableLength = -1; break;
            case "surrogate": Assert.True(f.Workspace.EditText(main.Id, "\ud800")); break;
            case "duplicate-id": f.Workspace.Files.Add(main.Clone()); break;
            case "duplicate-path": bin.RelativePath = main.RelativePath; break;
            case "prefix": bin.RelativePath = "entry/child.bin"; break;
            case "traversal": main.RelativePath = "../secret.tex"; break;
            case "absolute": main.RelativePath = "/absolute.tex"; break;
            case "backslash": main.RelativePath = "a\\b.tex"; break;
            case "trim": main.RelativePath = " entry"; break;
            case "unicode-path": main.RelativePath = "\ud800/main.tex"; break;
            case "forbidden": main.RelativePath = ".ssh/key"; break;
            case "null-content": main.Content = null!; break;
        }
        var result = await f.Capture.CaptureAsync(Budget, None);
        Assert.IsType<CompilationCaptureResult.InvalidInput>(result); Assert.Equal(0, f.Repo.Reads); Assert.Equal(0, f.Repo.Commits);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("utf8")]
    [InlineData("bom")]
    [InlineData("decoded-content")]
    [InlineData("io")]
    [InlineData("adapter")]
    public async Task Corrupt_reads_and_storage_errors_reject_whole_capture_with_safe_errors(string change)
    {
        await using var f = await CompilationTestWorkspace.Open();
        var main = f.Workspace.Files.Single(w => w.Id == f.Main); var bytes = f.Repo.Content[main.DurableHash!.Value];
        f.Repo.ReadOverride = hash =>
        {
            var data = f.Repo.Content[hash].ToArray(); if (hash != main.DurableHash) return data;
            return change switch
            {
                "hash" => data.Select((b, i) => i == data.Length - 1 ? (byte)'?' : b).ToArray(),
                "length" => [.. data, 1],
                "empty" => [],
                "null" => null!,
                "utf8" => Enumerable.Repeat((byte)255, data.Length).ToArray(),
                "io" => throw new IOException("secret imported source and credential"),
                "adapter" => throw new InvalidOperationException("secret backend details"),
                _ => data
            };
        };
        if (change == "bom") main.HasBom = false;
        if (change == "decoded-content") main.Content = "wrong display metadata";
        var state = f.State();
        var result = Assert.IsType<CompilationCaptureResult.Unavailable>(await f.Capture.CaptureAsync(Budget, None));
        Assert.Equal(main.Id, result.File!.FileId); Assert.DoesNotContain("secret", result.SafeMessage);
        Assert.True(result.SafeMessage.Length < 160); Assert.Equal(state, f.State()); Assert.Equal(0, f.Repo.Commits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_byte_limits_are_checked_before_copy_hash_and_decode(bool aggregate)
    {
        await using var f = await CompilationTestWorkspace.Open();
        long declared = f.Workspace.Files.Sum(w => w.DurableLength);
        var budget = Budget with { MaxFileBytes = 200, MaxTotalBytes = declared };
        f.Repo.ReadOverride = _ => new byte[aggregate ? checked((int)declared + 1) : 201];
        var result = Assert.IsType<CompilationCaptureResult.QuotaExceeded>(await f.Capture.CaptureAsync(budget, None));
        Assert.Equal(aggregate ? CompilationCaptureIssue.TotalByteLimit : CompilationCaptureIssue.FileByteLimit, result.Issue);
        Assert.Equal(1, f.Repo.Reads);
    }

    [Theory]
    [InlineData("zero-file")]
    [InlineData("negative-file")]
    [InlineData("zero-total")]
    [InlineData("negative-metadata")]
    [InlineData("array-size")]
    [InlineData("zero-count")]
    public async Task Invalid_caller_budgets_are_typed_without_reading_or_defaulting_to_unlimited(string field)
    {
        await using var f = await CompilationTestWorkspace.Open();
        var budget = field switch
        {
            "zero-file" => Budget with { MaxFileBytes = 0 },
            "negative-file" => Budget with { MaxFileBytes = -1 },
            "zero-total" => Budget with { MaxTotalBytes = 0 },
            "negative-metadata" => Budget with { MaxMetadataBytes = -1 },
            "array-size" => Budget with { MaxFileBytes = (long)Array.MaxLength + 1 },
            _ => Budget with { MaxFiles = 0 }
        };
        var failure = Assert.IsType<CompilationCaptureResult.InvalidInput>(await f.Capture.CaptureAsync(budget, None));
        Assert.Equal(CompilationCaptureIssue.InvalidBudget, failure.Issue); Assert.Equal(0, f.Repo.Reads);
    }

    [Fact]
    public async Task Corrupt_durable_binary_is_verified_and_rejects_the_entire_snapshot()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var binary = f.Workspace.Files.Single(w => w.MediaKind == "binary");
        f.Repo.ReadOverride = hash => hash == binary.DurableHash ? new byte[] { 255, 254, 0, 129 } : f.Repo.Content[hash];
        var failure = Assert.IsType<CompilationCaptureResult.Unavailable>(await f.Capture.CaptureAsync(Budget, None));
        Assert.Equal(binary.Id, failure.File!.FileId); Assert.Equal(CompilationCaptureIssue.CorruptBytes, failure.Issue);
        Assert.Contains(binary.DurableHash!.Value, f.Repo.ReadHashes); Assert.Equal(0, f.Repo.Commits);
    }

    [Fact]
    public async Task Cancelling_a_returned_capture_does_not_retarget_or_mutate_its_owned_bytes()
    {
        await using var f = await CompilationTestWorkspace.Open(); using var caller = new CancellationTokenSource();
        var snapshot = Assert.IsType<CompilationCaptureResult.Captured>(await f.Capture.CaptureAsync(Budget, caller.Token)).Snapshot;
        var expected = snapshot.Files.Select(file => file.ToArray()).ToArray();
        caller.Cancel(); Assert.True(f.Capture.IsCurrent(snapshot));
        for (int index = 0; index < expected.Length; index++) Assert.Equal(expected[index], snapshot.Files[index].ToArray());
    }

    [Fact]
    public async Task Metadata_budget_is_exact_and_identity_does_not_alias_returned_bytes()
    {
        await using var f = await CompilationTestWorkspace.Open(); var first = await f.Snapshot();
        var exact = Budget with { MaxMetadataBytes = checked((int)first.MetadataByteLength) };
        var second = await f.Snapshot(exact); Assert.Equal(first.ContentDigest, second.ContentDigest);
        var reads = f.Repo.Reads;
        Assert.IsType<CompilationCaptureResult.QuotaExceeded>(await f.Capture.CaptureAsync(exact with { MaxMetadataBytes = exact.MaxMetadataBytes - 1 }, None));
        Assert.Equal(reads, f.Repo.Reads);
        Assert.NotEqual(first.Identity.CaptureId, second.Identity.CaptureId); Assert.True(f.Capture.IsCurrent(first)); Assert.True(f.Capture.IsCurrent(second));
    }
}
