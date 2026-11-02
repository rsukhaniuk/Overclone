using Client.Workspace.Domain;

namespace Client.Editing.Undo;

/// <summary>Exact source UTF16 strings/selections; not durable history bytes.</summary>
public sealed record SourceEditTransaction(FileId FileId, string Before, string After,
    SourceRange BeforeSelection, SourceRange AfterSelection, string Kind, long WorkingGeneration);

public sealed record SourceJournalBudget(int MaxUnits = 100, long MaxRetainedUtf8Bytes = 16L * 1024 * 1024)
{
    public const int HardMaxUnits = 100;
    public const long HardMaxRetainedUtf8Bytes = 16L * 1024 * 1024;
    internal bool Valid => MaxUnits is > 0 and <= HardMaxUnits
        && MaxRetainedUtf8Bytes is > 0 and <= HardMaxRetainedUtf8Bytes;
}
public sealed record SourceJournalObservation(bool Applied, bool CallbackInvoked, bool Tracking);
public sealed record SourceJournalRecordResult(bool Recorded, bool Coalesced, string Notice);
public sealed record SourceJournalActionResult(bool Applied, bool CaretRestored, string Notice);

public sealed record SourceJournalRead(FileId FileId, string ExpectedTextareaText, long Intent);
public sealed record SourceJournalDom(FileId FileId, string Value, SourceRange Selection, long Intent);
public interface ISourceJournalEditor
{
    long Intent { get; }
    bool IsComposing { get; }
    /// <summary>Synchronously cancel competing page selection intents BEFORE capture.</summary>
    bool BeginHistoryAction();
    /// <summary>Actual import/read task, never a cancelled proxy wait.</summary>
    Task<SourceJournalDom?> ReadAsync(SourceJournalRead request);
    /// <summary>Freshly recheck IsCurrent plus actual DOM file/value/caret/epoch,
    /// then synchronously use ordinary EditText, not an exact-patch byte buffer.</summary>
    bool TryApply(SourceJournalMutation mutation);
    /// <summary>Own the actual render/selection task through settlement. Check
    /// IsCurrent immediately before selecting; never rewrite during composition.</summary>
    Task<bool> SelectAsync(SourceJournalSelection selection);
}
public sealed class SourceJournalMutation
{
    private readonly Func<bool> _current;
    internal SourceJournalMutation(SourceToolSnapshot before, SourceJournalDom dom, SourceEdit edit, string kind, Func<bool> current)
    { Before = before; Dom = dom; Edit = edit; Kind = kind; _current = current; }
    public SourceToolSnapshot Before { get; }
    public SourceJournalDom Dom { get; }
    public SourceEdit Edit { get; }
    public string Kind { get; }
    public bool IsCurrent() => _current();
}
public sealed class SourceJournalSelection
{
    private readonly Func<bool> _current;
    internal SourceJournalSelection(FileId fileId, string text, SourceRange selection, long intent, Func<bool> current)
    { FileId = fileId; Text = text; Selection = selection; Intent = intent; _current = current; }
    public FileId FileId { get; }
    public string Text { get; }
    public SourceRange Selection { get; }
    public long Intent { get; }
    public bool IsCurrent() => _current();
}

/// <summary>Opaque recording token. It grants no permission to bypass the root's
/// ordinary edit guard. Internal retained preimages are released on retirement.</summary>
public sealed class SourceEditLease
{
    internal SourceEditLease(FileId fileId, string kind) { FileId = fileId; Kind = kind; }
    public FileId FileId { get; }
    public string Kind { get; }
}
