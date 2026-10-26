using Client.Editing;
using Client.Editing.Search;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Patching;
using Xunit;

namespace Workspace.Tests.Editing.Search;

public sealed class ProjectSearchSessionTests
{
    [Fact]
    public async Task Searches_real_working_files_in_ordinal_path_and_source_order_without_side_effects()
    {
        await using var f = await CompilationTestWorkspace.Open();
        Assert.True(f.Workspace.CreateFile("z.tex", "find find"));
        Assert.True(f.Workspace.CreateFile("A.tex", "find"));
        Assert.True(f.Workspace.CreateFile("nested/a.tex", "find"));
        using var search = new ProjectSearchSession(f.Workspace);
        string state = f.State();
        int reads = f.Repo.Reads, commits = f.Repo.Commits, queries = f.Lock.Queries;
        var report = search.Search("find");
        Assert.Equal(ProjectSearchStatus.Complete, report.Status);
        Assert.Equal(new[] { "A.tex", "nested/a.tex", "z.tex", "z.tex" }, report.Matches.Select(m => m.RelativePath));
        Assert.Equal(new[] { 0, 0, 0, 5 }, report.Matches.Select(m => m.Start));
        Assert.Equal(6, report.TextFiles);
        Assert.Equal(1, report.BinaryFiles);
        Assert.Equal(state, f.State());
        Assert.Equal(reads, f.Repo.Reads);
        Assert.Equal(commits, f.Repo.Commits);
        Assert.Equal(queries, f.Lock.Queries);
    }

    [Fact]
    public async Task Binary_and_deleted_files_are_not_searched_even_if_their_content_contains_query()
    {
        await using var f = await CompilationTestWorkspace.Open();
        // Defensive test against public WorkingFile fields: a binary's incidental
        // Content must never become search input, even malformed UTF-16.
        var binary = f.Workspace.Files.Single(x => x.MediaKind == "binary");
        binary.Content = "chapter" + new string((char)0xD800, 1);
        var chapter = f.Workspace.Files.Single(x => x.RelativePath == "chapter.tex");
        Assert.True(f.Workspace.DeleteFile(chapter.Id));
        using var search = new ProjectSearchSession(f.Workspace);
        var report = search.Search("chapter");
        Assert.Equal(ProjectSearchStatus.Complete, report.Status);
        Assert.Empty(report.Matches);
        Assert.Equal(1, report.BinaryFiles);
    }

    [Fact]
    public async Task Uses_actual_crlf_bom_free_source_and_one_based_utf16_columns()
    {
        await using var f = await CompilationTestWorkspace.Open();
        using var search = new ProjectSearchSession(f.Workspace);
        var main = f.Workspace.Files.Single(x => x.Id == f.Main);
        Assert.True(main.HasBom);
        Assert.Equal("Україна🙂\r\nmain\rlast\n", main.Content);
        var emoji = Assert.Single(search.Search("🙂").Matches);
        Assert.Equal((7, 9, 1, 8), (emoji.Start, emoji.End, emoji.Line, emoji.Column));
        Assert.Contains("Україна🙂\\r\\nmain\\rlast\\n", emoji.Snippet);
        Assert.DoesNotContain('\r', emoji.Snippet);
        Assert.DoesNotContain('\n', emoji.Snippet);
        var mainMatch = Assert.Single(search.Search("main").Matches);
        Assert.Equal((11, 15, 2, 1), (mainMatch.Start, mainMatch.End, mainMatch.Line, mainMatch.Column));
        var last = Assert.Single(search.Search("last").Matches);
        Assert.Equal((16, 20, 3, 1), (last.Start, last.End, last.Line, last.Column));
        Assert.Empty(search.Search("\uFEFF").Matches);
        Assert.True(f.Workspace.EditText(f.Main, "🙂x\r\nУкраїна🙂x"));
        var xs = search.Search("x").Matches;
        Assert.Equal(new[] { (2, 1, 3), (14, 2, 10) }, xs.Select(m => (m.Start, m.Line, m.Column)));
    }

