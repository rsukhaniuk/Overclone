using System.Collections.Immutable;
using System.IO;
using System.Text;
using Client.Workspace;
using Client.Workspace.Domain;
using Client.Workspace.Transfer;
using Microsoft.JSInterop;

namespace Client.Analysis;

// Immutable in-memory analysis-snapshot capture (M2a-2a). Constructed with the
// existing WorkspaceCoordinator; subscribes ONLY to WorkspaceChanged to bump a
// LOCAL monotonically increasing workspace epoch (C# orchestration metadata,
// NOT a competing Rust revision id). Capture runs TWO synchronous passes before
// any await:
//   pass1 — enumerate active metadata/text strings and check counts and source
//          byte lengths/totals, computing edited-byte lengths with strict
//          Unicode/newline/BOM semantics WITHOUT encoding or cloning source
//          buffers; reject on per-file/total/count quota or a lone surrogate
//          (InvalidInput, no replacement — the strict encoder does not replace);
//   pass2 — only after ALL preflight checks pass, clone the eligible text
//          entries and own the necessary imported buffers.
// Bytes are then materialized from FROZEN clones via GetFileBytesAsync(clone, ct),
// never the live Files. Binary assets remain metadata-only (never read/hashed or
// deep-copied). Immediately after each awaited read, CURRENT identity is
// rechecked BEFORE any validation/storage failure: a stale read from a
// closed/switched/restored project resolves Superseded (or Cancelled when the
// request token is cancelled), never Unavailable from the old project. The
// capture is rechecked at final publication too. Failure policy is
// all-or-unavailable: corrupt durable / non-UTF8 / BOM or length mismatch is
// Unavailable with the affected file metadata (no empty substitution); malformed
// new/edited UTF-16 is InvalidInput. Capture never calls SaveAsync/
// FlushUntilCleanAsync, never takes the writer lock, works on a dirty/unsaved
// snapshot in a read-only tab, and leaves SavePhase/LastError/IsDirty/head
// unchanged.

public sealed class AnalysisSnapshotFactory : IAsyncDisposable
{
    private readonly WorkspaceCoordinator _coordinator;
    private readonly Func<AnalysisSelection>? _currentSelection;
    private readonly Func<long>? _selectionVersion;
    private long _epoch;
    private bool _disposed;

    public AnalysisSnapshotFactory(WorkspaceCoordinator coordinator, Func<AnalysisSelection>? currentSelection = null, Func<long>? selectionVersion = null)
    {
        _coordinator = coordinator;
        _currentSelection = currentSelection;
        _selectionVersion = selectionVersion;
        _coordinator.WorkspaceChanged += OnWorkspaceChanged;
    }

    /// <summary>The local workspace epoch, bumped on every WorkspaceChanged
    /// raise. Captured in snapshot identity and rechecked after awaits so a
    /// restore/recovery that replaces contents (without moving
    /// WorkingGeneration) invalidates a stale capture.</summary>
    public long CurrentWorkspaceEpoch => Interlocked.Read(ref _epoch);

    private void OnWorkspaceChanged() => Interlocked.Increment(ref _epoch);

    /// <summary>Capture an immutable analysis snapshot. The capture runs two
    /// synchronous preflight passes before the first await; an unavailable or
    /// corrupt text file fails the whole capture (Unavailable/InvalidInput) with
    /// the affected file metadata and NO empty substitution.</summary>
    public Task<CaptureResult> CaptureAsync(
        AnalysisProfile? profile,
        CancellationToken ct,
        AnalysisLimitsDto? requestLimits = null)
        => CaptureSelectionAsync(new(profile ?? AnalysisProfile.EngineStructural(), null), ct, requestLimits);

