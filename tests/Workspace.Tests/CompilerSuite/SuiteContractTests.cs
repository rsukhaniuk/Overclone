using System.Collections;
using Client.Compilation;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using System.Text.Json;
using Workspace.Tests.CompilationNative;
using Xunit;

namespace Workspace.Tests.CompilerSuite;

public sealed class SuiteContractTests
{
    [Theory]
    [InlineData(NativeCompilerEngine.PdfLaTeX, 6, "pdftex")]
    [InlineData(NativeCompilerEngine.LaTeXDvi, 7, "latex-dvi")]
    [InlineData(NativeCompilerEngine.XeLaTeX, 8, "xelatex")]
    [InlineData(NativeCompilerEngine.LuaLaTeX, 9, "lualatex")]
    public void Only_closed_profile_engine_phase_pairs_are_valid(NativeCompilerEngine engine, int phase, string id)
    {
        var plan = NativeCompilerSuiteContract.CreatePlan(engine);
        Assert.True(plan.IsValid); Assert.Equal(phase, plan.EffectivePhaseVersion);
        Assert.Equal(id, NativeCompilerSuiteContract.EngineId(engine)); Assert.Equal(8388608, plan.ProjectFileBytes);
        Assert.True(NativeCompilerSuiteContract.MatchesOptions(NativeCompilerSuiteContract.SelectionOptions(engine), engine));
        Assert.False(NativeCompilerSuiteContract.MatchesOptions([], engine));
        Assert.False((plan with { PhaseProtocolVersion = 10 }).IsValid);
        Assert.False((plan with { Engine = (NativeCompilerEngine)99 }).IsValid);
        Assert.False((plan with { Profile = NativeRuntimeProfile.LocalLatex2023Dvi }).IsValid);
        Assert.False((plan with { DviOutput = !plan.DviOutput }).IsValid);
    }
    [Fact]
    public void Suite_limits_do_not_expand_existing_profiles_or_pdf_budgets()
    {
        var suite = LocalRuntimeSelection.LimitsFor(NativeRuntimeProfile.LocalCompilerSuite);
        Assert.True(suite.IsValid); Assert.Equal(8192, suite.MaxFiles); Assert.Equal(67108864, suite.MaxFileBytes); Assert.Equal(268435456, suite.MaxTotalBytes);
        Assert.False((suite with { MaxFiles = 8193 }).IsValid); Assert.False((suite with { MaxFileBytes = 67108865 }).IsValid);
        Assert.False((suite with { MaxTotalBytes = 268435457 }).IsValid);
        foreach (var profile in new[] { NativeRuntimeProfile.LocalLatex2023Authoring, NativeRuntimeProfile.LocalLatex2023SyncTeX, NativeRuntimeProfile.LocalLatex2023Dvi })
        {
            var old = LocalRuntimeSelection.LimitsFor(profile);
            Assert.Equal(512, old.MaxFiles); Assert.Equal(33554432, old.MaxFileBytes); Assert.Equal(67108864, old.MaxTotalBytes);
            Assert.Same(LocalRuntimeSelection.Budget, LocalRuntimeSelection.BudgetFor(profile));
        }
        Assert.Equal(262144, LocalRuntimeSelection.MaxPinsBytes); Assert.Equal(1048576, LocalRuntimeSelection.MaxMetadataBytes);
        Assert.Equal(2097152, LocalRuntimeSelection.SuiteMaxPinsBytes); Assert.Equal(4194304, LocalRuntimeSelection.SuiteMaxMetadataBytes);
        Assert.Equal(8388608, NativeLatexDviContract.PdfBytes);
        Assert.Equal("f48f93c5176be1337486c2b432b829637a0a231737dba7eb7d65c69b6d4d0481", NativeRuntimeProfiles.DviClosureHash);
        Assert.Equal(22342701, NativeRuntimeProfiles.DviFormatLength);
        Assert.Equal("04c29d7c297a114c4173e3597dc2f886cefa84889f040e5d4ad897ce5e5d72d0", NativeRuntimeProfiles.DviFormatHash);
        Assert.Equal("6dc117f6ca3b16cea8e675683486e1f641a933fbfdccc9de8f94f51224e18c61", NativeRuntimeProfiles.DviConverterWasmHash);
    }
    [Theory]
    [InlineData("geometry.sty", true)] [InlineData("pgf/gd/control/pgfgd-core.lua", true)]
    [InlineData("pgf/gd/interface/InterfaceToAlgorithms.lua", true)] [InlineData("font.otf", true)]
    [InlineData("../escape.lua", false)] [InlineData("pgf/../x.lua", false)] [InlineData("pgf/./x.lua", false)]
    [InlineData("pgf//x.lua", false)] [InlineData("/x.lua", false)] [InlineData("pgf\\x.lua", false)]
    [InlineData("pgf:x.lua", false)] [InlineData("pgf/x.sty", false)] [InlineData(".", false)] [InlineData("..", false)]
    [InlineData("укр.lua", false)] [InlineData("a\0.lua", false)]
    public void Only_bounded_relative_ASCII_Lua_module_paths_may_be_qualified(string name, bool valid)
        => Assert.Equal(valid, NativeCompilerSuiteContract.ResourceName(name));
    [Fact]
    public void Module_depth_and_segment_lengths_are_bounded()
    {
        Assert.False(NativeCompilerSuiteContract.ResourceName(string.Join('/', Enumerable.Repeat("a", 12)) + "/a.lua"));
        Assert.False(NativeCompilerSuiteContract.ResourceName(new string('a', 101) + ".lua"));
    }
    [Fact]
    public void Xe_entry_is_raw_and_XDV_is_never_carried_state()
    {
        var entry = NativeCompilerSuiteContract.CreateEntry("тези/my thesis.tex", ["тези/my thesis.tex", "chapter.tex"], NativeCompilerEngine.XeLaTeX, out var error)!;
        Assert.Null(error); Assert.Equal("тези/my thesis.tex", entry.LatexEntry); Assert.Equal("my thesis.tex", entry.BibtexEntry);
        Assert.Contains("my thesis.xdv", entry.Outputs); Assert.False(entry.IsState("my thesis.xdv")); Assert.Contains("chapter.aux", entry.State);
        // U+10400 is a non-BMP letter accepted by the general selected-entry policy, but not XeTeX bootstrap.
        Assert.Null(NativeCompilerSuiteContract.CreateEntry("\U00010400.tex", ["\U00010400.tex"], NativeCompilerEngine.XeLaTeX, out error));
        Assert.Equal("xetex-non-bmp-main-unsupported", error);
    }
    [Fact]
    public void Lua_uses_fixed_result_job_and_rejects_generated_collisions()
    {
        var entry = NativeCompilerSuiteContract.CreateEntry("тези/my thesis.tex", ["тези/my thesis.tex", "chapters/part.tex"], NativeCompilerEngine.LuaLaTeX, out var error)!;
        Assert.Null(error); Assert.Equal("result", entry.JobName); Assert.Equal("result.tex", entry.BibtexEntry);
        Assert.Equal("тези/my thesis.tex", entry.LatexEntry); Assert.Equal("result.pdf", entry.Pdf);
        Assert.Equal(10, entry.Outputs.Length); Assert.Contains("chapters/part.aux", entry.State); Assert.False(entry.IsState("result.fmt"));
        foreach (string collision in new[] { "result.pdf", "RESULT.AUX", "result.log/part.tex", "chapters/part.aux" })
        {
            Assert.Null(NativeCompilerSuiteContract.CreateEntry("main.tex", ["main.tex", "chapters/part.tex", collision], NativeCompilerEngine.LuaLaTeX, out error));
            Assert.NotNull(error);
        }
    }
    [Fact]
    public void Unavailable_pins_fail_closed_without_accessing_untrusted_resource_bytes()
    {
        if (NativeCompilerSuitePins.Ready) return; // final root promotion is covered by fixture-backed tests
        var creation = NativeRuntimePackage.Create(new NoAccess(8192), [], LocalRuntimeSelection.LimitsFor(NativeRuntimeProfile.LocalCompilerSuite), NativeRuntimeProfile.LocalCompilerSuite);
        Assert.Null(creation.Package); Assert.Equal(NativeRuntimeIssue.Unavailable, creation.Issue);
        Assert.Throws<FormatException>(() => LocalRuntimeSelection.ProfileFor([new("arbitrary.lua", 0, new('0', 64))]));
    }
    [Fact]
    public async Task Unpinned_or_overcount_installations_refuse_before_any_selected_read_or_store_IO()
    {
        int reads = 0;
        var backend = new SetupBackend(); var selection = new SetupSelection { Value = "prior" };
        await using var setup = new NativeRuntimeSetup(new(backend), selection);
        foreach (int count in new[] { 1, 8193 })
        {
            var pins = Enumerable.Range(0, count).Select(i => new { name = $"resource{i}.lua", length = 0, sha256 = new string('0', 64) }).ToArray();
            var files = pins.Select(p => new LocalRuntimeFile(p.name, 0, _ => { reads++; return Task.FromResult(Array.Empty<byte>()); })).ToArray();
            await setup.InstallAsync(JsonSerializer.SerializeToUtf8Bytes(pins), files);
            Assert.Equal(RuntimeSetupState.InvalidSelection, setup.State);
        }
        Assert.Equal(0, reads); Assert.Equal(0, backend.Calls); Assert.Equal("prior", selection.Value);
    }
    internal sealed class NoAccess(int count) : IReadOnlyList<NativeResourceInput>
    {
        public int Count => count;
        public NativeResourceInput this[int index] => throw new InvalidOperationException("Untrusted closure accessed bytes");
        public IEnumerator<NativeResourceInput> GetEnumerator() => throw new InvalidOperationException("Unexpected enumeration");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
