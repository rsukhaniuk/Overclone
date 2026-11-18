using System.Collections;
using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Xunit;

namespace Workspace.Tests.CompilerSuite;

public sealed class CompatibilityContractTests
{
    private const NativeRuntimeProfile Profile = NativeRuntimeProfile.LocalCompilerCompatibility;

    [Fact]
    public void New_profile_preserves_numeric_contract_and_distinguishes_package_identity()
    {
        Assert.Equal(0, (int)NativeRuntimeProfile.Legacy);
        Assert.Equal(1, (int)NativeRuntimeProfile.LocalLatex2023);
        Assert.Equal(2, (int)NativeRuntimeProfile.LocalLatex2023Authoring);
        Assert.Equal(3, (int)NativeRuntimeProfile.LocalLatex2023SyncTeX);
        Assert.Equal(4, (int)NativeRuntimeProfile.LocalLatex2023Dvi);
        Assert.Equal(5, (int)NativeRuntimeProfile.LocalCompilerSuite);
        Assert.Equal(6, (int)Profile);
        foreach (var profile in Enum.GetValues<NativeRuntimeProfile>())
            Assert.Equal((int)profile is 5 or 6 or 7 or 8 or 9 or 10 or 12 or 13 or 14 or 15 or 16, NativeRuntimeProfiles.IsSuite(profile));
        Assert.False(NativeRuntimeProfiles.IsSuite((NativeRuntimeProfile)11));
        Assert.False(NativeRuntimeProfiles.Known((NativeRuntimeProfile)11));
        Assert.False(NativeRuntimeProfiles.IsSuite((NativeRuntimeProfile)int.MaxValue));
        Assert.False(NativeRuntimeProfiles.Known((NativeRuntimeProfile)int.MaxValue));
        Assert.True(NativeRuntimeProfiles.Known(Profile));
        Assert.True(NativeRuntimeProfiles.Modern(Profile));
        Assert.Equal("local-compiler-suite-v1", NativeRuntimeProfiles.IdentityName(NativeRuntimeProfile.LocalCompilerSuite));
        Assert.Equal("local-compiler-suite-v2", NativeRuntimeProfiles.IdentityName(Profile));
        Assert.Equal(NativeRuntimeProfile.LocalCompilerInputBudget, BundledRuntimeAssets.CompilerProfile);
    }

    [Theory]
    [InlineData(NativeCompilerEngine.PdfLaTeX, 6)]
    [InlineData(NativeCompilerEngine.LaTeXDvi, 7)]
    [InlineData(NativeCompilerEngine.XeLaTeX, 8)]
    [InlineData(NativeCompilerEngine.LuaLaTeX, 9)]
    public void Both_suites_reuse_closed_phase_options_and_budgets(NativeCompilerEngine engine, int phase)
    {
        var old = NativeCompilerSuiteContract.CreatePlan(engine);
        var expanded = NativeCompilerSuiteContract.CreatePlan(engine, profile: Profile);
        Assert.True(old.IsValid); Assert.True(expanded.IsValid);
        Assert.Equal(NativeRuntimeProfile.LocalCompilerSuite, old.Profile);
        Assert.Equal(Profile, expanded.Profile);
        Assert.Equal(phase, expanded.EffectivePhaseVersion);
        Assert.Equal(old, expanded with { Profile = old.Profile });
        Assert.Equal(8388608, expanded.ProjectFileBytes);
        Assert.True(NativeCompilerSuiteContract.MatchesOptions(NativeCompilerSuiteContract.SelectionOptions(engine), engine));
        Assert.False((expanded with { PhaseProtocolVersion = 10 }).IsValid);
        Assert.False((expanded with { DviOutput = !expanded.DviOutput }).IsValid);
        Assert.False((expanded with { Engine = (NativeCompilerEngine)99 }).IsValid);
        Assert.False((expanded with { MaxPhaseFiles = NativeCompilerSuiteContract.Files + 193 }).IsValid);
        Assert.False((expanded with { MaxInputBytes = NativeCompilerSuiteContract.TotalBytes + 1 }).IsValid);
        Assert.False((expanded with { MaxFileBytes = NativeCompilerSuiteContract.FileBytes + 1 }).IsValid);
        var custom = NativeCompilerSuiteContract.CreatePlan(engine, 4, TimeSpan.FromSeconds(20), Profile);
        Assert.True(custom.IsValid); Assert.Equal(4, custom.MaxLatexPasses); Assert.Equal(TimeSpan.FromSeconds(20), custom.JobDeadline);
    }

