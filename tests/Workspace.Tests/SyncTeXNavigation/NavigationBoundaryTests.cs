using System.Text;
using Client.Compilation.SyncTeX;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;
using static Workspace.Tests.SyncTeXNavigation.NavigationFixture;

namespace Workspace.Tests.SyncTeXNavigation;

public sealed class NavigationBoundaryTests
{
    [Theory]
    [InlineData("edit")][InlineData("ABA")][InlineData("save")][InlineData("selection-ABA")]
    [InlineData("samehash-compile")][InlineData("takeover")][InlineData("dispose-policy")][InlineData("dispose-session")]
    public void Already_dispatched_query_never_reauthorizes_after_owner_change(string change) => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        var reply = new TaskCompletionSource<SyncTeXQueryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Query.Reply = _ => reply.Task;
        var pending = f.Inverse();
        Assert.Single(f.Query.Requests); // Synchronous dispatch, then a deliberately noncooperative query await.
        await f.Change(change);
        reply.TrySetResult(Matches(Match()));
        Assert.Null(await pending.WaitAsync(Bound));
    });

    [Fact] public void Ignored_query_cancellation_returns_no_authority_and_cannot_revive_old_results() => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        using var stop = new CancellationTokenSource();
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Inverse(stop.Token));
        var selection = Assert.IsType<SyncTeXNavigationSelection>(await f.Policy.PrepareSourceAsync(result, 0));
        stop.Cancel();
        Assert.False(f.Policy.IsCurrent(result)); Assert.False(f.Policy.IsCurrent(selection));
        Assert.Null(await f.Policy.PrepareSourceAsync(result, 0));
        int count = f.Query.Requests.Count;
        Assert.Null(await f.Inverse(stop.Token)); Assert.Equal(count, f.Query.Requests.Count);

        using var secondStop = new CancellationTokenSource();
        var reply = new TaskCompletionSource<SyncTeXQueryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Query.Reply = _ => reply.Task;
        var pending = f.Inverse(secondStop.Token); secondStop.Cancel();
        // No need to release the noncooperative query to observe cancellation.
        Assert.Null(await pending.WaitAsync(Bound));
        reply.TrySetResult(Matches(Match()));
        Assert.False(f.Policy.IsCurrent(result));
    });

    [Theory]
    [InlineData("edit")][InlineData("ABA")][InlineData("save")][InlineData("selection-ABA")]
    [InlineData("samehash-compile")][InlineData("takeover")][InlineData("dispose-policy")][InlineData("dispose-session")]
    public void Real_storage_read_boundary_rechecks_all_authority_after_await(string change) => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Inverse());
        var gate = f.Storage.Hold();
        var pending = f.Policy.PrepareSourceAsync(result, 0);
        await gate.WaitReached();
        await f.Change(change);
        gate.Release.TrySetResult(f.Storage.Repo.Content[gate.Hash]);
        Assert.Null(await pending.WaitAsync(Bound));
    });

    [Fact] public void Preparation_cancellation_is_retained_and_noncooperative_storage_cannot_publish_late_selection() => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Inverse());
        using var stop = new CancellationTokenSource();
        var selection = Assert.IsType<SyncTeXNavigationSelection>(await f.Policy.PrepareSourceAsync(result, 0, stop.Token));
        stop.Cancel();
        Assert.False(f.Policy.IsCurrent(selection)); Assert.True(f.Policy.IsCurrent(result));
        using var second = new CancellationTokenSource();
        var gate = f.Storage.Hold(); var preparing = f.Policy.PrepareSourceAsync(result, 0, second.Token);
        await gate.WaitReached(); second.Cancel();
        Assert.Null(await preparing.WaitAsync(Bound));
        gate.Release.TrySetResult(f.Storage.Repo.Content[gate.Hash]);
        Assert.Null(await f.Policy.PrepareSourceAsync(result, 0, second.Token));
    });

    [Theory]
    [InlineData("same-line")][InlineData("invalid-utf8")][InlineData("no-bom")]
    public void Actual_complete_bytes_hash_BOM_and_source_are_required_not_durable_hash_allegations(string corruption) => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Inverse());
        var file = result.Matches[0].File;
        f.Storage.Repo.ReadOverride = _ => corruption switch
        {
            "same-line" => [239, 187, 191, .. Encoding.UTF8.GetBytes("Україна🙂\r\nDIFFERENT unrelated line\n")],
            "invalid-utf8" => [239, 187, 191, 255],
            _ => file.ToArray()[3..]
        };
        Assert.True(f.Policy.IsCurrent(result)); // Identity is necessary, not byte materialization.
        Assert.Null(await f.Policy.PrepareSourceAsync(result, 0));
    });

    [Theory][InlineData("content")][InlineData("dirty")][InlineData("isnew")][InlineData("hash")][InlineData("bom")]
    public void Final_synchronous_guard_detects_unannounced_working_file_mutation(string mutation) => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Inverse());
        var selection = Assert.IsType<SyncTeXNavigationSelection>(await f.Policy.PrepareSourceAsync(result, 0));
        var file = f.Workspace.Files.Single(x => x.Id == selection.File.FileId);
        switch (mutation)
        {
            case "content": file.Content += "unrelated tail"; break;
            case "dirty": file.DirtyContent = !file.DirtyContent; break;
            case "isnew": file.IsNew = !file.IsNew; break;
            case "hash": file.DurableHash = new(new string('0', 64)); break;
            case "bom": file.HasBom = !file.HasBom; break;
        }
        Assert.False(f.Policy.IsCurrent(selection));
    });

    [Fact] public void Mutable_import_array_preimage_is_rechecked_after_preparation() => Run(async () =>
    {
        await using var f = await Open();
        Assert.True(f.Workspace.CreateFileFromBytes("imported.tex", Encoding.UTF8.GetBytes("same line\r\nexact tail")));
        var file = f.Workspace.Files.Single(x => x.RelativePath == "imported.tex");
        await f.Compile(); f.Query.Reply = _ => Task.FromResult(Matches(Match("imported.tex")));
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Inverse());
        var selection = Assert.IsType<SyncTeXNavigationSelection>(await f.Policy.PrepareSourceAsync(result, 0));
        Assert.True(f.Policy.IsCurrent(selection));
        Assert.NotNull(file.ImportedBytes); file.ImportedBytes[^1] ^= 1;
        Assert.False(f.Policy.IsCurrent(selection));
    });

    [Fact] public void Cancelled_waits_allow_late_borrowed_faults_without_reauthorizing_or_poisoning_next_action() => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        using var queryStop = new CancellationTokenSource();
        var reply = new TaskCompletionSource<SyncTeXQueryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Query.Reply = _ => reply.Task;
        var pending = f.Policy.ForwardAsync(f.Main, 0, queryStop.Token);
        queryStop.Cancel(); Assert.Null(await pending.WaitAsync(Bound));
        reply.SetException(new IOException("late noncooperative query fault"));
        // Do not read reply.Task.Exception here: production must observe the
        // actual task even though its WaitAsync wrapper has already cancelled.
        f.Query.Reply = _ => Task.FromResult(Matches(Match()));
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Inverse());
        using var storageStop = new CancellationTokenSource();
        var gate = f.Storage.Hold(); var preparing = f.Policy.PrepareSourceAsync(result, 0, storageStop.Token);
        await gate.WaitReached(); storageStop.Cancel(); Assert.Null(await preparing.WaitAsync(Bound));
        gate.Release.SetException(new IOException("late noncooperative storage fault"));
        var selected = Assert.IsType<SyncTeXNavigationSelection>(await f.Policy.PrepareSourceAsync(result, 0));
        Assert.True(f.Policy.IsCurrent(selected)); Assert.True(f.Policy.IsCurrent(result));
    });

    [Fact] public void Query_exception_empty_or_oversized_response_is_unavailable() => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        f.Query.Reply = _ => throw new IOException("untrusted query details");
        Assert.Null(await f.Inverse());
        f.Query.Reply = _ => Task.FromResult(Matches()); Assert.Null(await f.Inverse());
        f.Query.Reply = _ => Task.FromResult(Matches(Enumerable.Repeat(Match(), 33).ToArray()));
        Assert.Null(await f.Inverse());
    });
}
