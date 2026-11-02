using System.Text;
using Client.Compilation.SyncTeX;
using Client.Pdf;
using Client.Pdf.Preview;
using Xunit;
using static Workspace.Tests.CompilationLifecycle.LifecycleTestHost;
using static Workspace.Tests.SyncTeXNavigation.NavigationFixture;

namespace Workspace.Tests.SyncTeXNavigation;

// Synthetic transports exercise the actual live lifecycle/map policy. Native
// producer correctness and measured renderer composition need separate evidence.
public sealed class NativeGeometryNavigationTests
{
    private static byte[] Sidecar(bool available = true) => Encoding.UTF8.GetBytes(available
        ? """{"schemaVersion":1,"pages":[{"page":1,"status":"available","reason":"none","writerToPdfRaw":[2,0,0,-3,5,190],"writerPageBoxBp":[-10,-10,100,100]}]}"""
        : """{"schemaVersion":1,"pages":[{"page":1,"status":"unsupported","reason":"writer-origin-unavailable","writerToPdfRaw":null,"writerPageBoxBp":null}]}""");
    private static PdfPreviewGeometry View => new(new(0, 0, 100, 200), new(10, 20, 90, 180), 1, new PdfPoint(160, 80));

    [Theory][InlineData("latex-dvi")][InlineData("xelatex")][InlineData("lualatex")]
    public void A_map_without_required_native_geometry_cannot_use_legacy_pdftex_coordinates(string engine) => Run(async () =>
    {
        await using var f = await Open(engine: engine); await f.Compile();
        Assert.Null(await f.Policy.ForwardAsync(f.Main, 0));
        Assert.Null(await f.Policy.InverseAsync(1, View, .5, .4375));
        Assert.Empty(f.Query.Requests);
    });

    [Theory][InlineData("latex-dvi")][InlineData("xelatex")][InlineData("lualatex")]
    public void Live_owner_precedes_matrix_conversion_and_invalid_pages_never_query(string engine) => Run(async () =>
    {
        await using var f = await Open(engine: engine); var output = await f.Compile(nativeGeometry: Sidecar());
        var result = Assert.IsType<SyncTeXNavigationResult>(await f.Policy.InverseAsync(1, View, .5, .4375));
        var query = Assert.Single(f.Query.Requests);
        Assert.Equal(20, query.H, 8); Assert.Equal(30, query.V, 8); Assert.Same(output, result.Output);
        Assert.NotNull(f.Policy.PageTransform(result, 1)); Assert.Null(f.Policy.PageTransform(result, 2));
        Assert.Null(await f.Policy.InverseAsync(2, View, .5, .5)); Assert.Single(f.Query.Requests);
        Assert.Null(await f.Policy.InverseAsync(2, 20, 30)); Assert.Single(f.Query.Requests);
        await f.Change("ABA");
        Assert.False(f.Policy.IsCurrent(result)); Assert.Null(f.Policy.PageTransform(result, 1));
        Assert.Null(await f.Policy.InverseAsync(1, View, .5, .4375)); Assert.Single(f.Query.Requests);
    });

    [Fact] public void Unsupported_pages_engine_mismatch_and_late_queries_never_gain_authority() => Run(async () =>
    {
        await using var f = await Open(engine: "lualatex"); await f.Compile(nativeGeometry: Sidecar(false));
        Assert.Null(await f.Policy.InverseAsync(1, View, .5, .4375)); Assert.Empty(f.Query.Requests);
        await f.Compile(nativeGeometry: Sidecar(), geometryEngine: "pdflatex");
        Assert.Null(await f.Policy.InverseAsync(1, View, .5, .4375)); Assert.Empty(f.Query.Requests);
        await f.Compile(nativeGeometry: Sidecar());
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SyncTeXQueryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Query.Reply = _ => { reached.TrySetResult(); return release.Task; };
        var pending = f.Policy.InverseAsync(1, View, .5, .4375); await reached.Task.WaitAsync(Bound);
        await f.Change("samehash-compile"); release.SetResult(Matches(Match()));
        Assert.Null(await pending.WaitAsync(Bound));
    });

    [Theory]
    [InlineData("/work/project/розділ/текст файл.tex", true)]
    [InlineData("/work/project/./розділ/текст файл.tex", true)]
    [InlineData("/work/project/../розділ/текст файл.tex", false)]
    [InlineData("/work/project/././розділ/текст файл.tex", false)]
    [InlineData("/work/project/текст файл.tex", false)]
    [InlineData("/work/project/РОЗДІЛ/текст файл.tex", false)]
    public void Lua_full_project_prefixes_bind_exact_captured_paths_only(string path, bool expected) => Run(async () =>
    {
        await using var f = await Open(engine: "lualatex");
        Assert.True(f.Workspace.CreateFileFromBytes("розділ/текст файл.tex", Encoding.UTF8.GetBytes("Україна🙂")));
        await f.Compile(nativeGeometry: Sidecar()); f.Query.Reply = _ => Task.FromResult(Matches(Match(path)));
        Assert.Equal(expected, await f.Policy.InverseAsync(1, View, .5, .4375) is not null);
    });

    [Fact] public void Pdftex_does_not_gain_Lua_path_aliases() => Run(async () =>
    {
        await using var f = await Open(engine: "pdftex"); await f.Compile();
        f.Query.Reply = _ => Task.FromResult(Matches(Match("/work/project/./main.tex")));
        Assert.Null(await f.Policy.InverseAsync(1, View, .5, .5));
    });
}
