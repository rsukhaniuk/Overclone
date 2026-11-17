using Client.Compilation.Native;
using Client.Compilation.Provisioning;
using Xunit;

namespace Workspace.Tests.CompilerInputBudget16;

// Managed contract/metadata checks. Real native corpus/browser acceptance is separate.
public sealed class InputBudgetProfileTests
{
    private const NativeRuntimeProfile Profile = NativeInputBudgetContract.Profile;
    [Fact]
    public void Reserved_profile_delegates_parent_engines_formats_phases_and_query()
    {
        Assert.Equal(16, (int)Profile); Assert.True(NativeRuntimeProfiles.Known(Profile));
        Assert.True(NativeRuntimeProfiles.SupportsBiber(Profile)); Assert.True(NativeRuntimeProfiles.IsSuite(Profile));
        Assert.Equal("local-compiler-input-budget-v1", NativeRuntimeProfiles.IdentityName(Profile));
        Assert.Equal(128, NativeRuntimeProfiles.ProjectFileLimit(Profile)); Assert.Equal(256, NativeRuntimeProfiles.OutputDeclarationLimit(Profile));
        Assert.Equal(6, NativeRuntimeProfiles.ProtocolVersion(Profile));
        Assert.Equal(new[] { 23, 24, 21, 22 }, Enum.GetValues<NativeCompilerEngine>().Select(e => NativeCompilerSuiteContract.Phase(Profile, e)));
        Assert.Equal(10, NativeBiberCompilerContract.BiberPhase); Assert.Equal(64, NativeBiberCompilerContract.InputFiles);
        Assert.Equal(8388608, NativeBiberCompilerContract.InputBytes); Assert.Equal(8388608, NativeBiberCompilerContract.OutputBytes);
        foreach (var engine in Enum.GetValues<NativeCompilerEngine>())
        {
            var parent = NativeCompilerComponents.For(NativeOriginCompilerContract.Profile, engine);
            var next = NativeCompilerComponents.For(Profile, engine);
            Assert.Equal(parent with { OutputAdapter = next.OutputAdapter }, next);
            Assert.Equal(NativeCompilerOriginPins.QueryLoaderHash, next.QueryLoaderHash); Assert.Equal(NativeCompilerOriginPins.QueryWasmHash, next.QueryWasmHash);
            if (engine is NativeCompilerEngine.LaTeXDvi or NativeCompilerEngine.XeLaTeX)
            {
                Assert.EndsWith("-origin1-neutral1-input-budget1", next.OutputAdapter.Version);
                Assert.Equal(engine == NativeCompilerEngine.LaTeXDvi ? NativeCompilerInputBudgetPins.DviConverterWasmHash : NativeCompilerInputBudgetPins.XeConverterWasmHash, next.OutputAdapter.Hash!.Value.Hex);
                Assert.NotEqual(parent.OutputAdapter, next.OutputAdapter);
            }
            else Assert.Equal(parent, next);
            var plan = NativeCompilerSuiteContract.CreatePlan(engine, profile: Profile); Assert.True(plan.IsValid);
            Assert.Equal(8448, plan.MaxPhaseFiles); Assert.Equal(8 * 1024 * 1024, plan.ProjectFileBytes);
            Assert.Equal(engine != NativeCompilerEngine.PdfLaTeX, NativeNavigationContract.IsMapped(Profile, plan.EffectivePhaseVersion, engine));
            Assert.False((plan with { PhaseProtocolVersion = 0 }).IsValid);
        }
    }
    [Fact]
    public void Readiness_and_exact_profile_gate_new_source_bytes_without_changing_carry_output_or_old_profiles()
    {
        Assert.Equal(1, NativeInputBudgetContract.InputBudgetAbi); Assert.Equal(2097152, NativeInputBudgetContract.ProjectFileBytes);
        Assert.Equal(16777216, NativeInputBudgetContract.ProjectTotalBytes); Assert.Equal(8388608, NativeInputBudgetContract.PdfOutputBytes);
        Assert.Equal(NativeCompilerInputBudgetPins.Ready ? 2097152 : 1048576, NativeRuntimeProfiles.ProjectFileBytes(Profile));
        Assert.Equal(NativeCompilerInputBudgetPins.Ready ? 16777216 : 8388608, NativeRuntimeProfiles.ProjectTotalBytes(Profile));
        foreach (var old in Enum.GetValues<NativeRuntimeProfile>().Where(p => (int)p < 16))
        {
            Assert.Equal(1048576, NativeRuntimeProfiles.ProjectFileBytes(old)); Assert.Equal(8388608, NativeRuntimeProfiles.ProjectTotalBytes(old));
            var plan = NativeCompilerSuiteContract.CreatePlan(NativeCompilerEngine.XeLaTeX, profile: old);
            Assert.Equal(8388608, plan.ConverterProjectBytes);
            if (NativeRuntimeProfiles.Modern(old)) Assert.Equal(8388608, plan.ProjectFileBytes);
        }
        var next = NativeCompilerSuiteContract.CreatePlan(NativeCompilerEngine.XeLaTeX, profile: Profile);
        Assert.Equal(NativeRuntimeProfiles.ProjectFileBytes(Profile), next.SourceFileBytes);
        Assert.Equal(NativeRuntimeProfiles.ProjectTotalBytes(Profile), next.ProjectTotalBytes);
        Assert.Equal(NativeCompilerInputBudgetPins.Ready ? 16777216 : 8388608, next.ConverterProjectBytes);
        Assert.Equal(8388608, next.ProjectFileBytes); Assert.Equal(128, NativeAuthoringV4Contract.CarriedFiles);
        foreach (var unknown in new[] { (NativeRuntimeProfile)11, (NativeRuntimeProfile)17, (NativeRuntimeProfile)int.MaxValue })
        { Assert.False(NativeRuntimeProfiles.Known(unknown)); Assert.False(NativeInputBudgetContract.Enabled(unknown)); Assert.Equal(8388608, NativeRuntimeProfiles.ProjectTotalBytes(unknown)); }
    }
    [Fact]
    public void Unready_profile_refuses_before_accessing_any_untrusted_inventory()
    {
        if (NativeCompilerInputBudgetPins.Ready) return;
        Assert.False(NativeRuntimeProfiles.SuitePinsReady(Profile)); Assert.Equal(0, NativeRuntimeProfiles.InstalledCount(Profile));
        Assert.Equal("", NativeRuntimeProfiles.ClosureHash(Profile)); Assert.False(NativeInputBudgetContract.Enabled(Profile));
        Assert.False(NativeCompilerInputBudgetPins.MatchesComponents(new Dictionary<string, NativeResourcePin>()));
        Assert.Equal(NativeRuntimeIssue.Unavailable, NativeRuntimePackage.Create(new ForbiddenList<NativeResourceInput>(), new ForbiddenList<NativeResourcePin>(), LocalRuntimeSelection.LimitsFor(Profile), Profile).Issue);
    }
    [Fact]
    public void New_catalogue_and_four_real_faces_are_closed_metadata_not_aliases()
    {
        var additions = NativeCompilerInputBudgetPins.AdditionalResources; Assert.Equal(104, additions.Count);
        Assert.Equal(additions.Count, additions.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { "XITS-Bold.otf", "XITS-BoldItalic.otf", "XITS-Italic.otf", "XITS-Regular.otf" }, additions.Where(p => p.Name.StartsWith("XITS-", StringComparison.Ordinal)).Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(additions, p => p.Name == NativeCompilerSuitePins.XeCatalogResourceName);
        Assert.Equal(12315, NativeCompilerInputBudgetPins.ParentXeCatalogLength); Assert.Equal(12729, NativeCompilerInputBudgetPins.XeCatalogLength);
        Assert.NotEqual(NativeCompilerInputBudgetPins.ParentXeCatalogHash, NativeCompilerInputBudgetPins.XeCatalogHash);
        Assert.Throws<NotSupportedException>(() => ((IList<NativeResourcePin>)additions)[0] = new("invented.otf", 1, new string('0', 64)));
    }
    [Theory]
    [InlineData("notices/original-thesis-packages/xetex/README", true)]
    [InlineData("notices/original-thesis-packages/xetex/OTHER", false)]
    [InlineData("notices/original-thesis-packages/../README", false)]
    [InlineData("notices/thesis-packages/xits/OFL.txt", true)]
    public void Notice_namespace_extension_is_finite(string name, bool permitted) => Assert.Equal(permitted, NativeInputBudgetContract.NoticeName(name));
    [Fact]
    public void Existing_non_bmp_selected_entry_support_keeps_native_paths_and_namespace_guards()
    {
        const string selected = "тези/робота 😀.tex"; var names = new[] { selected, "chapter.tex" };
        var entry = NativeCompilerSuiteContract.CreateEntry(selected, names, NativeCompilerEngine.XeLaTeX, out var error, Profile);
        Assert.Null(error); Assert.NotNull(entry); Assert.Equal(selected, entry.SelectedPath); Assert.Equal(selected, entry.LatexEntry);
        Assert.Null(NativeSelectedEntryContract.ValidateEntry(selected, Profile, NativeCompilerEngine.XeLaTeX));
        Assert.NotNull(NativeSelectedEntryContract.ValidateEntry("../escape.tex", Profile, NativeCompilerEngine.XeLaTeX));
        Assert.NotNull(NativeSelectedEntryContract.ValidateEntry("main\".tex", Profile, NativeCompilerEngine.XeLaTeX));
    }
    private sealed class ForbiddenList<T> : IReadOnlyList<T>
    {
        public int Count => throw new InvalidOperationException("untrusted inventory was accessed");
        public T this[int index] => throw new InvalidOperationException();
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