    [Fact]
    public async Task Never_matches_part_of_crlf_or_surrogate_and_can_match_complete_multiline_literals()
    {
        await using var f = await CompilationTestWorkspace.Open();
        Assert.True(f.Workspace.EditText(f.Main, "a\r\nb\rc\nd🙂"));
        using var search = new ProjectSearchSession(f.Workspace);
        var cr = Assert.Single(search.Search("\r").Matches);
        Assert.Equal(4, cr.Start);
        // Other fixture text files also contain CRLF; those are not partial matches.
        var lf = Assert.Single(search.Search("\n").Matches);
        Assert.Equal(6, lf.Start);
        var full = Assert.Single(search.Search("a\r\nb").Matches);
        Assert.Equal((0, 4), (full.Start, full.End));
        Assert.Empty(search.Search("a\r").Matches);
        Assert.Empty(search.Search("\nb").Matches);
        var high = search.Search(new string((char)0xD83D, 1));
        Assert.Equal(ProjectSearchStatus.InvalidInput, high.Status);
        Assert.Empty(high.Matches);
        Assert.Equal(ProjectSearchStatus.InvalidInput, search.Search(new string((char)0xDE42, 1)).Status);
        Assert.Single(search.Search("🙂").Matches);
    }

    [Fact]
    public async Task Literal_ordinal_case_switch_and_no_cross_file_spans()
    {
        await using var f = await CompilationTestWorkspace.Open();
        Assert.True(f.Workspace.EditText(f.Main, "УКРАЇНА .*[x] aaaa tail"));
        Assert.True(f.Workspace.EditText(f.Workspace.Files.Single(x => x.RelativePath == "chapter.tex").Id, "head"));
        using var search = new ProjectSearchSession(f.Workspace);
        Assert.Empty(search.Search("україна", true).Matches);
        Assert.Single(search.Search("україна", false).Matches);
        Assert.Single(search.Search(".*[x]").Matches);
        Assert.Empty(search.Search("tailhead").Matches);
        Assert.Equal(2, search.Search("aa").Matches.Length); // nonoverlapping listing, no wrap
    }

    [Fact]
    public async Task Malformed_source_is_rejected_as_a_whole_not_normalized_or_partially_searched()
    {
        await using var f = await CompilationTestWorkspace.Open();
        using var search = new ProjectSearchSession(f.Workspace);
        Assert.Single(search.Search("chapter").Matches);
        foreach (var bad in new[] { "prefix" + new string((char)0xD800, 1), new string((char)0xDC00, 1) + "suffix" })
        {
            Assert.True(f.Workspace.EditText(f.Main, bad));
            var report = search.Search("chapter");
            Assert.Equal(ProjectSearchStatus.InvalidInput, report.Status);
            Assert.Empty(report.Matches);
        }
    }

    [Fact]
    public async Task Empty_query_query_budget_and_no_project_are_explicit_and_clear_previous_results()
    {
        await using var f = await CompilationTestWorkspace.Open();
        using var search = new ProjectSearchSession(f.Workspace, new(QueryUnits: 4));
        Assert.Single(search.Search("main").Matches);
        Assert.Equal(ProjectSearchStatus.EmptyQuery, search.Search("").Status);
        Assert.Empty(search.Report.Matches);
        Assert.Equal(ProjectSearchStatus.EmptyQuery, search.Search(null).Status);
        Assert.Equal(ProjectSearchStatus.InputLimit, search.Search("12345").Status);
        Assert.Equal(ProjectSearchStatus.Complete, search.Search(" ").Status); // whitespace is literal
        await f.Workspace.CloseAsync(CancellationToken.None);
        Assert.Equal(ProjectSearchStatus.NoProject, search.Search("main").Status);
    }

    [Fact]
    public async Task Byte_limits_include_utf8_bom_and_do_not_silently_search_source_prefixes()
    {
        await using var f = await CompilationTestWorkspace.Open();
        var main = f.Workspace.Files.Single(x => x.Id == f.Main);
        // Measure actual UTF-8 source plus its separately held BOM metadata.
        long mainBytes = System.Text.Encoding.UTF8.GetByteCount(main.Content) + 3;
        using var exact = new ProjectSearchSession(f.Workspace, new(FileBytes: mainBytes, TotalBytes: mainBytes + 9));
        Assert.Equal(ProjectSearchStatus.Complete, exact.Search("main").Status);
        using var tooSmall = new ProjectSearchSession(f.Workspace, new(FileBytes: mainBytes - 1));
        Assert.Equal(ProjectSearchStatus.InputLimit, tooSmall.Search("chapter").Status);
        Assert.Empty(tooSmall.Report.Matches);
        using var total = new ProjectSearchSession(f.Workspace, new(TotalBytes: mainBytes + 8));
        Assert.Equal(ProjectSearchStatus.InputLimit, total.Search("chapter").Status);
        Assert.True(f.Workspace.EditText(f.Main, new string('x', 100)));
        Assert.Equal(ProjectSearchStatus.InputLimit, exact.Search("x").Status);
        Assert.Empty(exact.Report.Matches);
    }