    public async Task<CaptureResult> CaptureSelectionAsync(
        AnalysisSelection selection, CancellationToken ct, AnalysisLimitsDto? requestLimits = null, bool awaitActualReads = false)
    {
        // Selection is an immutable value captured BEFORE the first await.
        // ---- synchronous, BEFORE the first await ----
        if (_disposed)
            return new CaptureResult.Disposed();
        if (ct.IsCancellationRequested)
            return new CaptureResult.Cancelled();
        if (_coordinator.Project is not { } project)
            return new CaptureResult.NoProject();

        var profileVal = selection.Profile;
        if (!selection.IsValid)
            return new CaptureResult.InvalidInput(
                "profile id/version must be nonempty strict-UTF-8 <=128 bytes", Array.Empty<ExcludedFile>());
        if (AnalysisCatalog.IsThesis(profileVal) && !ThesisWireValidation.ValidLimits(requestLimits))
            return new CaptureResult.InvalidInput("Thesis source limits must be positive and within the hard bounds.", Array.Empty<ExcludedFile>());
        if (!LimitsAreNonnegative(requestLimits))
            return new CaptureResult.InvalidInput(
                "limits must be nonnegative", Array.Empty<ExcludedFile>());

        var limits = AnalysisLimits.Resolve(requestLimits);
        long epochAtCapture = CurrentWorkspaceEpoch;
        Guid sessionAtCapture = _coordinator.SessionId;
        var projectId = project.Id;
        long generationAtCapture = _coordinator.WorkingGeneration;
        if (AnalysisCatalog.IsThesis(profileVal) && generationAtCapture is < 0 or > 9007199254740991)
            return new CaptureResult.InvalidInput("Thesis source generation is outside the supported range.", Array.Empty<ExcludedFile>());
        var baseHead = _coordinator.CurrentRevision?.Id;
        long selectionVersion = _selectionVersion?.Invoke() ?? 0;
        WorkingFile? rootAtCapture = null;
        string? rootPath = null;
        bool Superseded() => IsSuperseded(sessionAtCapture, projectId, generationAtCapture, epochAtCapture)
            || _coordinator.CurrentRevision?.Id != baseHead
            || (_currentSelection is not null && _currentSelection() != selection)
            || (_selectionVersion is not null && _selectionVersion() != selectionVersion)
            || (AnalysisCatalog.RequiresRoot(profileVal) && (rootAtCapture is null || rootAtCapture.Deleted
                || rootAtCapture.MediaKind != "text" || rootAtCapture.RelativePath != rootPath));

        if (AnalysisCatalog.RequiresRoot(profileVal))
        {
            if (_coordinator.Files.Count(f => !f.Deleted) > limits.MaxFiles)
                return new CaptureResult.QuotaExceeded("file count exceeded the project file limit", Array.Empty<ExcludedFile>());
            var live = _coordinator.Files.Where(f => !f.Deleted).ToArray();
            var root = live.Where(f => f.Id.ToString() == selection.ReferenceConfiguration!.RootFileId).ToArray();
            if (root.Length != 1 || !ReferenceChecks.CanBeRoot(root[0])
                || live.Any(f => !ReferenceChecks.IsCanonicalPath(f.RelativePath))
                || live.Select(f => f.RelativePath).Distinct(StringComparer.Ordinal).Count() != live.Length
                || live.Select(f => f.Id).Distinct().Count() != live.Length)
                return new CaptureResult.InvalidInput("Select a current project-local .tex text root; paths and IDs must be canonical and unique.", Array.Empty<ExcludedFile>());
            rootAtCapture = root[0]; rootPath = rootAtCapture.RelativePath;
        }

        // ---- pass1: preflight counts and byte lengths WITHOUT cloning/copying ----
        var excluded = new List<ExcludedFile>();
        var pending = new List<(WorkingFile Live, long DeclaredLength)>();
        long totalDeclared = 0;
        int activeCount = 0;
        foreach (var wf in _coordinator.Files)
        {
            if (wf.Deleted) continue;
            activeCount++;
            if (wf.MediaKind == "binary")
            {
                // Metadata-only; NEVER read/hash/deep-copy binary content.
                excluded.Add(new ExcludedFile(wf.Id, wf.RelativePath, "binary", "binary-file"));
                continue;
            }

            long declared;
            if (wf.PendingTextBytes is { } exact)
            {
                declared = exact.Length;
            }
            else if (IsUneditedDurable(wf))
            {
                declared = wf.DurableLength;
            }
            else if (wf.IsNew && !wf.DirtyContent && wf.ImportedBytes is byte[] ib)
            {
                declared = ib.LongLength;
            }
            else if (!TryCountEditedBytes(wf.Content, wf.HasBom, out declared))
            {
                // Lone surrogate in edited/new text: the strict encoder THROWS (it
                // does not replace), so this is InvalidInput, not one byte.
                return new CaptureResult.InvalidInput(
                    "a text file contains malformed UTF-16 (lone surrogate)",
                    new[] { ExcludedFor(wf, "invalid-utf16") });
            }

            if (declared > limits.MaxBytesPerFile)
                return new CaptureResult.QuotaExceeded(
                    "a file exceeded the per-file byte limit", new[] { ExcludedFor(wf, "oversized") });
            totalDeclared = checked(totalDeclared + declared);
            pending.Add((wf, declared));
        }

        if (activeCount > limits.MaxFiles)
            return new CaptureResult.QuotaExceeded(
                "file count exceeded the project file limit", excluded);
        if (totalDeclared > limits.MaxTotalBytes)
            return new CaptureResult.QuotaExceeded(
                "total bytes exceeded the project byte limit", excluded);

        // ---- pass2: ALL preflight passed — clone text entries and own imported buffers ----
        var frozen = new List<FrozenText>(pending.Count);
        foreach (var (wf, declared) in pending)
        {
            var clone = wf.Clone();
            // Own the imported buffer so a later mutation of the caller's array
            // (or a workspace command clearing the live ImportedBytes) cannot
            // affect this capture's bytes.
            if (clone.ImportedBytes is byte[] ib)
                clone.ImportedBytes = ib.ToArray();
            frozen.Add(new FrozenText(clone, declared));
        }

        // ---- async materialization from frozen clones ----
        var captured = new CapturedFile[frozen.Count];
        long totalActual = 0;
        for (int i = 0; i < frozen.Count; i++)
        {
            var ft = frozen[i];
            byte[] bytes;
            try
            {
                ct.ThrowIfCancellationRequested();
                // Review owns an admitted mutation/recovery workflow and must
                // retain admission through the actual read. Opt-in thesis1.5
                // does too; older ordinary analysis keeps its default semantics.
                var read = _coordinator.GetFileBytesAsync(ft.Source, ct);
                bytes = awaitActualReads || AnalysisCatalog.IsThesis(profileVal) ? await read : await read.WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return new CaptureResult.Cancelled();
            }
            catch (EncoderFallbackException)
            {
                // Malformed UTF-16 in a new/edited source: the strict
                // ENCODER (TextContent.EncodeEdited -> UTF8.GetBytes) throws
                // on a lone surrogate (it does NOT replace it). Preflight
                // rejects lone surrogates; reach here only defensively. A
                // disposed/cancelled/stale project still resolves first.
                if (_disposed) return new CaptureResult.Disposed();
                if (ct.IsCancellationRequested) return new CaptureResult.Cancelled();
                if (Superseded())
                    return new CaptureResult.Superseded();
                return new CaptureResult.InvalidInput(
                    "a text file contains malformed UTF-16",
                    new[] { ExcludedFor(ft.Source, "invalid-utf16") });
            }
            catch (IOException) // covers FileNotFoundException
            {
                return ResolveStorageFailure(ft.Source);
            }
            catch (UnauthorizedAccessException)
            {
                return ResolveStorageFailure(ft.Source);
            }
            catch (Microsoft.JSInterop.JSException)
            {
                // JS interop storage failure (OPFS/IndexedDB read).
                return ResolveStorageFailure(ft.Source);
            }

            // AFTER the awaited read, recheck disposal/cancellation and
            // CURRENT identity BEFORE any validation/storage failure: a
            // close/switch/restore (which replaces content without moving
            // WorkingGeneration) bumps the local epoch, and an in-flight
            // disposal must resolve Disposed, never a fresh snapshot.
            if (_disposed) return new CaptureResult.Disposed();
            if (ct.IsCancellationRequested) return new CaptureResult.Cancelled();
            if (Superseded())
                return new CaptureResult.Superseded();

            // Actual returned byte length and aggregate caps BEFORE any
            // UTF-8 decode/hash/immutable copy: a repo returning a much
            // larger buffer than the manifest must not trigger a full
            // decode/hash/copy.
            long actual = bytes.LongLength;
            if (actual > limits.MaxBytesPerFile)
                return new CaptureResult.QuotaExceeded(
                    "actual bytes exceeded the per-file limit", new[] { ExcludedFor(ft.Source, "oversized") });
            totalActual = checked(totalActual + actual);
            if (totalActual > limits.MaxTotalBytes)
                return new CaptureResult.QuotaExceeded(
                    "total bytes exceeded the project byte limit", new[] { ExcludedFor(ft.Source, "oversized") });

            var cf = ValidateAndFreeze(ft, bytes);
            if (cf is null)
            {
                return new CaptureResult.Unavailable(
                    "a text file's stored bytes failed verification",
                    new[] { ExcludedFor(ft.Source, "corrupt") });
            }
            captured[i] = cf;
        }

        // Final recheck before publication.
        if (_disposed) return new CaptureResult.Disposed();
        if (ct.IsCancellationRequested) return new CaptureResult.Cancelled();
        if (Superseded())
            return new CaptureResult.Superseded();

        // A narrowly-caught expected storage failure resolves to Disposed /
        // Cancelled / Superseded before Unavailable (never leaks raw
        // storage exception text, never substitutes empty bytes).
        CaptureResult ResolveStorageFailure(WorkingFile f)
            => _disposed ? new CaptureResult.Disposed()
            : ct.IsCancellationRequested ? new CaptureResult.Cancelled()
            : Superseded()
                ? new CaptureResult.Superseded()
            : new CaptureResult.Unavailable(
                "a text file's source bytes could not be read",
                new[] { ExcludedFor(f, "unread") });

        var snapshot = new AnalysisSnapshot(
            Id: AnalysisSnapshotId.New(),
            ProjectId: projectId,
            SessionId: sessionAtCapture,
            WorkingGeneration: generationAtCapture,
            LocalEpoch: epochAtCapture,
            BaseHeadRevisionId: baseHead,
            Profile: profileVal,
            Limits: limits,
            Files: ImmutableArray.CreateRange(captured.Where(c => c is not null).Cast<CapturedFile>()),
            Excluded: ImmutableArray.CreateRange(excluded),
            Configuration: selection.Configuration,
            SelectionVersion: selectionVersion,
            ReferenceConfiguration: selection.ReferenceConfiguration);

        return new CaptureResult.Captured(snapshot);
    }

