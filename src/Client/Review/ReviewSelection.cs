using System.Collections.Immutable;
using Client.Workspace.Domain;
using Client.Workspace.Patching;

namespace Client.Review;

/// <summary>Index in the ORIGINAL proposal's Edits, not a line-diff index.</summary>
public readonly record struct ReviewSelectedEdit(FileId FileId, int EditIndex);

/// <summary>Owned bounded choices, never authorization. Invalid input is retained
/// as an invalid empty selection so admission can return a safe ReviewFailure.</summary>
public sealed class ReviewSubsetSelection
{
    public const int MaxSelectedEdits = (int)((TextChangeSet.MaxMetadataBytes - 16) / (16 + sizeof(int)));
    public ReviewSubsetSelection(Guid proposalId, IReadOnlyList<ReviewSelectedEdit> edits)
    {
        ProposalId = proposalId;
        if (proposalId == Guid.Empty || edits is null || edits.Count is <= 0 or > MaxSelectedEdits) return;
        var seen = new HashSet<ReviewSelectedEdit>();
        var owned = ImmutableArray.CreateBuilder<ReviewSelectedEdit>(edits.Count);
        for (int i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            if (edit.FileId.Value == Guid.Empty || edit.EditIndex < 0 || !seen.Add(edit)) return;
            owned.Add(edit);
        }
        Edits = owned.ToImmutable();
        IsValid = true;
    }
    public Guid ProposalId { get; }
    public ImmutableArray<ReviewSelectedEdit> Edits { get; } = [];
    public bool IsValid { get; }

    internal ChangeSetCreation CreateSubset(TextChangeSet original)
    {
        ChangeSetCreation Bad() => new(null, new(ReviewCode.InvalidInput, "Select exact edits from the current User/AI proposal."));
        if (!IsValid || ProposalId != original.Id || original.Origin is not (TextPatchOrigin.User or TextPatchOrigin.Ai)) return Bad();
        var byId = original.Files.ToDictionary(f => f.FileId);
        foreach (var edit in Edits)
            if (!byId.TryGetValue(edit.FileId, out var file) || edit.EditIndex >= file.Edits.Length) return Bad();
        // A subset of an already-owned bounded proposal cannot increase any
        // payload/metadata budget. Validate every key before copying ANY bytes.
        var selected = Edits.ToHashSet();
        const string explanation = "Explicit selected edits from the current proposal";
        long metadata = System.Text.Encoding.UTF8.GetByteCount(explanation);
        foreach (var file in original.Files)
        {
            int count = 0;
            for (int i = 0; i < file.Edits.Length; i++) if (selected.Contains(new(file.FileId, i))) count++;
            if (count == 0) continue;
            metadata += 16 + 64 + sizeof(long) + System.Text.Encoding.UTF8.GetByteCount(file.RelativePath)
                + (long)count * (2 * sizeof(long) + 2 * sizeof(int));
            if (metadata > TextChangeSet.MaxMetadataBytes) return Bad();
        }
        var inputs = new List<FilePatchInput>();
        foreach (var file in original.Files)
        {
            var edits = new List<TextEditInput>();
            for (int i = 0; i < file.Edits.Length; i++)
            {
                if (!selected.Contains(new(file.FileId, i))) continue;
                var e = file.Edits[i];
                edits.Add(new(e.StartByte, e.EndByte, e.Expected.ToArray(), e.Replacement.ToArray()));
            }
            if (edits.Count > 0) inputs.Add(new(file.FileId, file.RelativePath, file.ExpectedHash, file.ExpectedLength, edits));
        }
        // Original file/edit ordering and origin survive regardless of checkbox
        // selection order. Canonical byte/span validation remains in Prepare.
        return TextChangeSet.Create(original.Origin, explanation, inputs);
    }
}

public sealed record ReviewSubsetRequest(Guid ExpectedPreviewId, ReviewSubsetSelection Selection);