    [Fact]
    public async Task File_count_and_invalid_budget_limits_are_enforced()
    {
        await using var f = await CompilationTestWorkspace.Open();
        using var search = new ProjectSearchSession(f.Workspace, new(FileCount: 3));
        Assert.Equal(ProjectSearchStatus.InputLimit, search.Search("main").Status); // includes binary file in count
        Assert.Empty(search.Report.Matches);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectSearchSession(f.Workspace, new(FileBytes: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectSearchSession(f.Workspace, new(TotalBytes: WorkspaceTextLimits.ProjectBytes + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectSearchSession(f.Workspace, new(TotalResults: 501)));
    }

    [Fact]
    public async Task Per_file_limit_is_explicit_but_other_files_still_searched_and_exact_limit_is_complete()
    {
        await using var f = await CompilationTestWorkspace.Open();
        Assert.True(f.Workspace.EditText(f.Main, "xx"));
        Assert.True(f.Workspace.CreateFile("a.tex", "x"));
        using var search = new ProjectSearchSession(f.Workspace, new(ResultsPerFile: 1));
        var report = search.Search("x");
        Assert.Equal(ProjectSearchStatus.Truncated, report.Status);
        Assert.Equal(new[] { "a.tex", "entry" }, report.Matches.Select(m => m.RelativePath));
        Assert.True(f.Workspace.EditText(f.Main, "x"));
        Assert.Equal(ProjectSearchStatus.Complete, search.Search("x").Status);
    }

    [Fact]
    public async Task Total_limit_and_bounded_plain_snippets_are_explicit()
    {
        await using var f = await CompilationTestWorkspace.Open();
        Assert.True(f.Workspace.EditText(f.Main, "<script>\\\t\0" + string.Concat(Enumerable.Repeat("🙂x\r\n", 200))));
        using var search = new ProjectSearchSession(f.Workspace, new(TotalResults: 2));
        var report = search.Search("x");
        Assert.Equal(ProjectSearchStatus.Truncated, report.Status);
        Assert.Equal(2, report.Matches.Length);
        Assert.All(report.Matches, m =>
        {
            Assert.InRange(m.Snippet.Length, 1, 160);
            Assert.Contains("🙂", m.Snippet);
            Assert.DoesNotContain('\r', m.Snippet);
            Assert.DoesNotContain('\n', m.Snippet);
            Assert.DoesNotContain('\0', m.Snippet);
            Assert.False(char.IsHighSurrogate(m.Snippet[^1]));
        });
        Assert.Contains("<script>", report.Matches[0].Snippet); // plain text, escaped by Razor, not HTML manufactured here
        Assert.Contains("\\u0000", report.Matches[0].Snippet);
        Assert.True(f.Workspace.EditText(f.Main, "xx"));
        Assert.Equal(ProjectSearchStatus.Complete, search.Search("x").Status);
    }

    [Fact]
    public async Task Default_result_limits_bound_large_match_lists()
    {
        await using var f = await CompilationTestWorkspace.Open();
        for (int i = 0; i < 6; i++) Assert.True(f.Workspace.CreateFile($"many{i}.tex", new string('x', 101)));
        using var search = new ProjectSearchSession(f.Workspace);
        var report = search.Search("x");
        Assert.Equal(ProjectSearchStatus.Truncated, report.Status);
        Assert.Equal(500, report.Matches.Length);
        Assert.All(report.Matches.GroupBy(m => m.FileId), group => Assert.Equal(100, group.Count()));
    }

    [Fact]
    public async Task Exact_current_navigation_is_owned_and_does_not_change_active_file_dirty_or_generation()
    {
        await using var f = await CompilationTestWorkspace.Open();
        using var search = new ProjectSearchSession(f.Workspace);
        var match = Assert.Single(search.Search("chapter").Matches);
        var before = f.State();
        var request = Assert.IsType<ProjectSearchNavigationRequest>(search.TryNavigate(match));
        Assert.True(request.IsCurrent);
        Assert.True(search.IsCurrent(request));
        Assert.Equal(f.Workspace.SessionId, request.SessionId);
        Assert.Equal(f.Workspace.Project!.Id, request.ProjectId);
        Assert.Equal(f.Workspace.WorkingGeneration, request.WorkingGeneration);
        Assert.Equal((match.FileId, 0, 7, 1, 1), (request.FileId, request.Start, request.End, request.Line, request.Column));
        Assert.Equal(before, f.State());
        using var other = new ProjectSearchSession(f.Workspace);
        Assert.False(other.IsCurrent(request));
        Assert.Null(other.TryNavigate(match));
        Assert.Null(search.TryNavigate(null));
        search.Search("main");
        Assert.False(request.IsCurrent);
        Assert.Null(search.TryNavigate(match));
    }

    [Fact]
    public async Task Any_mutation_including_other_file_edit_and_ABA_invalidates_immediately()
    {
        await using var f = await CompilationTestWorkspace.Open();
        using var search = new ProjectSearchSession(f.Workspace);
        var match = Assert.Single(search.Search("chapter").Matches);
        var request = Assert.IsType<ProjectSearchNavigationRequest>(search.TryNavigate(match));
        var before = f.Workspace.ActiveFile!.Content;
        Assert.True(f.Workspace.EditText(f.Main, "different"));
        Assert.Equal(ProjectSearchStatus.Stale, search.Report.Status);
        Assert.Empty(search.Report.Matches);
        Assert.False(request.IsCurrent);
        Assert.True(f.Workspace.EditText(f.Main, before));
        Assert.Null(search.TryNavigate(match));
        Assert.False(request.IsCurrent);
        Assert.Single(search.Search("chapter").Matches);
    }

    [Fact]
    public async Task Tree_changes_and_binary_changes_invalidate_results()
    {
        await using var f = await CompilationTestWorkspace.Open();
        using var search = new ProjectSearchSession(f.Workspace);
        void Check(Action mutation)
        {
            var match = Assert.Single(search.Search("main").Matches);
            var request = Assert.IsType<ProjectSearchNavigationRequest>(search.TryNavigate(match));
            mutation();
            Assert.Empty(search.Report.Matches);
            Assert.False(request.IsCurrent);
        }
        Check(() => Assert.True(f.Workspace.CreateFile("new.tex", "other")));
        var other = f.Workspace.Files.Single(x => x.RelativePath == "new.tex");
        Check(() => Assert.True(f.Workspace.RenameFile(other.Id, "renamed.tex")));
        Check(() => Assert.True(f.Workspace.DeleteFile(other.Id)));
        Check(() => Assert.True(f.Workspace.ReplaceFileBytes(f.Workspace.Files.Single(x => x.MediaKind == "binary").Id, [1, 2])));
    }

    [Fact]
    public async Task Direct_preimage_path_bom_and_file_object_tampering_cannot_yield_navigation()
    {
        await using var f = await CompilationTestWorkspace.Open();
        using var search = new ProjectSearchSession(f.Workspace);
        var file = f.Workspace.Files.Single(x => x.Id == f.Main);
        var match = Assert.Single(search.Search("main").Matches);
        file.Content += "tamper";
        Assert.Null(search.TryNavigate(match));
        match = Assert.Single(search.Search("main").Matches);
        file.RelativePath = "tampered.tex";
        Assert.Null(search.TryNavigate(match));
        match = Assert.Single(search.Search("main").Matches);
        file.HasBom = !file.HasBom;
        Assert.Null(search.TryNavigate(match));
        match = Assert.Single(search.Search("main").Matches);
        f.Workspace.Files[f.Workspace.Files.IndexOf(file)] = file.Clone();
        Assert.Null(search.TryNavigate(match)); // same ID/range/content is not same working-file identity
    }

    [Fact]
    public async Task Restore_event_invalidates_even_when_session_generation_and_content_are_unchanged()
    {
        await using var f = await CompilationTestWorkspace.Open();
        using var search = new ProjectSearchSession(f.Workspace);
        var request = Assert.IsType<ProjectSearchNavigationRequest>(search.TryNavigate(Assert.Single(search.Search("main").Matches)));
        long generation = f.Workspace.WorkingGeneration;
        Guid session = f.Workspace.SessionId;
        var revision = f.Workspace.CurrentRevision!;
        var historical = revision with { Id = RevisionId.New(), ParentRevisionId = null };
        f.Repo.Revisions[historical.Id] = historical;
        var preview = await f.Workspace.CaptureRestorePreviewAsync(historical.Id, CancellationToken.None);
        var restored = await f.Workspace.AcceptRestoreAsync(preview, CancellationToken.None);
        Assert.Equal(CommitStatus.Saved, restored.Status);
        Assert.Equal(generation, f.Workspace.WorkingGeneration);
        Assert.Equal(session, f.Workspace.SessionId);
        Assert.False(request.IsCurrent);
        Assert.Equal(ProjectSearchStatus.Stale, search.Report.Status);
        Assert.Empty(search.Report.Matches);
    }

    [Fact]
    public async Task Close_reopen_takeover_and_project_switch_reject_old_requests()
    {
        await using var f = await CompilationTestWorkspace.Open();
        using var search = new ProjectSearchSession(f.Workspace);
        ProjectSearchNavigationRequest Request() => Assert.IsType<ProjectSearchNavigationRequest>(
            search.TryNavigate(Assert.Single(search.Search("main").Matches)));
        var request = Request();
        var project = f.Workspace.Project!.Id;
        await f.Workspace.CloseAsync(CancellationToken.None);
        Assert.False(request.IsCurrent);
        await f.Workspace.OpenAsync(project, CancellationToken.None);
        Assert.False(request.IsCurrent);
        request = Request();
        Assert.True(await f.Workspace.TakeOverAsync(CancellationToken.None));
        Assert.False(request.IsCurrent);
        request = Request();
        var second = new Project(ProjectId.New(), "second", null);
        var rev = f.Workspace.CurrentRevision! with { Id = RevisionId.New(), ProjectId = second.Id };
        f.Repo.Projects[second.Id] = (second with { HeadRevisionId = rev.Id }, rev);
        await f.Workspace.OpenAsync(second.Id, CancellationToken.None);
        Assert.False(request.IsCurrent);
        Assert.Empty(search.Report.Matches);
        Assert.True(Request().IsCurrent);
    }

    [Fact]
    public async Task Active_file_navigation_preserves_owned_listing_and_maps_exact_source_with_frozen_editor_api()
    {
        foreach (bool readOnly in new[] { false, true })
        {
            await using var f = await CompilationTestWorkspace.Open(readOnly: readOnly);
            using var search = new ProjectSearchSession(f.Workspace);
            var report = search.Search("🙂\r\nmain");
            var match = Assert.Single(report.Matches);
            var request = Assert.IsType<ProjectSearchNavigationRequest>(search.TryNavigate(match));
            long generation = f.Workspace.WorkingGeneration;
            bool dirty = f.Workspace.IsDirty;
            var other = f.Workspace.Files.Single(x => x.RelativePath == "chapter.tex");
            Assert.True(f.Workspace.SelectFile(other.Id));
            Assert.True(request.IsCurrent);
            Assert.Same(report, search.Report);
            Assert.Same(match, search.Report.Matches[0]);
            Assert.True(f.Workspace.SelectFile(request.FileId));
            Assert.True(request.IsCurrent);
            var source = f.Workspace.ActiveFile!.Content;
            Assert.Equal((7, 15), (request.Start, request.End));
            var dom = Assert.IsType<SourceRange>(SourceTools.ToTextarea(source, new(request.Start, request.End)));
            Assert.Equal(new SourceRange(7, 14), dom);
            Assert.Equal("🙂\nmain", SourceTools.TextareaText(source)[dom.Start..dom.End]);
            Assert.Equal(generation, f.Workspace.WorkingGeneration);
            Assert.Equal(dirty, f.Workspace.IsDirty);
            Assert.Equal(0, f.Repo.Commits);
        }
    }

    [Fact]
    public async Task Read_only_project_can_search_and_disposal_unsubscribes_without_disposing_coordinator()
    {
        await using var f = await CompilationTestWorkspace.Open(readOnly: true);
        var search = new ProjectSearchSession(f.Workspace);
        string state = f.State();
        var request = Assert.IsType<ProjectSearchNavigationRequest>(search.TryNavigate(Assert.Single(search.Search("main").Matches)));
        Assert.True(request.IsCurrent);
        Assert.Equal(state, f.State());
        int notifications = 0;
        search.Changed += () => notifications++;
        search.Dispose();
        Assert.Equal(1, notifications);
        Assert.False(request.IsCurrent);
        Assert.Empty(search.Report.Matches);
        Assert.Equal(ProjectSearchStatus.Disposed, search.Report.Status);
        search.Dispose();
        await f.Workspace.OpenAsync(f.Repo.Project.Id, CancellationToken.None);
        Assert.True(f.Workspace.IsOpen);
        Assert.Equal(1, notifications);
        Assert.Equal(ProjectSearchStatus.Disposed, search.Report.Status); // event handler actually removed
        Assert.Equal(ProjectSearchStatus.Disposed, search.Search("main").Status);
        using var replacement = new ProjectSearchSession(f.Workspace);
        Assert.Single(replacement.Search("main").Matches);
    }
}
