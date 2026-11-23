using Client.Review;
using Client.Editing;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Patching;
using Client.Workspace.Transfer;

namespace Client.AI;

/// <summary>One immutable authoritative byte preimage. No editor normalization,
/// implicit save, project traversal or diagnostics. Identity includes generation
/// so A → B → A content edits still invalidate this capture.</summary>
public sealed class AiFileContext
{
    private readonly byte[] _bytes;
    private AiFileContext(TextPatchIdentity identity, FileId fileId, string filename, byte[] bytes)
    {
        Identity = identity; FileId = fileId; Filename = filename; _bytes = bytes.ToArray();
        Hash = HashBytes(_bytes); HasBom = TextContent.HasBom(_bytes); Text = TextContent.DecodeStrict(_bytes);
    }
    public TextPatchIdentity Identity { get; }
    public FileId FileId { get; }
    public string Filename { get; }
    public SHA256 Hash { get; }
    public bool HasBom { get; }
    public string Text { get; }
    public int ByteLength => _bytes.Length;
    public SourceRange? Selection { get; private init; }
    public string DisclosedText => Selection is { } range ? Text[range.Start..range.End] : Text;
    public override string ToString() => "Active-file AI context (content omitted)";

    public bool IsCurrent(WorkspaceCoordinator workspace) => workspace.CurrentTextPatchIdentity == Identity
        && workspace.ActiveFileId == FileId && workspace.ActiveFile is { MediaKind: "text", Deleted: false } file
        && file.RelativePath == Filename;

    public async Task<bool> VerifyAsync(WorkspaceCoordinator workspace, CancellationToken ct)
    {
        if (!IsCurrent(workspace)) return false;
        var bytes = await ReadBoundedAsync(workspace, workspace.ActiveFile!.Clone(), ct);
        ct.ThrowIfCancellationRequested();
        return IsCurrent(workspace) && bytes is not null && HashBytes(bytes) == Hash && bytes.AsSpan().SequenceEqual(_bytes);
    }
    public static async Task<(AiFileContext? Context, AiOutcome Outcome)> CaptureAsync(WorkspaceCoordinator workspace, CancellationToken ct,
        SourceRange? selection = null)
    {
        if (workspace.CurrentTextPatchIdentity is not { } identity || workspace.ActiveFile is not { MediaKind: "text", Deleted: false } file)
            return (null, AiOutcome.Unavailable);
        var frozen = file.Clone();
        var bytes = await ReadBoundedAsync(workspace, frozen, ct);
        ct.ThrowIfCancellationRequested();
        if (workspace.CurrentTextPatchIdentity != identity || workspace.ActiveFileId != frozen.Id
            || workspace.ActiveFile?.RelativePath != frozen.RelativePath) return (null, AiOutcome.Cancelled);
        if (bytes is null) return (null, AiOutcome.TooLarge);
        if (!TextContent.TryDecode(bytes, out var text, out var bom) || bom != frozen.HasBom) return (null, AiOutcome.Malformed);
        if (selection is { } range && (range.Start == range.End || text != frozen.Content || !SourceTools.ValidRange(text, range)))
            return (null, AiOutcome.Malformed);
        if (!frozen.DirtyContent && !frozen.IsNew && frozen.PendingTextBytes is null
            && (bytes.LongLength != frozen.DurableLength || HashBytes(bytes) != frozen.DurableHash)) return (null, AiOutcome.Malformed);
        return (new(identity, frozen.Id, frozen.RelativePath, bytes) { Selection = selection }, AiOutcome.Success);
    }
    private static async Task<byte[]?> ReadBoundedAsync(WorkspaceCoordinator workspace, WorkingFile file, CancellationToken ct)
    {
        long length;
        if (file.PendingTextBytes is { } pending) length = pending.Length;
        else if (file.DirtyContent || file.IsNew && file.ImportedBytes is null)
        {
            // Preflight before normalization/materialization; oversized content is
            // not copied into a hidden truncated context.
            if (!TextContent.TryCountEditedBytes(file.Content, file.HasBom, out length)) return null;
        }
        else if (file.IsNew && file.ImportedBytes is { } imported) length = imported.Length;
        else length = file.DurableLength;
        if (length < 0 || length > AiLimits.ContextBytes) return null;
        var bytes = await workspace.GetFileBytesAsync(file, ct);
        ct.ThrowIfCancellationRequested();
        return bytes.Length <= AiLimits.ContextBytes ? bytes.ToArray() : null;
    }
    private static SHA256 HashBytes(byte[] bytes) => SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes));

    internal TextChangeSet? Propose(string replacementText, string explanation)
    {
        if (Selection is not null) return null;
        return Propose(new(0, Text.Length), replacementText, explanation);
    }

    internal TextChangeSet? Propose(SourceRange range, string replacementText, string explanation)
    {
        if (!SourceTools.ValidRange(Text, range) || !AiLimits.Fits(replacementText, AiLimits.OutputBytes)
            || !AiLimits.Fits(explanation, 4096) || replacementText.StartsWith('\uFEFF')) return null;
        int start = (HasBom ? 3 : 0) + AiLimits.Utf8.GetByteCount(Text.AsSpan(0, range.Start));
        int length = AiLimits.Utf8.GetByteCount(Text.AsSpan(range.Start, range.End - range.Start));
        var replacement = AiLimits.Utf8.GetBytes(replacementText);
        return TextChangeSet.Create(TextPatchOrigin.Ai, explanation,
            [new(FileId, Filename, Hash, _bytes.Length,
                [new(start, start + length, _bytes.AsMemory(start, length), replacement)])]).ChangeSet;
    }
}
