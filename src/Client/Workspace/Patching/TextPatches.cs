using System.Collections.Immutable;
using System.Text;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;

namespace Client.Workspace.Patching;

/// <summary>Existing source-text budgets, shared with analysis. Binary files count
/// towards project size/count, but are never read or patched by this foundation.</summary>
public static class WorkspaceTextLimits
{
    public const long FileBytes = 8L * 1024 * 1024;
    public const long ProjectBytes = 64L * 1024 * 1024;
    public const int FileCount = 5000;
}

public sealed record TextPatchLimits(long FileBytes, long ProjectBytes, int FileCount)
{
    public static TextPatchLimits Default { get; } = new(WorkspaceTextLimits.FileBytes, WorkspaceTextLimits.ProjectBytes, WorkspaceTextLimits.FileCount);
    internal TextPatchLimits Resolve() => new(Math.Min(FileBytes, Default.FileBytes), Math.Min(ProjectBytes, Default.ProjectBytes), Math.Min(FileCount, Default.FileCount));
}

public sealed record TextPatchIdentity(ProjectId ProjectId, Guid SessionId, long WorkingGeneration, RevisionId? ExpectedHead);
public enum TextPatchOrigin { Rule, Ai, User }
public enum TextPatchError { InvalidInput, QuotaExceeded, PreimageMismatch, InvalidUtf8, InvalidSpan, BomChange, Overlap, NoChanges, Stale, ReadOnly, LockLost, RevisionConflict, StorageFailure, Cancelled, Ineligible }
public sealed record TextPatchFailure(TextPatchError Code, string SafeMessage);
public sealed record TextPatchPreparation(PreparedTextBatch? Batch, TextPatchFailure? Failure);
public sealed record TextPatchApplication(bool AppliedUnsaved, TextPatchFailure? Failure);

// UNTRUSTED input DTOs. Their memory/lists may alias caller buffers. Prepare
// validates sizes before copying; nothing from these DTOs survives in the result.
public sealed record TextEditInput(long StartByte, long EndByte, ReadOnlyMemory<byte> Expected, ReadOnlyMemory<byte> Replacement);
public sealed record FilePatchInput(FileId FileId, string RelativePath, SHA256 ExpectedHash, long ExpectedLength, IReadOnlyList<TextEditInput> Edits);
public sealed record PatchSourceInput(FileId FileId, string RelativePath, string MediaKind, long ByteLength, bool HasBom, ReadOnlyMemory<byte>? Bytes = null, bool Deleted = false);

/// <summary>Privately owned bytes; no mutable array or ReadOnlyMemory backing
/// array escapes. Sharing this object on clone is safe. Only the preparer can
/// create pending buffers. ToArray returns a defensive copy.</summary>
public sealed class ExactTextBytes
{
    private readonly byte[] _bytes;
    private ExactTextBytes(byte[] owned)
    {
        _bytes = owned;
        HasBom = TextContent.HasBom(owned);
        Content = TextContent.DecodeStrict(owned);
        Hash = SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(owned));
    }
    public int Length => _bytes.Length;
    public bool HasBom { get; }
    public string Content { get; }
    public SHA256 Hash { get; }
    public ReadOnlySpan<byte> Span => _bytes;
    public byte[] ToArray() => _bytes.ToArray();
    internal static ExactTextBytes OwnPrepared(byte[] owned) => new(owned);
}