    // ---- validation / freezing ----

    private sealed record FrozenText(WorkingFile Source, long DeclaredLength);

    private static CapturedFile? ValidateAndFreeze(FrozenText ft, byte[] bytes)
    {
        var clone = ft.Source;
        // Check actual length against declared/durable metadata BEFORE any
        // UTF-8 decode/hash/immutable copy: a repo returning a much
        // larger buffer than the manifest must not trigger a full
        // decode/hash/copy.
        if (IsUneditedDurable(clone))
        {
            // Healthy unchanged durable: actual length must match frozen
            // metadata (catches corrupt-but-valid-UTF8 storage).
            if (bytes.LongLength != clone.DurableLength) return null;
        }
        else
        {
            // New/edited source: actual canonical encoding length must match
            // the preflight computed length.
            if (bytes.LongLength != ft.DeclaredLength) return null;
        }

        // Strict UTF-8 + BOM consistency (within the already-checked cap).
        if (!TextContent.TryDecode(bytes, out _, out var actualBom))
            return null;
        if (actualBom != clone.HasBom)
            return null;
        if (clone.PendingTextBytes is { } exact && (exact.Content != clone.Content || exact.HasBom != clone.HasBom
            || !exact.Span.SequenceEqual(bytes))) return null;

        var hash = Client.Workspace.Domain.SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes));
        if (IsUneditedDurable(clone) && hash != clone.DurableHash)
            return null;

        // ONE owned immutable copy. ImmutableArray.Create(byte[]) copies the
        // source array into a fresh immutable backing store, so an unedited
        // durable (a shared mutable repository array) is owned once, and a
        // new/edited source (an already-fresh owned array from pass2) is
        // copied once. Either way the captured bytes do not alias a mutable
        // repo array (no ToArray()+Create double copy).
        ImmutableArray<byte> owned = ImmutableArray.Create(bytes);
        return new CapturedFile(
            clone.Id, clone.RelativePath, clone.HasBom, bytes.LongLength, hash, owned);
    }

    private static bool IsUneditedDurable(WorkingFile f)
        => f.PendingTextBytes is null && !f.IsNew && !f.DirtyContent && f.DurableHash is not null;

    private static ExcludedFile ExcludedFor(WorkingFile f, string reason)
        => new(f.Id, f.RelativePath, f.MediaKind, reason);

    private bool IsSuperseded(Guid session, ProjectId project, long generation, long epoch)
    {
        // Read CURRENT state (not the captured copy). A close during the await
        // (Project now null) is a mismatch => Superseded.
        if (_coordinator.Project is not { } p || p.Id != project)
            return true;
        return _coordinator.SessionId != session
            || _coordinator.WorkingGeneration != generation
            || CurrentWorkspaceEpoch != epoch;
    }

    private static bool LimitsAreNonnegative(AnalysisLimitsDto? lim)
        => lim is null
            || (lim.MaxBytesPerFile is null or >= 0)
            && (lim.MaxFiles is null or >= 0)
            && (lim.MaxTotalBytes is null or >= 0)
            && (lim.MaxResultBytes is null or >= 0)
            && (lim.MaxTokensPerFile is null or >= 0)
            && (lim.MaxTokensPerProject is null or >= 0)
            && (lim.MaxDiagnosticsPerProject is null or >= 0)
            && (lim.MaxBraceDepth is null or >= 0);

    /// <summary>Bounded byte-count pass matching EncodeEdited (CRLF->LF,
    /// lone CR->LF, optional BOM) WITHOUT allocating the encoded byte array.
    /// Returns false on a lone surrogate: the strict encoder does NOT replace
    /// it, it throws, so the caller reports InvalidInput.</summary>
    private static bool TryCountEditedBytes(string content, bool hasBom, out long length)
        => TextContent.TryCountEditedBytes(content, hasBom, out length);

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _coordinator.WorkspaceChanged -= OnWorkspaceChanged;
        }
        await ValueTask.CompletedTask;
    }
}
