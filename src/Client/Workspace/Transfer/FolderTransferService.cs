using System.Collections.Immutable;
using System.Text.Json;
using Client.Workspace.Domain;
using Microsoft.JSInterop;

namespace Client.Workspace.Transfer;

/// <summary>
/// Browser directory-handle transport through <c>folder-transfer.js</c> glue.
/// C# owns all path/size/count/depth validation and the staged→publish workflow
/// (shared with <see cref="ZipTransferService"/> via <see cref="StagedImportBuilder"/>);
/// JS only enumerates browser-granted handles, reads bytes and writes bytes.
/// Folder handles stay session-local and are never persisted or reused for
/// writes. Every import goes into a NEW project; every export writes a fresh
/// child directory and never overwrites an existing tree.
/// </summary>
public sealed class FolderTransferService
{
    private readonly IJSRuntime _js;

    public FolderTransferService(IJSRuntime js) => _js = js;

    /// <summary>Feature-detect the directory picker and writable streams.</summary>
    public async Task<FolderCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        var m = await ModuleAsync(ct);
        return await m.InvokeAsync<FolderCapabilities>("folderCapabilities");
    }

    /// <summary>Pick a directory and enumerate it into a staged import preview.
    /// The picker must be invoked from a user gesture before unrelated awaits.
    /// Cancellation/denial returns a recoverable outcome rather than throwing.</summary>
    public async Task<FolderStageResult> StageImportFromFolderAsync(string defaultName, CancellationToken ct)
    {
        IJSObjectReference m;
        IJSObjectReference? handle;
        try
        {
            m = await ModuleAsync(ct);
            handle = await m.InvokeAsync<IJSObjectReference?>("pickDirectory");
        }
        catch (Exception ex)
        {
            return FolderStageResult.Failed(MapPickerError(ex), defaultName);
        }

        var limits = new
        {
            maxEntries = ArchiveLimits.MaxEntries,
            maxDepth = ArchiveLimits.MaxDepth,
            maxSegmentBytes = ArchiveLimits.MaxSegmentBytes,
            singleFile = ArchiveLimits.SingleFile,
        };

        EnumerateResult enumResult;
        try
        {
            enumResult = await m.InvokeAsync<EnumerateResult>("enumerateTree", handle!, limits);
        }
        catch (Exception ex)
        {
            await TryReleaseAsync(m, handle);
            return FolderStageResult.Failed(MapPickerError(ex), defaultName);
        }

        // Read each enumerated file's bytes, then run the shared C# validator
        // (same policy as ZIP). Bytes are read one at a time through the JS
        // transport; the validator enforces limits independently as well.
        var stagedFiles = new List<StagedFile>();
        long expanded = 0;
        var errors = new List<ValidationError>();
        var filePathKeys = new HashSet<string>();
        var directories = new HashSet<string>();

        foreach (var f in enumResult.Files)
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                bytes = await m.InvokeAsync<byte[]>("readFileHandleById", f.HandleId);
            }
            catch (Exception ex)
            {
                errors.Add(new ValidationError(f.Path, "ReadFailed", ClassifyJs(ex)));
                continue;
            }

            if (bytes.LongLength > ArchiveLimits.SingleFile)
            {
                errors.Add(new ValidationError(f.Path, "FileTooLarge", "File exceeds the single-file limit."));
                continue;
            }
            expanded += bytes.LongLength;
            if (expanded > ArchiveLimits.ExpandedSize)
            {
                errors.Add(new ValidationError(f.Path, "ExpandedTooLarge", "Expanded project exceeds the size limit."));
                continue;
            }

            var err = StagedImportBuilder.BuildFile(f.Path, bytes, filePathKeys, directories, out var staged);
            if (err is { } ve)
                errors.Add(new ValidationError(f.Path, ve.Code, ve.Message));
            else
                stagedFiles.Add(staged!);
        }

        await TryReleaseAsync(m, handle);
        // Release any JS-side file handles not consumed by reading (e.g. on
        // validation error before read).
        try { await m.InvokeVoidAsync("releaseHandles", enumResult.Files.Select(f => f.HandleId).ToArray()); } catch { /* best-effort */ }

        var name = string.IsNullOrWhiteSpace(defaultName) ? "Imported project" : defaultName.Trim();
        var stagedProject = new StagedProject(name, stagedFiles.ToImmutableArray(), errors);
        return new FolderStageResult(
            errors.Count == 0,
            stagedProject,
            enumResult.ExcludedTrees ?? new List<string>());
    }

    /// <summary>Pick a parent directory and export an immutable revision's exact
    /// bytes into a fresh child directory. Returns a receipt with the captured
    /// revision; partial output is reported honestly and never marked complete.</summary>
    public async Task<FolderExportResult> ExportToFolderAsync(ExportSource source, CancellationToken ct)
    {
        IJSObjectReference m;
        IJSObjectReference? parent;
        try
        {
            m = await ModuleAsync(ct);
            parent = await m.InvokeAsync<IJSObjectReference?>("pickDirectory");
        }
        catch (Exception ex)
        {
            return FolderExportResult.FailedResult("PickerFailed", ClassifyJs(ex), source.Revision.Id);
        }

        // Read each file's exact bytes (bound by export policy) and build the
        // entry list the JS writer consumes. Bytes are sent to JS as Uint8Array.
        var entries = new List<ExportEntry>(source.Revision.Files.Length);
        long total = 0;
        foreach (var file in source.Revision.Files)
        {
            ct.ThrowIfCancellationRequested();
            total += file.ByteLength;
            if (total > ArchiveLimits.ExpandedSize)
                return FolderExportResult.FailedResult("ExpandedTooLarge",
                    "Export exceeds the project-size limit.", source.Revision.Id);
            var bytes = await source.ReadContent(file.ContentHash, ct);
            if (bytes.Length != file.ByteLength)
                return FolderExportResult.FailedResult("ContentLengthMismatch",
                    $"Content length mismatch for {file.RelativePath}.", source.Revision.Id);
            entries.Add(new ExportEntry(file.RelativePath, bytes));
        }

        ExportWriteResult? write;
        try
        {
            write = await m.InvokeAsync<ExportWriteResult?>("exportToDirectory", parent!, entries);
        }
        catch (Exception ex)
        {
            return FolderExportResult.FailedResult("WriteFailed", ClassifyJs(ex), source.Revision.Id);
        }

        string? note = write!.Complete ? null :
            $"Partial export: {write!.Completed.Count} written, {write!.Failed.Count} failed in '{write!.DirectoryName}'.";
        return new FolderExportResult(
            write!.Complete,
            write!.DirectoryName,
            write!.Completed,
            write!.Failed,
            source.Revision.Id,
            note);
    }

    private async Task<IJSObjectReference> ModuleAsync(CancellationToken ct)
    {
        return await _js.InvokeAsync<IJSObjectReference>("import", "./js/folder-transfer.js");
    }

    private static async Task TryReleaseAsync(IJSObjectReference module, IJSObjectReference? handle)
    {
        if (handle is null) return;
        try { await module.InvokeVoidAsync("releaseHandle", handle); } catch { /* best-effort */ }
        try { await handle.DisposeAsync(); } catch { /* best-effort */ }
    }

    private static string ClassifyJs(Exception ex) => ex.Message ?? ex.GetType().Name;

    private static (string Code, string Message) MapPickerError(Exception ex)
    {
        var msg = ex.Message ?? "";
        if (msg.Contains("AbortError", StringComparison.Ordinal) || msg.Contains("abort", StringComparison.OrdinalIgnoreCase))
            return ("PickerCancelled", "Folder selection was cancelled.");
        if (msg.Contains("PickerUnavailable", StringComparison.Ordinal))
            return ("PickerUnavailable", "Directory picker is unavailable in this browser.");
        if (msg.Contains("NotAllowed", StringComparison.Ordinal) || msg.Contains("Security", StringComparison.Ordinal))
            return ("PermissionDenied", "Folder access was denied.");
        if (msg.Contains("TooManyEntries", StringComparison.Ordinal))
            return ("TooManyEntries", "Too many entries in the selected folder.");
        if (msg.Contains("DepthExceeded", StringComparison.Ordinal))
            return ("DepthExceeded", "Folder depth exceeds the limit.");
        if (msg.Contains("SegmentTooLong", StringComparison.Ordinal))
            return ("SegmentTooLong", "A segment name is too long.");
        if (msg.Contains("FileTooLarge", StringComparison.Ordinal))
            return ("FileTooLarge", "A file exceeds the single-file limit.");
        return ("TransferFailed", msg);
    }
}