public sealed class OwnedPatchBytes
{
    private readonly byte[] _bytes;
    internal OwnedPatchBytes(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();
    public int Length => _bytes.Length;
    public ReadOnlySpan<byte> Span => _bytes;
    public byte[] ToArray() => _bytes.ToArray();
}

public sealed class PreparedTextEdit
{
    internal PreparedTextEdit(long start, long end, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> replacement)
    { StartByte = start; EndByte = end; Expected = new(expected); Replacement = new(replacement); }
    public long StartByte { get; }
    public long EndByte { get; }
    public OwnedPatchBytes Expected { get; }
    public OwnedPatchBytes Replacement { get; }
}

public sealed class PreparedTextFile
{
    internal PreparedTextFile(FileId id, string path, ExactTextBytes before, ExactTextBytes after, ImmutableArray<PreparedTextEdit> edits)
    { FileId = id; RelativePath = path; Before = before; After = after; Edits = edits; }
    public ImmutableArray<PreparedTextEdit> Edits { get; }
    public FileId FileId { get; }
    public string RelativePath { get; }
    public ExactTextBytes Before { get; }
    public ExactTextBytes After { get; }
}

/// <summary>Not publicly constructible/cloneable. Every applicable batch comes
/// through complete pure preparation. The coordinator separately verifies its
/// current live preimages, entire project budget, head and writer ownership.</summary>
public sealed class PreparedTextBatch
{
    private PreparedTextBatch(TextPatchIdentity identity, TextPatchOrigin origin, string explanation, ImmutableArray<PreparedTextFile> files, TextPatchLimits limits)
    { Identity = identity; Origin = origin; Explanation = explanation; Files = files; Limits = limits; }
    public TextPatchIdentity Identity { get; }
    public TextPatchOrigin Origin { get; }
    public string Explanation { get; }
    public ImmutableArray<PreparedTextFile> Files { get; }
    public TextPatchLimits Limits { get; }
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Pure and all-or-nothing. Half-open spans: distinct adjacent edits
    /// are allowed, including insertion at a replacement's END, not its START.
    /// Same-start edits (including two insertions) and insertions inside a range
    /// are ambiguous. No-op edits/files/batches are explicitly rejected.</summary>
    public static TextPatchPreparation Prepare(TextPatchIdentity identity, IReadOnlyList<FilePatchInput> patches,
        IReadOnlyList<PatchSourceInput> project, TextPatchOrigin origin = TextPatchOrigin.User,
        string explanation = "Explicit text patch", TextPatchLimits? requestedLimits = null)
    {
        TextPatchPreparation Fail(TextPatchError code) => new(null, FailureFor(code));
        if (identity is null || identity.ProjectId.Value == Guid.Empty || identity.SessionId == Guid.Empty || identity.WorkingGeneration < 0
            || identity.ExpectedHead?.Value == Guid.Empty || patches is null || project is null || !Enum.IsDefined(origin)
            || string.IsNullOrWhiteSpace(explanation) || explanation.Length > WorkspaceTextLimits.FileBytes)
            return Fail(TextPatchError.InvalidInput);
        var limits = (requestedLimits ?? TextPatchLimits.Default).Resolve();
        if (limits.FileBytes < 0 || limits.ProjectBytes < 0 || limits.FileCount < 0) return Fail(TextPatchError.InvalidInput);
        if (patches.Count == 0) return Fail(TextPatchError.NoChanges);
        if (project.Count > limits.FileCount || patches.Count > project.Count) return Fail(TextPatchError.QuotaExceeded);
        try
        {
            // Metadata and every edit are checked before allocating any output
            // buffer. No encoding/decoding of oversized input/replacements.
            if (!ValidProject(project, limits, out long total)) return Fail(TextPatchError.QuotaExceeded);
            var byId = project.ToDictionary(f => f.FileId);
            var seen = new HashSet<FileId>();
            var plans = new List<(FilePatchInput Patch, PatchSourceInput Source, TextEditInput[] Edits, int Length)>();
            foreach (var patch in patches)
            {
                if (patch is null || !seen.Add(patch.FileId) || !byId.TryGetValue(patch.FileId, out var source)
                    || source.MediaKind != "text" || patch.RelativePath != source.RelativePath || patch.ExpectedLength != source.ByteLength
                    || source.Bytes is not { } bytes || bytes.Length != source.ByteLength || patch.Edits is null
                    || !ValidHash(patch.ExpectedHash)) return Fail(TextPatchError.PreimageMismatch);
                if (patch.Edits.Count == 0) return Fail(TextPatchError.NoChanges);
                // Distinct integer starts in [0,length] impose this derived cap;
                // no arbitrary extra edit-count allowance or unbounded sorting.
                if (patch.Edits.Count > source.ByteLength + 1) return Fail(TextPatchError.Overlap);
                var edits = patch.Edits.ToArray();
                if (edits.Any(e => e is null)) return Fail(TextPatchError.InvalidInput);
                Array.Sort(edits, (a,b) => a.StartByte.CompareTo(b.StartByte));
                long output = source.ByteLength;
                TextEditInput? previous = null;
                foreach (var edit in edits)
                {
                    if (edit.StartByte < 0 || edit.EndByte < edit.StartByte || edit.EndByte > bytes.Length
                        || edit.Expected.Length != edit.EndByte - edit.StartByte) return Fail(TextPatchError.InvalidSpan);
                    if (previous is not null && (edit.StartByte == previous.StartByte || edit.StartByte < previous.EndByte)) return Fail(TextPatchError.Overlap);
                    if (source.HasBom && edit.StartByte < 3) return Fail(TextPatchError.BomChange);
                    if (!Boundary(bytes.Span, edit.StartByte) || !Boundary(bytes.Span, edit.EndByte)) return Fail(TextPatchError.InvalidSpan);
                    if (edit.Replacement.Length > limits.FileBytes) return Fail(TextPatchError.QuotaExceeded);
                    if (!bytes.Span.Slice((int)edit.StartByte, edit.Expected.Length).SequenceEqual(edit.Expected.Span)) return Fail(TextPatchError.PreimageMismatch);
                    if (edit.Expected.Span.SequenceEqual(edit.Replacement.Span)) return Fail(TextPatchError.NoChanges);
                    Utf8.GetCharCount(edit.Replacement.Span); // strict validation without string allocation
                    output = checked(output - edit.Expected.Length + edit.Replacement.Length);
                    previous = edit;
                }
                if (output > limits.FileBytes) return Fail(TextPatchError.QuotaExceeded);
                total = checked(total - source.ByteLength + output);
                plans.Add((patch, source, edits, checked((int)output)));
            }
            if (total > limits.ProjectBytes) return Fail(TextPatchError.QuotaExceeded);
            var prepared = ImmutableArray.CreateBuilder<PreparedTextFile>(plans.Count);
            foreach (var (patch, source, edits, length) in plans)
            {
                // Copy preimage before hashing/deriving output. Result never
                // references caller input arrays or lists, including replacements.
                var before = ExactTextBytes.OwnPrepared(source.Bytes!.Value.ToArray());
                if (before.HasBom != source.HasBom || before.Hash != patch.ExpectedHash) return Fail(TextPatchError.PreimageMismatch);
                var ownedEdits = edits.Select(e => new PreparedTextEdit(e.StartByte, e.EndByte,
                    before.Span.Slice((int)e.StartByte, e.Expected.Length), e.Replacement.Span)).ToImmutableArray();
                var result = new byte[length];
                int read = before.Length, write = length;
                foreach (var edit in ownedEdits.Reverse())
                {
                    int tail = read - (int)edit.EndByte;
                    write -= tail; before.Span.Slice((int)edit.EndByte, tail).CopyTo(result.AsSpan(write));
                    write -= edit.Replacement.Length; edit.Replacement.Span.CopyTo(result.AsSpan(write));
                    read = (int)edit.StartByte;
                }
                before.Span[..read].CopyTo(result);
                var after = ExactTextBytes.OwnPrepared(result);
                if (before.HasBom != after.HasBom) return Fail(TextPatchError.BomChange);
                if (before.Span.SequenceEqual(after.Span)) return Fail(TextPatchError.NoChanges);
                prepared.Add(new(patch.FileId, patch.RelativePath, before, after, ownedEdits));
            }
            return new(new(identity, origin, explanation, prepared.MoveToImmutable(), limits), null);
        }
        catch (DecoderFallbackException) { return Fail(TextPatchError.InvalidUtf8); }
        catch (OverflowException) { return Fail(TextPatchError.QuotaExceeded); }
        catch (ArgumentException) { return Fail(TextPatchError.InvalidInput); }
    }

