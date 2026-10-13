using System.Collections;
using Client.Compilation;
using Client.Workspace.Domain;
using Xunit;
using static Workspace.Tests.CompilationTestWorkspace;

namespace Workspace.Tests;

public sealed class CompilationContractsTests
{
    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(-1, 1, 1, 1)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, -1, 1, 1)]
    [InlineData(1, 1, 0, 1)]
    [InlineData(1, 1, -1, 1)]
    [InlineData(1, 1, 1, 0)]
    [InlineData(1, 1, 1, -1)]
    public void Budgets_are_explicit_positive_finite_values(int files, long perFile, long total, int metadata)
        => Assert.False(new CompilationCaptureBudget(files, perFile, total, metadata).IsValid);

    [Fact]
    public void Limits_are_representation_bounds_not_the_analyzer_caps()
    {
        Assert.True(new CompilationCaptureBudget(1, Array.MaxLength, long.MaxValue, Array.MaxLength).IsValid);
        Assert.False(new CompilationCaptureBudget(1, (long)Array.MaxLength + 1, long.MaxValue, 1).IsValid);
        Assert.False(new CompilationCaptureBudget(1, 1, 1, int.MaxValue).IsValid);
    }

    [Fact]
    public void Selection_owns_collections_and_does_not_approve_synthetic_capabilities()
    {
        var main = FileId.New();
        var bundles = new List<CompilationDescriptor> { Descriptor("z-bundle"), Descriptor("a-bundle") };
        var tools = new[] { Descriptor("unknown-future-tool") };
        var options = new[] { new CompilationOption("z-mode", "explicit"), new CompilationOption("a-mode", "") };
        var selection = Select(main, "unselected-future-engine", bundles, tools, options);
        bundles.Clear(); tools[0] = Descriptor("changed"); options[0] = new("changed", "changed");
        Assert.Equal(main, selection.MainFileId);
        Assert.Equal("unselected-future-engine", selection.Engine.Id); // metadata, NOT supported/installed
        Assert.Equal(new[] { "a-bundle", "z-bundle" }, selection.Bundles.Select(b => b.Id));
        Assert.Equal("unknown-future-tool", selection.Tools[0].Id);
        Assert.Equal(new[] { "a-mode", "z-mode" }, selection.Options.Select(o => o.Key));
        Assert.Equal("explicit", selection.Options[1].Value);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("engine")]
    [InlineData("bundles")]
    [InlineData("tools")]
    [InlineData("options")]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("uppercase-hash")]
    [InlineData("surrogate")]
    [InlineData("control")]
    [InlineData("option-key")]
    [InlineData("option-value")]
    [InlineData("duplicate-bundle")]
    [InlineData("duplicate-tool")]
    [InlineData("duplicate-option")]
    public void Invalid_selection_metadata_never_constructs_a_selection(string change)
    {
        var input = new CompilationSelectionInput(FileId.New(), Descriptor(), [], [], []);
        input = change switch
        {
            "main" => input with { MainFileId = default },
            "engine" => input with { Engine = null! },
            "bundles" => input with { Bundles = null! },
            "tools" => input with { Tools = null! },
            "options" => input with { Options = null! },
            "id" => input with { Engine = Descriptor("") },
            "version" => input with { Engine = Descriptor(version: " ") },
            "hash" => input with { Engine = Descriptor(hash: "bad") },
            "uppercase-hash" => input with { Engine = Descriptor(hash: new string('A', 64)) },
            "surrogate" => input with { Engine = Descriptor("\ud800") },
            "control" => input with { Engine = Descriptor("bad\nname") },
            "option-key" => input with { Options = [new("", "value")] },
            "option-value" => input with { Options = [new("key", "\udfff")] },
            "duplicate-bundle" => input with { Bundles = [Descriptor("same"), Descriptor("same", "different")] },
            "duplicate-tool" => input with { Tools = [Descriptor("same"), Descriptor("same")] },
            "duplicate-option" => input with { Options = [new("same", "one"), new("same", "two")] },
            _ => throw new InvalidOperationException()
        };
        var result = CompilationSelection.Create(input, Budget.MaxMetadataBytes);
        Assert.Null(result.Selection); Assert.Equal(CompilationCaptureIssue.InvalidMetadata, result.Failure);
    }

    [Fact]
    public void Selection_metadata_budget_is_exact_and_rejects_before_collection_copy()
    {
        var input = new CompilationSelectionInput(FileId.New(), Descriptor(), [], [], [new("unicode", "Київ🙂")]);
        var first = Assert.IsType<CompilationSelection>(CompilationSelection.Create(input, 10000).Selection);
        Assert.NotNull(CompilationSelection.Create(input, checked((int)first.MetadataByteLength)).Selection);
        Assert.Equal(CompilationCaptureIssue.MetadataLimit, CompilationSelection.Create(input, (int)first.MetadataByteLength - 1).Failure);
        Assert.Equal(CompilationCaptureIssue.InvalidBudget, CompilationSelection.Create(input, 0).Failure);
        Assert.Equal(CompilationCaptureIssue.InvalidBudget, CompilationSelection.Create(input, -1).Failure);
        Assert.Equal(CompilationCaptureIssue.InvalidBudget, CompilationSelection.Create(input, int.MaxValue).Failure);
        var huge = input with { Bundles = new HugeList<CompilationDescriptor>() };
        Assert.Equal(CompilationCaptureIssue.MetadataLimit, CompilationSelection.Create(huge, 1024).Failure);
        huge = input with { Tools = new HugeList<CompilationDescriptor>() };
        Assert.Equal(CompilationCaptureIssue.MetadataLimit, CompilationSelection.Create(huge, 1024).Failure);
        huge = input with { Options = new HugeList<CompilationOption>() };
        Assert.Equal(CompilationCaptureIssue.MetadataLimit, CompilationSelection.Create(huge, 1024).Failure);
        Assert.Equal(CompilationCaptureIssue.MetadataLimit, CompilationSelection.Create(input with { Options = [new("key", new string('x', 100000))] }, 1024).Failure);
    }

    [Fact]
    public void Selection_version_changes_for_A_B_A_same_value_and_clear_without_mutating_prior_stamps()
    {
        var owner = new CompilationSelectionState(); var a = Select(FileId.New()); var b = Select(FileId.New());
        Assert.Null(owner.Current.Selection); Assert.Equal(0, owner.Current.Version);
        owner.SetSelection(a); var first = owner.Current;
        owner.SetSelection(b); owner.SetSelection(a); Assert.Same(a, owner.Current.Selection);
        Assert.Equal(1, first.Version); Assert.Same(a, first.Selection); Assert.Equal(3, owner.Current.Version);
        owner.SetSelection(a); Assert.Equal(4, owner.Current.Version);
        owner.SetSelection(null); Assert.Null(owner.Current.Selection); Assert.Equal(5, owner.Current.Version);
    }

    private sealed class HugeList<T> : IReadOnlyList<T>
    {
        public int Count => int.MaxValue;
        public T this[int index] => throw new InvalidOperationException("must reject count before touching data");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("must not enumerate/copy");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
