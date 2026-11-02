using Client.Pages;
using Client.Review;
using Client.Workspace.Domain;
using Client.Workspace.History;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Workspace.Tests.HistoryComparison;

public sealed class LifetimeAndPanelTests
{
    [Theory]
    [InlineData("readContent")]
    [InlineData("ownsLock")]
    [InlineData("getProject")]
    public async Task Cancelled_review_acceptance_retains_admission_through_actual_reads(string operation)
    {
        await using var h = await HistoryTestHost.Open(); var proposal = await h.Proposal(h.Main);
        var preview = await proposal.PreviewAsync(h.Review, default); var before = (await h.Bytes(h.Main)).ToArray();
        var hold = h.Io.HoldNext(operation); using var cts = new CancellationTokenSource();
        var accepting = h.Review.AcceptAsync(preview.Review!.Preview!.Id, cts.Token); await hold.Wait();
        cts.Cancel(); Assert.True(h.Review.State.Busy); Assert.False(accepting.IsCompleted);
        Assert.Equal(ReviewCode.Busy, (await h.Review.AcceptAsync(preview.Review.Preview.Id, default)).Failure!.Code);
        hold.Release.TrySetResult(); var result = await accepting.WaitAsync(HistoryTestHost.Bound);
        Assert.False(result.Changed); Assert.False(h.Review.State.Busy); Assert.Equal(before, await h.Bytes(h.Main));
        Assert.Equal(h.OtherBytes, await h.Bytes(h.Other)); Assert.Equal(h.BinaryBytes, await h.Bytes(h.Binary));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("direct-model")]
    [InlineData("head")]
    public async Task Proposal_reads_reject_changed_full_preimages_not_just_equal_revision_ids(string mode)
    {
        await using var h = await HistoryTestHost.Open(); await h.Compare();
        var hold = h.Io.HoldNext("readContent"); var creating = HistoryTextRestoreProposal.CreateAsync(h.Session, [h.Main], default);
        await hold.Wait();
        if (mode == "manifest") h.Io.Revisions[h.Old.ToString()]["CreatedAt"] = DateTimeOffset.UtcNow.AddDays(1).ToString("O");
        if (mode == "direct-model") h.File(h.Other).Content = "unnotified change";
        if (mode == "head") h.Io.Projects[h.Workspace.Project!.Id.ToString()]["HeadRevisionId"] = h.Old.ToString();
        hold.Release.TrySetResult(); var result = await creating.WaitAsync(HistoryTestHost.Bound);
        Assert.Equal(HistoryComparisonCode.Stale, result.Code); Assert.Null(result.Proposal); Assert.Null(h.Review.State.Preview);
    }

    [Fact]
    public async Task Historical_manifest_replacement_between_comparison_and_selection_is_not_rebased()
    {
        await using var h = await HistoryTestHost.Open(); await h.Compare();
        h.Io.Revisions[h.Old.ToString()]["CreatedAt"] = DateTimeOffset.UtcNow.AddDays(1).ToString("O");
        var result = await HistoryTextRestoreProposal.CreateAsync(h.Session, [h.Main], default);
        Assert.Equal(HistoryComparisonCode.Stale, result.Code); Assert.Null(result.Proposal);
    }

    [Fact]
    public async Task Historical_manifest_replacement_during_saved_pair_read_withholds_result()
    {
        await using var h = await HistoryTestHost.Open(); var hold = h.Io.HoldNext("readContent");
        var pending = h.Session.CompareSavedAsync(h.Old, h.Current, default); await hold.Wait();
        h.Io.Revisions[h.Old.ToString()]["CreatedAt"] = DateTimeOffset.UtcNow.AddDays(1).ToString("O");
        hold.Release.TrySetResult(); var result = await pending.WaitAsync(HistoryTestHost.Bound);
        Assert.Equal(HistoryComparisonCode.Stale, result.Code); Assert.Null(result.Comparison);
    }

    [Fact]
    public async Task Dispose_invalidates_but_does_not_retire_started_repository_work_early()
    {
        await using var h = await HistoryTestHost.Open(); var hold = h.Io.HoldNext("readContent");
        var pending = h.Session.CompareWorkingAsync(h.Old, default); await hold.Wait(); h.Session.Dispose();
        Assert.True(h.Session.Busy); Assert.False(pending.IsCompleted);
        Assert.Equal(HistoryComparisonCode.Disposed, (await h.Session.CompareWorkingAsync(h.Old, default)).Code);
        hold.Release.TrySetResult(); Assert.Equal(HistoryComparisonCode.Disposed, (await pending.WaitAsync(HistoryTestHost.Bound)).Code);
        Assert.False(h.Session.Busy); Assert.Null(h.Session.Comparison);
    }

    [Fact]
    public async Task Readonly_panel_discloses_direction_budgets_and_escapes_labels_without_picking_a_diff()
    {
        await using var h = await HistoryTestHost.Open(readOnly: true); await h.Compare();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<HistoryComparisonPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Session"] = h.Session, ["Review"] = h.Review, ["Writable"] = false,
                ["Revisions"] = new[] { new HistoryCheckpoint(CheckpointId.New(), h.Workspace.Project!.Id, h.Old,
                    "<img src=x onerror=alert(1)>", DateTimeOffset.UtcNow) }
            }));
            return rendered.ToHtmlString();
        });
        Assert.Contains("history-direction", html); Assert.Contains("working copy", html);
        Assert.Contains("&lt;img", html); Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("summary-only", html); Assert.Contains("No arbitrary file is selected automatically", html);
        Assert.DoesNotContain("data-testid=\"history-detailed-diff\"", html);
        Assert.Contains("historical PDFs do not grant live analysis", html);
    }
}