    internal static bool ValidProject(IReadOnlyList<PatchSourceInput> files, TextPatchLimits limits, out long total)
    {
        total = 0;
        if (files.Count > limits.FileCount) return false;
        var ids = new HashSet<FileId>();
        foreach (var f in files)
        {
            if (f is null || f.Deleted || f.FileId.Value == Guid.Empty || !ids.Add(f.FileId) || f.ByteLength < 0
                || f.MediaKind is not ("text" or "binary") || f.RelativePath != ProjectPathValidator.Normalize(f.RelativePath)
                || (f.MediaKind == "text" && f.ByteLength > limits.FileBytes)) return false;
            total = checked(total + f.ByteLength);
            if (total > limits.ProjectBytes) return false;
        }
        return ProjectTreeValidator.FindPathConflicts(files.Select(f => f.RelativePath)) is not { Count: > 0 };
    }
    private static bool Boundary(ReadOnlySpan<byte> b, long offset) => offset == b.Length || (b[(int)offset] & 0xc0) != 0x80;
    private static bool ValidHash(SHA256 h) => h.Hex is { Length: 64 } && h.Hex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static TextPatchFailure FailureFor(TextPatchError code) => new(code, code switch
    {
        TextPatchError.Cancelled => "The text batch was cancelled; no changes were applied.",
        TextPatchError.Stale => "The workspace changed; prepare a fresh text batch.",
        TextPatchError.LockLost => "This session does not own the project writer lock.",
        TextPatchError.RevisionConflict => "The expected durable project head could not be verified; reopen to reconcile.",
        TextPatchError.StorageFailure => "Source or head verification failed; no changes were applied.",
        TextPatchError.QuotaExceeded => "The text batch exceeds the bounded project limits.",
        _ => "The text batch could not be validated; no changes were applied.",
    });
}
