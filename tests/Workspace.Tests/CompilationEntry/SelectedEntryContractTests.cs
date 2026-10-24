using Client.Compilation;
using Client.Compilation.Native;
using Xunit;

namespace Workspace.Tests.CompilationEntry;

public sealed class SelectedEntryContractTests
{
    [Theory]
    [InlineData("renamed.tex", "renamed.tex", "renamed.tex", "renamed")]
    [InlineData("dir/paper.tex", "dir/paper.tex", "paper.tex", "paper")]
    [InlineData("тези/праця.tex", "тези/праця.tex", "праця.tex", "праця")]
    [InlineData("space dir/paper.tex", "\"space dir/paper.tex\"", "paper.tex", "paper")]
    [InlineData("my thesis.tex", "\"my thesis.tex\"", "my thesis.tex", "my thesis")]
    [InlineData("dir/my thesis.tex", "\"dir/my thesis.tex\"", "my thesis.tex", "my thesis")]
    [InlineData("space dir/my thesis.tex", "\"space dir/my thesis.tex\"", "my thesis.tex", "my thesis")]
    [InlineData("dir/Paper.TEX", "dir/Paper.TEX", "Paper.TEX", "Paper")]
    [InlineData("e\u0301.tex", "e\u0301.tex", "e\u0301.tex", "e\u0301")]
    public void Exact_path_native_literal_tool_entry_job_outputs_and_state_are_separate(string root, string latex, string bibtex, string job)
    {
        var entry = Assert.IsType<NativeSelectedEntry>(NativeSelectedEntryContract.Create(root, [root, "chapters/one.tex", "asset.bin"], out var error));
        Assert.Null(error); Assert.Equal(root, entry.SelectedPath); Assert.Equal(latex, entry.LatexEntry);
        Assert.Equal(bibtex, entry.BibtexEntry); Assert.Equal(job, entry.JobName);
        Assert.Equal(job + ".pdf", entry.Pdf); Assert.Equal(job + ".aux", entry.Aux); Assert.Equal(job + ".bbl", entry.Bbl);
        Assert.Equal(10, entry.Outputs.Length); Assert.Contains("chapters/one.aux", entry.State);
        Assert.DoesNotContain(root[..^4] + ".pdf", entry.Outputs.Where(n => n != job + ".pdf"));
        Assert.All(new[] { ".aux", ".bbl", ".toc", ".lof", ".lot", ".out" }, ext => Assert.True(entry.IsState(job + ext)));
        Assert.False(entry.IsState(job + ".log")); Assert.False(entry.IsState(job + ".blg")); Assert.False(entry.IsState("main.bbl"));
        Assert.Null(NativeSelectedEntryContract.ValidateNamespace(entry, [root, "chapters/one.tex"], entry.State));
        Assert.NotNull(NativeSelectedEntryContract.ValidateNamespace(entry, [root], ["main.bbl"]));
        Assert.NotNull(NativeSelectedEntryContract.ValidateNamespace(entry, [root], [entry.Aux, entry.Aux]));
    }

    [Theory]
    [InlineData("../paper.tex")][InlineData("/paper.tex")][InlineData("a//paper.tex")]
    [InlineData("a\\paper.tex")][InlineData("a/./paper.tex")][InlineData("a\0.tex")]
    [InlineData(" paper.tex")]
    [InlineData("a\n.tex")][InlineData("a\t.tex")][InlineData("a\".tex")]
    [InlineData("a%.tex")][InlineData("a#.tex")][InlineData("a{b}.tex")]
    [InlineData("a&b.tex")][InlineData("a'b.tex")][InlineData("a~b.tex")]
    [InlineData("a🙂.tex")][InlineData("NUL.tex")][InlineData(".git/paper.tex")]
    [InlineData("a/-.tex")][InlineData(".tex")][InlineData("a/ my thesis.tex")]
    [InlineData("my thesis .tex")][InlineData("dir/my thesis.tex ")][InlineData("dir /my thesis.tex")]
    [InlineData("dir/my thesis\t.tex")][InlineData("dir/my thesis\".tex")][InlineData("dir/my thesis%.tex")]
    [InlineData("a.tex ")][InlineData("paper.txt")][InlineData("a\u00a0b.tex")]
    public void Unsupported_path_or_bootstrap_syntax_rejects_without_repair(string name)
    {
        Assert.NotNull(NativeSelectedEntryContract.ValidateEntry(name));
        Assert.Null(NativeSelectedEntryContract.Create(name, [name], out var error)); Assert.NotNull(error);
    }

    [Fact]
    public void Both_lone_surrogates_reject_without_discovery_serialization()
    {
        // Discovery serializes invalid Unicode as replacement characters: construct
        // both distinct malformed strings inside one test instead of InlineData.
        foreach (int codeUnit in new[] { 0xd800, 0xdc00 })
        {
            string name = "a" + (char)codeUnit + ".tex";
            Assert.NotNull(NativeSelectedEntryContract.ValidateEntry(name));
            Assert.Null(NativeSelectedEntryContract.Create(name, [name], out var error)); Assert.NotNull(error);
        }
    }

