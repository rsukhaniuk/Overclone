using System.Collections.Immutable;
using Client.Workspace.Domain;

namespace Client.Compilation;

/// <summary>Validated, privately owned selection metadata, NOT a capability
/// approval or provisioning receipt. No default engine/main/asset is inferred.</summary>
public sealed class CompilationSelection
{
    private CompilationSelection(FileId main, CompilationDescriptor engine, ImmutableArray<CompilationDescriptor> bundles,
        ImmutableArray<CompilationDescriptor> tools, ImmutableArray<CompilationOption> options, long metadataBytes)
    { MainFileId = main; Engine = engine; Bundles = bundles; Tools = tools; Options = options; MetadataByteLength = metadataBytes; }
    public FileId MainFileId { get; }
    public CompilationDescriptor Engine { get; }
    public ImmutableArray<CompilationDescriptor> Bundles { get; }
    public ImmutableArray<CompilationDescriptor> Tools { get; }
    public ImmutableArray<CompilationOption> Options { get; }
    public long MetadataByteLength { get; }

    public static CompilationSelectionCreation Create(CompilationSelectionInput input, int maxMetadataBytes)
    {
        if (maxMetadataBytes <= 0 || maxMetadataBytes > Array.MaxLength)
            return new(null, CompilationCaptureIssue.InvalidBudget);
        if (input is null || input.Engine is null || input.Bundles is null || input.Tools is null || input.Options is null)
            return new(null, CompilationCaptureIssue.InvalidMetadata);
        try
        {
            using var measure = new CompilationCanonical(maxMetadataBytes);
            measure.Selection(input.MainFileId, input.Engine, input.Bundles, input.Tools, input.Options);
            // After complete bounded validation, own the input lists. Elements
            // are immutable value records containing strings/IDs/hashes only.
            var bundles = input.Bundles.OrderBy(d => d.Id, StringComparer.Ordinal).ToImmutableArray();
            var tools = input.Tools.OrderBy(d => d.Id, StringComparer.Ordinal).ToImmutableArray();
            var options = input.Options.OrderBy(o => o.Key, StringComparer.Ordinal).ToImmutableArray();
            return new(new(input.MainFileId, input.Engine, bundles, tools, options, measure.Length), null);
        }
        catch (CompilationInputException error) { return new(null, error.Issue); }
    }
}

public sealed record CompilationSelectionStamp(CompilationSelection? Selection, long Version);

/// <summary>Session-local selection owner. Every explicit set/clear advances
/// version, including A→B→A or reselecting the same value. Not UI/persistence.</summary>
public sealed class CompilationSelectionState
{
    private readonly object _gate = new();
    private CompilationSelectionStamp _current = new(null, 0);
    public CompilationSelectionStamp Current { get { lock (_gate) return _current; } }
    public void SetSelection(CompilationSelection? selection)
    {
        lock (_gate) _current = new(selection, checked(_current.Version + 1));
    }
}