public record FolderCapabilities(bool Picker, bool Writable);

public record FolderStageResult(
    bool Valid,
    StagedProject Staged,
    IReadOnlyList<string> ExcludedTrees)
{
    public static FolderStageResult Failed((string Code, string Message) err, string defaultName)
    {
        var staged = new StagedProject(
            string.IsNullOrWhiteSpace(defaultName) ? "Imported project" : defaultName,
            ImmutableArray<StagedFile>.Empty,
            new[] { new ValidationError("", err.Code, err.Message) });
        return new FolderStageResult(false, staged, Array.Empty<string>());
    }
}

public record FolderExportResult(
    bool Complete,
    string? DirectoryName,
    IReadOnlyList<string> Completed,
    IReadOnlyList<ExportFailure> Failed,
    RevisionId CapturedRevision,
    string? PartialNote)
{
    public static FolderExportResult FailedResult(string code, string message, RevisionId captured)
        => new(false, null, Array.Empty<string>(),
            new[] { new ExportFailure("", code, message) }, captured, message);
}

public record ExportFailure(string Path, string Code, string Message);

internal sealed class EnumerateResult
{
    public List<EnumeratedFile> Files { get; set; } = new();
    public List<string>? ExcludedTrees { get; set; }
}

internal sealed class EnumeratedFile
{
    public string Path { get; set; } = "";
    public long HandleId { get; set; }
}

internal sealed class ExportWriteResult
{
    public string DirectoryName { get; set; } = "";
    public List<string> Completed { get; set; } = new();
    public List<ExportFailure> Failed { get; set; } = new();
    public bool Complete { get; set; }
}

// JSRuntime deserializes with JsonSerializerDefaults.Web (camelCase, case-insensitive).
// These DTOs use PascalCase properties that match the JS camelCase keys.
public record ExportEntry(string Path, byte[] Bytes);