    [Fact]
    public void Expanded_suite_reuses_executables_but_not_the_old_pdf_format_pin()
    {
        var old = NativeRuntimeProfile.LocalCompilerSuite;
        Assert.Equal(NativeRuntimeProfiles.CompilerLoaderHash(old), NativeRuntimeProfiles.CompilerLoaderHash(Profile));
        Assert.Equal(NativeRuntimeProfiles.CompilerWasmHash(old), NativeRuntimeProfiles.CompilerWasmHash(Profile));
        Assert.Equal(NativeRuntimeProfiles.CompilerLoaderLength(old), NativeRuntimeProfiles.CompilerLoaderLength(Profile));
        Assert.Equal(NativeRuntimeProfiles.CompilerWasmLength(old), NativeRuntimeProfiles.CompilerWasmLength(Profile));
        Assert.Equal(NativeCompilerCompatibilityPins.PdfFormatLength, NativeRuntimeProfiles.FormatLength(Profile));
        Assert.Equal(NativeCompilerCompatibilityPins.PdfFormatHash, NativeRuntimeProfiles.FormatHash(Profile));
        Assert.Equal(NativeRuntimeProfiles.SyncTeXFormatLength, NativeRuntimeProfiles.FormatLength(old));
        Assert.Equal(NativeRuntimeProfiles.SyncTeXFormatHash, NativeRuntimeProfiles.FormatHash(old));
        Assert.Equal(6, NativeRuntimeProfiles.ProtocolVersion(Profile));
        var limits = LocalRuntimeSelection.LimitsFor(Profile);
        Assert.True(limits.IsValid);
        Assert.Equal(LocalRuntimeSelection.LimitsFor(old) with { Profile = Profile }, limits);
        Assert.Same(LocalRuntimeSelection.SuiteBudget, LocalRuntimeSelection.BudgetFor(Profile));
        Assert.False((limits with { MaxFiles = NativeCompilerSuiteContract.Files + 1 }).IsValid);
        Assert.False((limits with { MaxTotalBytes = NativeCompilerSuiteContract.TotalBytes + 1 }).IsValid);
        Assert.False((limits with { MaxFileBytes = NativeCompilerSuiteContract.FileBytes + 1 }).IsValid);
    }

    [Fact]
    public void Unknown_profile_and_mismatched_limits_reject_before_inspecting_untrusted_lists()
    {
        var unknownProfile = (NativeRuntimeProfile)int.MaxValue;
        var result = NativeRuntimePackage.Create(new Uninspectable<NativeResourceInput>(), new Uninspectable<NativeResourcePin>(),
            LocalRuntimeSelection.LimitsFor(Profile) with { Profile = unknownProfile }, unknownProfile);
        Assert.Null(result.Package); Assert.Equal(NativeRuntimeIssue.InvalidLimits, result.Issue);
        var wrongLimits = NativeRuntimePackage.Create(new Uninspectable<NativeResourceInput>(), new Uninspectable<NativeResourcePin>(),
            LocalRuntimeSelection.LimitsFor(NativeRuntimeProfile.LocalCompilerSuite), Profile);
        Assert.Null(wrongLimits.Package); Assert.Equal(NativeRuntimeIssue.InvalidLimits, wrongLimits.Issue);
    }

    [Theory]
    [InlineData(NativeRuntimeProfiles.AuthoringResourceCount)]
    [InlineData(NativeRuntimeProfiles.SyncTeXResourceCount)]
    [InlineData(NativeRuntimeProfiles.DviResourceCount)]
    [InlineData(NativeCompilerSuitePins.ResourceCount)]
    public void Direct_profile_selection_rejects_duplicate_names_as_format_errors(int count)
    {
        var pins = Enumerable.Range(0, count).Select(i => new NativeResourcePin($"resource{i}.sty", 0, new string('0', 64))).ToArray();
        pins[^1] = pins[0];
        Assert.Throws<FormatException>(() => LocalRuntimeSelection.ProfileFor(pins));
    }

    private sealed class Uninspectable<T> : IReadOnlyList<T>
    {
        public int Count => throw new InvalidOperationException("Invalid contract inspected untrusted count");
        public T this[int index] => throw new InvalidOperationException("Invalid contract inspected untrusted entry");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Invalid contract enumerated untrusted data");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
