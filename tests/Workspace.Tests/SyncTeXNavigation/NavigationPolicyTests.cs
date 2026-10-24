using System.Text;
using Client.Compilation;
using Client.Compilation.SyncTeX;
using Client.Editing;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;
using static Workspace.Tests.SyncTeXNavigation.NavigationFixture;

namespace Workspace.Tests.SyncTeXNavigation;

public sealed class NavigationPolicyTests
{
    [Fact] public void Actual_nearest_native_line_and_geometry_survive_forward_query_without_edits_or_saves() => Run(async () =>
    {
        await using var f = await Open();
        var output = await f.Compile();
        var native = Match(line: 1, page: 3);
        f.Query.Reply = _ => Task.FromResult(Matches(native));
        var before = f.Storage.State(); int commits = f.Storage.Repo.Commits, workers = f.Config.Workers.Created.Count;
        // Captured source is BOM + Україна🙂 CRLF main CR last LF. Caret is
        // BOM-stripped source UTF-16; 11 is within line 2, not DOM offset 11.
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Policy.ForwardAsync(f.Main, 11));
        var request = Assert.Single(f.Query.Requests);
        Assert.Equal(SyncTeXDirection.Forward, request.Direction);
        Assert.Equal("main.tex", request.File); Assert.Equal(2, request.Line);
        Assert.Equal(Map, request.Map); Assert.Equal(output.SourceMapping!.Hash.Hex, request.MapSha256);
        Assert.Same(output, result.Output); Assert.Same(output.Snapshot, result.Snapshot);
        var target = Assert.Single(result.Matches);
        Assert.Same(native, target.Native); Assert.Equal(1, target.Line); // Not manufactured requested line 2.
        Assert.Equal(91, target.Native.Column); // Raw native column is retained, never used as a token offset.
        Assert.Equal(3, target.Page); Assert.Same(native.Point, target.Point); Assert.Same(native.Box, target.Box);
        Assert.Same(output.Snapshot.Files.Single(x => x.FileId == f.Main), target.File);
        var selected = Assert.IsType<SyncTeXNavigationSelection>(await f.Policy.PrepareSourceAsync(result, 0));
        Assert.Equal("Україна🙂", selected.ExpectedTextareaText[selected.TextareaRange.Start..selected.TextareaRange.End]);
        Assert.Equal(new SourceRange(0, 9), selected.SourceRange);
        Assert.True(f.Policy.IsCurrent(result)); Assert.True(f.Policy.IsCurrent(selected));
        Assert.Equal(before, f.Storage.State()); Assert.Equal(commits, f.Storage.Repo.Commits);
        Assert.Equal(workers, f.Config.Workers.Created.Count);
        request.Map[0] ^= 255;
        Assert.Equal(Map, output.ToSourceMapPayloadArray()); // Query gets a defensive accepted-byte copy.
    });

    [Theory]
    [InlineData("Привіт😀\r\nУкраїна🙂\r\n", 2, 11, "Україна🙂", 9, 18)]
    [InlineData("first\r\n\r\nlast", 2, 7, "", 6, 6)]
    [InlineData("a\rb\r", 3, 4, "", 4, 4)]
    [InlineData("", 1, 0, "", 0, 0)]
    [InlineData("a\nb\n", 3, 4, "", 4, 4)]
    public void Unicode_BOM_and_mixed_newlines_have_exact_source_and_DOM_line_ranges(string source, int line,
        int caret, string expected, int domStart, int domEnd) => Run(async () =>
    {
        await using var f = await Open();
        Assert.True(f.Workspace.CreateFileFromBytes("розділ/текст.tex", [239, 187, 191, .. Encoding.UTF8.GetBytes(source)]));
        var file = f.Workspace.Files.Single(x => x.RelativePath == "розділ/текст.tex");
        var output = await f.Compile();
        var native = Match("/work/розділ/текст.tex", line);
        f.Query.Reply = _ => Task.FromResult(Matches(native));
        var forward = Assert.IsType<SyncTeXNavigationResult>(await f.Policy.ForwardAsync(file.Id, caret));
        Assert.Equal(line, f.Query.Requests[^1].Line);
        var inverse = Assert.IsType<SyncTeXNavigationResult>(await f.Policy.InverseAsync(2, 21.25, 32.5));
        Assert.Equal(SyncTeXDirection.Inverse, f.Query.Requests[^1].Direction);
        Assert.Equal(2, f.Query.Requests[^1].Page); Assert.Equal(21.25, f.Query.Requests[^1].H); Assert.Equal(32.5, f.Query.Requests[^1].V);
        foreach (var result in new[] { forward, inverse })
        {
            var selection = Assert.IsType<SyncTeXNavigationSelection>(await f.Policy.PrepareSourceAsync(result, 0));
            Assert.Same(output.Snapshot, selection.Result.Snapshot); Assert.Same(native, selection.Match.Native);
            Assert.Equal(source, selection.Source);
            Assert.Equal(expected, selection.Source[selection.SourceRange.Start..selection.SourceRange.End]);
            Assert.Equal(new SourceRange(domStart, domEnd), selection.TextareaRange);
            Assert.Equal(expected, selection.ExpectedTextareaText[domStart..domEnd]);
            Assert.True(selection.File.HasBom);
        }
    });

    [Theory][InlineData("main.tex")][InlineData("./main.tex")][InlineData("/work/main.tex")]
    public void Only_explicit_engine_path_spellings_bind(string path) => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        f.Query.Reply = _ => Task.FromResult(Matches(Match(path)));
        Assert.NotNull(await f.Inverse());
    });

    [Theory]
    [InlineData("../main.tex")][InlineData("/work/../main.tex")][InlineData("/work/./main.tex")]
    [InlineData(".//work/main.tex")][InlineData("././main.tex")][InlineData("/work//main.tex")]
    [InlineData("MAIN.tex")][InlineData("main.tex ")][InlineData("main%2etex")]
    [InlineData("file:///work/main.tex")][InlineData("https://host/main.tex")]
    [InlineData("C:\\work\\main.tex")][InlineData("work\\main.tex")][InlineData("main.tex\0")]
    [InlineData("/texlive/main.tex")][InlineData("/work/texmf/article.cls")]
    [InlineData("main.aux")][InlineData("main.synctex")][InlineData("main.pdf")]
    [InlineData("asset.bin")][InlineData("missing.tex")][InlineData("same.tex")]
    public void Untrusted_names_never_resolve_aliases_resources_generated_binary_or_ambiguous_basenames(string path) => Run(async () =>
    {
        await using var f = await Open();
        Assert.True(f.Workspace.CreateFileFromBytes("a/same.tex", Encoding.UTF8.GetBytes("a")));
        Assert.True(f.Workspace.CreateFileFromBytes("b/same.tex", Encoding.UTF8.GetBytes("b")));
        Assert.True(f.Workspace.CreateFileFromBytes("main.aux", Encoding.UTF8.GetBytes("generated text is not source")));
        await f.Compile();
        int reads = f.Storage.Repo.Reads;
        f.Query.Reply = _ => Task.FromResult(Matches(Match(path)));
        Assert.Null(await f.Inverse()); Assert.Equal(reads, f.Storage.Repo.Reads);
    });

    [Fact] public void Unicode_paths_are_not_normalized_and_captured_project_style_is_source_not_runtime_alias() => Run(async () =>
    {
        await using var f = await Open();
        Assert.True(f.Workspace.CreateFileFromBytes("café.tex", Encoding.UTF8.GetBytes("café")));
        Assert.True(f.Workspace.CreateFileFromBytes("local.sty", Encoding.UTF8.GetBytes("style")));
        await f.Compile();
        f.Query.Reply = _ => Task.FromResult(Matches(Match("cafe\u0301.tex")));
        Assert.Null(await f.Inverse());
        f.Query.Reply = _ => Task.FromResult(Matches(Match("/work/local.sty")));
        Assert.NotNull(await f.Inverse());
    });

    [Fact] public void Invalid_matches_are_filtered_without_changing_native_order_or_fabricating_columns() => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        var first = Match("chapter.tex") with { Column = null };
        var second = Match(line: 2);
        f.Query.Reply = _ => Task.FromResult(new SyncTeXQueryOutcome(SyncTeXQueryStatus.Completed,
            [Match("asset.bin"), Match(line: 999), first, Match(line: 0), second, Match(page: 0),
                Match() with { Point = new(double.NaN, 1) }], true));
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Inverse());
        Assert.True(result.Truncated); Assert.Equal(2, result.Matches.Length);
        Assert.Same(first, result.Matches[0].Native); Assert.Same(second, result.Matches[1].Native);
        Assert.Null(result.Matches[0].Native.Column);
        Assert.Null(await f.Policy.PrepareSourceAsync(result, -1)); Assert.Null(await f.Policy.PrepareSourceAsync(result, 2));
    });

    [Theory]
    [InlineData("page")][InlineData("tag-zero")][InlineData("tag-negative")]
    [InlineData("column-zero")][InlineData("column-negative")]
    [InlineData("point-h")][InlineData("point-v")][InlineData("box-h")][InlineData("box-v")]
    [InlineData("width-negative")][InlineData("height-negative")][InlineData("depth-negative")]
    [InlineData("width-large")][InlineData("height-large")][InlineData("depth-large")]
    [InlineData("nan")][InlineData("infinity")]
    public void Public_query_seam_refuses_malformed_native_fields(string malformed) => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        var match = malformed switch
        {
            "page" => Match(page: 513),
            "tag-zero" => Match() with { Tag = 0 },
            "tag-negative" => Match() with { Tag = -1 },
            "column-zero" => Match() with { Column = 0 },
            "column-negative" => Match() with { Column = -1 },
            "point-h" => Match() with { Point = new(1000000.01, 0) },
            "point-v" => Match() with { Point = new(0, -1000000.01) },
            "box-h" => Match() with { Box = new(-1000000.01, 0, 1, 1, 0) },
            "box-v" => Match() with { Box = new(0, 1000000.01, 1, 1, 0) },
            "width-negative" => Match() with { Box = new(0, 0, -0.01, 1, 0) },
            "height-negative" => Match() with { Box = new(0, 0, 1, -0.01, 0) },
            "depth-negative" => Match() with { Box = new(0, 0, 1, 1, -0.01) },
            "width-large" => Match() with { Box = new(0, 0, 1000000.01, 1, 0) },
            "height-large" => Match() with { Box = new(0, 0, 1, 1000000.01, 0) },
            "depth-large" => Match() with { Box = new(0, 0, 1, 1, 1000000.01) },
            "nan" => Match() with { Box = new(0, 0, double.NaN, 1, 0) },
            _ => Match() with { Box = new(0, double.NegativeInfinity, 1, 1, 0) }
        };
        f.Query.Reply = _ => Task.FromResult(Matches(match));
        Assert.Null(await f.Inverse());
    });

    [Fact] public void Fixed_native_limits_are_inclusive_and_nullable_column_is_valid() => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        var native = Match(page: 512) with { Tag = 1, Column = null,
            Point = new(-1000000, 1000000), Box = new(1000000, -1000000, 0, 1000000, 0) };
        f.Query.Reply = _ => Task.FromResult(Matches(Enumerable.Repeat(native, 32).ToArray()));
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Policy.InverseAsync(512, -1000000, 1000000));
        Assert.Equal(32, result.Matches.Length);
        Assert.All(result.Matches, match => Assert.Same(native, match.Native));
    });

    [Theory][InlineData(false)][InlineData(true)]
    public void Identity_only_and_absent_maps_are_unavailable_without_query_dispatch(bool identity) => Run(async () =>
    {
        await using var f = await Open();
        var output = await f.Compile(mapBytes: false, mapIdentity: identity);
        Assert.False(output.HasSourceMapPayload);
        Assert.Null(await f.Inverse()); Assert.Null(await f.Policy.ForwardAsync(f.Main, 0));
        Assert.Empty(f.Query.Requests);
    });

    [Fact] public void Forward_rejects_binary_unknown_and_invalid_UTF16_carets_without_query() => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        var binary = f.Workspace.Files.Single(x => x.RelativePath == "asset.bin");
        Assert.Null(await f.Policy.ForwardAsync(binary.Id, 0));
        Assert.Null(await f.Policy.ForwardAsync(FileId.New(), 0));
        foreach (var caret in new[] { -1, 8, 10, 10000 }) // emoji split, CRLF split
            Assert.Null(await f.Policy.ForwardAsync(f.Main, caret));
        Assert.Null(await f.Policy.InverseAsync(0, 1, 2));
        Assert.Null(await f.Policy.InverseAsync(513, 1, 2));
        Assert.Null(await f.Policy.InverseAsync(1, 1000000.01, 2));
        Assert.Null(await f.Policy.InverseAsync(1, 1, -1000000.01));
        Assert.Null(await f.Policy.InverseAsync(1, double.PositiveInfinity, 2));
        Assert.Null(await f.Policy.InverseAsync(1, 1, double.NaN));
        Assert.Empty(f.Query.Requests);
    });

    [Theory]
    [InlineData(SyncTeXQueryStatus.NoMatches)][InlineData(SyncTeXQueryStatus.InvalidInput)]
    [InlineData(SyncTeXQueryStatus.Unavailable)][InlineData(SyncTeXQueryStatus.Busy)]
    [InlineData(SyncTeXQueryStatus.Cancelled)][InlineData(SyncTeXQueryStatus.TimedOut)]
    [InlineData(SyncTeXQueryStatus.Disposed)][InlineData(SyncTeXQueryStatus.InvalidResponse)][InlineData(SyncTeXQueryStatus.Failed)]
    public void Query_failures_cannot_authorize_matches(SyncTeXQueryStatus status) => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        f.Query.Reply = _ => Task.FromResult(new SyncTeXQueryOutcome(status, [Match()]));
        Assert.Null(await f.Inverse());
    });

    [Theory]
    [InlineData("edit")][InlineData("ABA")][InlineData("other-file")][InlineData("save")]
    [InlineData("rename")][InlineData("delete")][InlineData("selection")][InlineData("selection-ABA")]
    [InlineData("cancel-compile")][InlineData("close")][InlineData("reopen")][InlineData("project")]
    [InlineData("takeover")][InlineData("dispose-policy")][InlineData("dispose-session")][InlineData("samehash-compile")]
    public void Every_owner_stamp_change_invalidates_result_and_prepared_selection(string change) => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Inverse());
        var selection = Assert.IsType<SyncTeXNavigationSelection>(await f.Policy.PrepareSourceAsync(result, 0));
        await f.Change(change);
        Assert.False(f.Policy.IsCurrent(result)); Assert.False(f.Policy.IsCurrent(selection));
        Assert.Null(await f.Policy.PrepareSourceAsync(result, 0));
        if (change == "samehash-compile")
        {
            var replacement = f.Session.DisplayedOutput!;
            Assert.Equal(result.Output.OutputHash, replacement.OutputHash);
            Assert.Equal(result.Output.InputContentDigest, replacement.InputContentDigest);
            Assert.NotEqual(result.Output.RequestId, replacement.RequestId);
            Assert.NotEqual(result.Output.WorkerEpoch, replacement.WorkerEpoch);
            Assert.NotEqual(result.Snapshot.Identity.CaptureId, replacement.Snapshot.Identity.CaptureId);
            Assert.NotNull(await f.Inverse());
        }
    });

    [Fact] public void New_capture_invalidates_before_same_hash_replacement_arrives() => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Inverse());
        await f.Session.CompileAsync(); var worker = await f.Config.Workers.Next();
        Assert.Same(result.Output, f.Session.DisplayedOutput);
        Assert.False(f.Policy.IsCurrent(result)); Assert.Null(await f.Inverse());
        worker.Reply.TrySetResult(worker.Output(CompileOutputStatus.Failed));
        await f.Session.LastTicket!.Completion.WaitAsync(Bound);
    });

    [Fact] public void Cross_policy_results_are_not_authority_and_disposal_borrows_owners() => Run(async () =>
    {
        await using var f = await Open(); await f.Compile();
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Inverse());
        using var other = new SyncTeXNavigationPolicy(f.Workspace, f.Session, f.Query);
        Assert.False(other.IsCurrent(result)); Assert.Null(await other.PrepareSourceAsync(result, 0));
        f.Policy.Dispose();
        Assert.Equal(0, f.Query.Disposals); Assert.False(f.Session.Disposed); Assert.True(f.Workspace.IsOpen);
        Assert.NotNull(await other.InverseAsync(1, 1, 1));
    });

    [Fact] public void Read_only_navigation_does_not_select_a_file_unlock_compile_commit_or_save() => Run(async () =>
    {
        await using var f = await Open(readOnly: true); await f.Compile();
        f.Query.Reply = _ => Task.FromResult(Matches(Match("chapter.tex")));
        var before = f.Storage.State(); int workers = f.Config.Workers.Created.Count;
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Inverse());
        var selection = Assert.IsType<SyncTeXNavigationSelection>(await f.Policy.PrepareSourceAsync(result, 0));
        Assert.Equal(before, f.Storage.State()); Assert.Equal(0, f.Storage.Repo.Commits);
        Assert.Equal(workers, f.Config.Workers.Created.Count); Assert.True(f.Workspace.IsReadOnly);
        Assert.False(f.Policy.IsCurrent(selection, requireActiveFile: true));
        Assert.True(f.Workspace.SelectFile(selection.File.FileId));
        Assert.True(f.Policy.IsCurrent(selection, requireActiveFile: true));
    });
}
