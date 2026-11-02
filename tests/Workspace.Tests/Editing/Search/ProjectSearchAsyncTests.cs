using Client.Editing.Search;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Patching;
using Xunit;

namespace Workspace.Tests.Editing.Search;

public sealed class ProjectSearchAsyncTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);
    public static IEnumerable<object[]> ParityCases()
    {
        yield return ["Україна🙂 e\u0301\r\na\rb\nc🙂", "🙂", true];
        yield return ["УКРАЇНА україна", "україна", false];
        yield return ["aaa aaaa", "aa", true];
        yield return ["a\r\nb\rc\nd🙂", "\r", true];
        yield return ["a\r\nb\rc\nd🙂", "\n", true];
        yield return ["a\r\nb\rc\nd🙂", "a\r\nb", true];
        yield return [".*[x] \\ \t🙂", ".*[x]", false];
    }
    [Theory, MemberData(nameof(ParityCases))]
    public async Task Async_and_sync_use_the_same_literal_scalar_CRLF_and_location_semantics(string source, string query, bool sensitive)
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, source);
        using var sync = new ProjectSearchSession(f.Workspace);
        using var asyncSearch = new ProjectSearchSession(f.Workspace, yieldAsync: _ => Task.CompletedTask);
        string before = f.State(); int reads = f.Repo.Reads, writes = f.Repo.Commits, locks = f.Lock.Queries;
        var a = sync.Search(query, sensitive); var b = await asyncSearch.SearchAsync(query, sensitive);
        Assert.Equal(a.Status, b.Status); Assert.Equal(a.TextFiles, b.TextFiles); Assert.Equal(a.BinaryFiles, b.BinaryFiles);
        Assert.Equal(a.Matches.Select(Shape), b.Matches.Select(Shape));
        Assert.Equal(before, f.State()); Assert.Equal(reads, f.Repo.Reads); Assert.Equal(writes, f.Repo.Commits); Assert.Equal(locks, f.Lock.Queries);
        Assert.All(b.Matches, m => Assert.True(asyncSearch.TryNavigate(m)!.IsCurrent));
    }

    [Fact]
    public async Task Matching_lookahead_covers_both_micro_windows_and_every_large_slice_boundary_without_duplicates()
    {
        await using var f = await CompilationTestWorkspace.Open();
        using var search = new ProjectSearchSession(f.Workspace, yieldAsync: _ => Task.CompletedTask);
        foreach (var query in new[] { "aba", "🙂x", "a\r\nb" })
        foreach (int boundary in new[] { 512, 1024, 65536, 131072 })
        for (int delta = 1 - query.Length; delta <= 1; delta++)
        {
            int at = boundary + delta; f.Workspace.EditText(f.Main, new string('z', at) + query + "zz");
            var report = await search.SearchAsync(query); var match = Assert.Single(report.Matches);
            Assert.Equal(ProjectSearchStatus.Complete, report.Status); Assert.Equal((at, at + query.Length), (match.Start, match.End));
        }
    }

    [Fact]
    public async Task Exact_caps_are_complete_only_when_no_additional_valid_match_exists()
    {
        await using var f = await CompilationTestWorkspace.Open();
        using var a = new ProjectSearchSession(f.Workspace, new(ResultsPerFile: 2, TotalResults: 3), _ => Task.CompletedTask);
        f.Workspace.EditText(f.Main, "xx"); f.Workspace.CreateFile("A.tex", "x");
        Assert.Equal(ProjectSearchStatus.Complete, (await a.SearchAsync("x")).Status);
        f.Workspace.EditText(f.Main, "xxx");
        var truncated = await a.SearchAsync("x"); Assert.Equal(ProjectSearchStatus.Truncated, truncated.Status); Assert.Equal(3, truncated.Matches.Length);
        using var sync = new ProjectSearchSession(f.Workspace, new(ResultsPerFile: 2, TotalResults: 3));
        Assert.Equal(sync.Search("x").Matches.Select(Shape), truncated.Matches.Select(Shape));
    }

    [Fact]
    public async Task Entire_input_is_preflighted_in_slices_including_malformed_tail_after_first_matching_file()
    {
        await using var f = await CompilationTestWorkspace.Open();
        f.Workspace.CreateFile("000.tex", "find"); f.Workspace.CreateFile("zzz.tex", "valid initial text");
        f.Workspace.EditText(f.Workspace.ActiveFileId!.Value, new string('x', 1024 * 1024) + '\ud800');
        var gate = new SliceGate(); using var search = new ProjectSearchSession(f.Workspace, yieldAsync: gate.Yield);
        var pending = search.SearchAsync("find"); await gate.Reached.Task.WaitAsync(Bound);
        Assert.True(search.Busy); Assert.Equal(ProjectSearchStatus.Searching, search.Report.Status); Assert.Empty(search.Report.Matches);
        gate.Release.TrySetResult(); var report = await pending.WaitAsync(Bound);
        Assert.Equal(ProjectSearchStatus.InvalidInput, report.Status); Assert.Empty(report.Matches);
        Assert.True(gate.Calls > 1); Assert.Equal(0, f.Repo.Reads);
    }

    [Fact]
    public async Task Maximum_64MiB_text_and_1024_unit_near_miss_complete_with_bounded_scheduler_slices()
    {
        await using var f = await CompilationTestWorkspace.Open();
        f.Workspace.Files.Clear(); string source = new('a', (int)WorkspaceTextLimits.FileBytes);
        for (int i = 0; i < 8; i++) f.Workspace.Files.Add(NewFile($"{i}.tex", source));
        int turns = 0; using var search = new ProjectSearchSession(f.Workspace, yieldAsync: _ => { turns++; return Task.CompletedTask; });
        var result = await search.SearchAsync(new string('a', 1023) + "b").WaitAsync(Bound);
        Assert.Equal(ProjectSearchStatus.Complete, result.Status); Assert.Empty(result.Matches); Assert.Equal(8, result.TextFiles);
        Assert.True(turns >= 1024); Assert.Equal(0, f.Repo.Reads); Assert.Equal(0, f.Repo.Commits);
        // Shared immutable fixture strings reduce test RAM, not scanned bytes:
        // the real algorithm validates/searches all eight8MiB files.
    }

    [Fact]
    public async Task Five_thousand_file_metadata_preflight_yields_and_cancel_retains_actual_admission()
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.Files.Clear();
        for (int i = 0; i < WorkspaceTextLimits.FileCount; i++) f.Workspace.Files.Add(NewFile($"{i:D4}.tex", ""));
        var gate = new SliceGate(); using var search = new ProjectSearchSession(f.Workspace, yieldAsync: gate.Yield);
        var pending = search.SearchAsync("find"); await gate.Reached.Task.WaitAsync(Bound);
        search.Cancel(); Assert.True(search.Busy); Assert.False(pending.IsCompleted); Assert.Empty(search.Report.Matches);
        Assert.Equal(ProjectSearchStatus.Busy, (await search.SearchAsync("new request")).Status);
        Assert.True(search.Busy); Assert.False(pending.IsCompleted);
        gate.Release.TrySetResult(); await pending.WaitAsync(Bound);
        Assert.False(search.Busy); Assert.NotEqual(ProjectSearchStatus.Complete, search.Report.Status); Assert.Empty(search.Report.Matches);
        Assert.Equal(ProjectSearchStatus.Complete, (await search.SearchAsync("find")).Status);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("aba")]
    [InlineData("close")]
    [InlineData("reopen")]
    [InlineData("project")]
    [InlineData("dispose")]
    [InlineData("direct")]
    public async Task Held_async_work_cannot_publish_after_workspace_or_lifetime_authority_changes(string change)
    {
        await using var f = await CompilationTestWorkspace.Open(); f.Workspace.EditText(f.Main, new string('x', 150000) + "find");
        var gate = new SliceGate(); using var search = new ProjectSearchSession(f.Workspace, yieldAsync: gate.Yield);
        var pending = search.SearchAsync("find"); await gate.Reached.Task.WaitAsync(Bound); var main = f.Workspace.Files.Single(w => w.Id == f.Main);
        switch (change)
        {
            case "edit": f.Workspace.EditText(f.Main, "later"); break;
            case "aba": string old = main.Content; f.Workspace.EditText(f.Main, "away"); f.Workspace.EditText(f.Main, old); break;
            case "close": await f.Workspace.CloseAsync(default); break;
            case "reopen": await f.Workspace.OpenAsync(f.Repo.Project.Id, default); break;
            case "project": var p = new Project(ProjectId.New(), "other", null); f.Repo.Projects[p.Id] = (p, null); await f.Workspace.OpenAsync(p.Id, default); break;
            case "dispose": search.Dispose(); break;
            case "direct": main.Content = "unnotified"; break;
        }
        Assert.True(search.Busy); Assert.False(pending.IsCompleted); gate.Release.TrySetResult();
        var result = await pending.WaitAsync(Bound); Assert.Empty(result.Matches); Assert.False(search.Busy);
        Assert.Contains(result.Status, new[] { ProjectSearchStatus.Stale, ProjectSearchStatus.Disposed });
    }

    [Fact]
    public async Task Async_read_only_search_and_BOM_empty_file_limits_remain_strict()
    {
        await using var f = await CompilationTestWorkspace.Open(readOnly: true);
        using var search = new ProjectSearchSession(f.Workspace); string before = f.State();
        var report = await search.SearchAsync("🙂"); Assert.Single(report.Matches); Assert.Equal(before, f.State()); Assert.Equal(0, f.Lock.Queries);
        f.Workspace.Files.Clear(); var file = NewFile("bom.tex", ""); file.HasBom = true; f.Workspace.Files.Add(file);
        using var small = new ProjectSearchSession(f.Workspace, new(FileBytes: 2));
        Assert.Equal(ProjectSearchStatus.InputLimit, (await small.SearchAsync("x")).Status);
    }
    private static object Shape(ProjectSearchMatch m) => (m.RelativePath, m.Start, m.End, m.Line, m.Column, m.Snippet);
    private static WorkingFile NewFile(string path, string content) => new(FileId.New(), path, "text", content, null, false,
        true, false, false, true, false, null, 0, null);
    private sealed class SliceGate
    {
        internal int Calls;
        internal readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task Yield(CancellationToken ct)
        { _ = ct; Calls++; if (Calls != 1) return; Reached.TrySetResult(); await Release.Task; } // deliberately finite/noncooperative
    }
}