    [Fact]
    public void Byte_not_character_budget_and_generated_output_bytes_are_checked()
    {
        Assert.Null(NativeSelectedEntryContract.ValidateEntry(new string('x', 245) + ".tex"));
        Assert.NotNull(NativeSelectedEntryContract.ValidateEntry(new string('x', 246) + ".tex"));
        Assert.Null(NativeSelectedEntryContract.ValidateEntry(new string('я', 122) + ".tex"));
        Assert.NotNull(NativeSelectedEntryContract.ValidateEntry(new string('я', 123) + ".tex"));
        // .tex itself fits249, but the longest generated extension .pdf/.aux etc fits too.
        var entry = NativeSelectedEntryContract.Create(new string('x', 245) + ".tex", [new string('x', 245) + ".tex"], out var error);
        Assert.NotNull(entry); Assert.Null(error);
        string spaced = "a " + new string('x', 243) + ".tex";
        entry = NativeSelectedEntryContract.Create(spaced, [spaced], out error);
        Assert.NotNull(entry); Assert.Null(error); Assert.Equal("\"" + spaced + "\"", entry!.LatexEntry);
        Assert.Equal(spaced, entry.BibtexEntry); Assert.Equal(249, System.Text.Encoding.UTF8.GetByteCount(entry.Pdf));
        Assert.NotNull(NativeSelectedEntryContract.ValidateEntry("a " + new string('x', 244) + ".tex"));
    }

    [Theory]
    [InlineData("paper.pdf")][InlineData("PAPER.BBL")][InlineData("paper.toc/child.tex")]
    [InlineData("dir")][InlineData("n6.map")]
    public void Inputs_cannot_alias_or_prefix_selected_generated_names_or_runtime(string input)
    {
        var root = input == "n6.map" ? "n6.map/paper.tex" : "dir/paper.tex";
        var entry = NativeSelectedEntryContract.Create(root, [root], out _)!;
        Assert.NotNull(NativeSelectedEntryContract.ValidateNamespace(entry, [root, input], []));
    }

    [Fact]
    public void Generated_aliases_and_unicode_aliases_are_not_coalesced()
    {
        Assert.Null(NativeSelectedEntryContract.Create("dir/paper.tex", ["dir/paper.tex", "paper.TEX"], out var error));
        Assert.Equal("v4-namespace-collision", error);
        var entry = NativeSelectedEntryContract.Create("теза.tex", ["теза.tex"], out _)!;
        Assert.NotNull(NativeSelectedEntryContract.ValidateNamespace(entry, ["теза.tex", "теза.pdf"], []));
        Assert.NotNull(NativeSelectedEntryContract.ValidateNamespace(entry, ["теза.tex", "ТЕЗА.PDF"], []));
        entry = NativeSelectedEntryContract.Create("é.tex", ["é.tex"], out _)!;
        Assert.NotNull(NativeSelectedEntryContract.ValidateNamespace(entry, ["é.tex", "e\u0301.PDF"], []));
        entry = NativeSelectedEntryContract.Create("dir/my thesis.tex", ["dir/my thesis.tex"], out _)!;
        Assert.NotNull(NativeSelectedEntryContract.ValidateNamespace(entry, ["dir/my thesis.tex", "MY THESIS.BBL"], []));
        Assert.NotNull(NativeSelectedEntryContract.ValidateNamespace(entry, ["dir/my thesis.tex", "my thesis.toc/child.tex"], []));
        Assert.Null(NativeSelectedEntryContract.Create("dir/my thesis.tex", ["dir/my thesis.tex", "MY THESIS.TEX"], out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void V5_is_explicit_profile2_only_and_does_not_change_old_defaults()
    {
        foreach (var profile in Enum.GetValues<NativeRuntimeProfile>().Where(profile => (int)profile <= 2))
        {
            var old = new NativeCompilePlan(false, 4, TimeSpan.FromSeconds(25), Profile: profile);
            Assert.Equal((int)profile + 1, old.EffectivePhaseVersion); Assert.True(old.IsValid);
            Assert.Equal(profile == NativeRuntimeProfile.LocalLatex2023Authoring, (old with { PhaseProtocolVersion = 5 }).IsValid);
            Assert.False((old with { PhaseProtocolVersion = 6 }).IsValid);
        }
        var plan = NativeSelectedEntryContract.CreatePlan(deadline: TimeSpan.FromSeconds(25));
        Assert.True(plan.IsValid); Assert.Equal(5, plan.EffectivePhaseVersion); Assert.Equal(768, plan.MaxPhaseFiles);
        Assert.Equal(67108864, plan.MaxInputBytes); Assert.Equal(33554432, plan.MaxFileBytes);
        Assert.False((plan with { MaxPhaseFiles = 769 }).IsValid);
        Assert.False(NativeSelectedEntryContract.MatchesOptions([]));
        Assert.False(NativeSelectedEntryContract.MatchesOptions(NativeAuthoringV4Contract.SelectionOptions));
        Assert.True(NativeSelectedEntryContract.MatchesOptions(NativeSelectedEntryContract.SelectionOptions));
        Assert.False(NativeSelectedEntryContract.MatchesOptions([NativeSelectedEntryContract.SelectionOption, new CompilationOption("extra", "5")]));
    }
}
