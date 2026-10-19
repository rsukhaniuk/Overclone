using Client.Workspace.Domain;
using Client.Workspace.Transfer;

namespace Client.Workspace.Drive;

public sealed record DriveExportPreview(string ProjectName, ProjectId ProjectId, RevisionId Revision,
    int FileCount, long ZipBytes, string ProposedName);

/// <summary>Panel-owned transfer state, borrowing workspace/transfer/auth ports.
/// Network busy is independent of local save. No current-project replacement,
/// automatic transfer, retry, opening of imports, or compile ownership.</summary>
public sealed class DriveTransferSession : IDisposable
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly IProjectTransfer _transfer;
    private readonly IArchiveExport _export;
    private readonly DriveRestClient _rest;
    private readonly IDriveAuth _auth;
    private CancellationTokenSource? _request;
    private ArchiveExport? _archive;
    private TransferStageResult? _stage;
    private Stamp _observed;
    private Stamp? _previewStamp;
    private long _epoch;
    private bool _disposed, _connecting;
    public event Action? Changed;
    public DriveConfiguration Configuration { get; private set; } = new();
    public bool Busy { get; private set; }
    public string Notice { get; private set; } = DriveNotices.For(DriveCode.Unconfigured);
    public DriveCode AuthStatus => _auth.Status;
    // Remote request ledger belongs to the scoped adapter, not a project view.
    // It survives panel unmount while physical work settles, without old metadata.
    public string RemoteNotice => _rest.UploadNotice;
    public DriveSelection? Selection { get; private set; }
    public DriveExportPreview? ExportPreview { get; private set; }
    public TransferStageResult? ImportPreview => _stage;
    public bool CanAccept => !Busy && _stage is { Valid: true } && _previewStamp == Current();
    public bool CanUpload => !Busy && _archive is not null && ExportPreview is not null &&
        _previewStamp == Current() && _auth.Status == DriveCode.Ready;

    public DriveTransferSession(WorkspaceCoordinator workspace, IProjectTransfer transfer,
        IArchiveExport export, DriveRestClient rest, IDriveAuth auth)
    {
        _workspace = workspace; _transfer = transfer; _export = export; _rest = rest; _auth = auth;
        _observed = Current();
        workspace.WorkspaceChanged += WorkspaceChanged;
        auth.Changed += AuthChanged;
        rest.Changed += Notify;
    }
    public void Configure(DriveConfiguration configuration)
    {
        if (Busy || _disposed) return;
        Disconnect(); Configuration = configuration;
        Notice = DriveNotices.For(configuration.OAuthConfigured ? DriveCode.Disconnected : DriveCode.Unconfigured);
        Notify();
    }
    public async Task ConnectAsync()
    {
        var epoch = _epoch;
        var stamp = Current();
        if (!Begin()) return;
        ClearPreviews(); Selection = null;
        _connecting = true;
        try
        {
            if (!Fresh(epoch, stamp)) return;
            await _auth.ConnectAsync(Configuration);
            if (Fresh(epoch, stamp)) Notice = DriveNotices.For(_auth.Status);
        }
        catch (DriveFailure e) { if (Fresh(epoch, stamp)) Failure(e); }
        finally { _connecting = false; End(); }
    }
    public void Disconnect()
    {
        _epoch++; _request?.Cancel(); ClearPreviews(); Selection = null;
        _auth.Disconnect(); Notice = DriveNotices.For(DriveCode.Disconnected); Notify();
    }
    public void Cancel()
    {
        _epoch++; _request?.Cancel(); _auth.CancelPending(); ClearPreviews();
        Notice = Busy ? "Cancellation requested. Waiting for the physical request to settle; an upload may already exist." : DriveNotices.For(DriveCode.Cancelled);
        Notify();
    }
    public async Task ChooseAsync()
    {
        var epoch = _epoch;
        var stamp = Current();
        if (!Begin()) return;
        ClearPreviews(); Selection = null;
        try
        {
            if (!Fresh(epoch, stamp)) return;
            var selected = await _auth.ChooseZipAsync(Configuration);
            if (!Fresh(epoch, stamp)) return;
            Selection = selected;
            Notice = selected is null ? DriveNotices.For(DriveCode.Cancelled) : "ZIP selected. Download and stage explicitly to review it locally.";
        }
        catch (DriveFailure e) { if (Fresh(epoch, stamp)) Failure(e); }
        finally { End(); }
    }
    public async Task DownloadAndStageAsync()
    {
        if (Selection is not { } selected) return;
        var epoch = _epoch;
        var stamp = Current();
        if (!Begin()) return;
        ClearPreviews();
        try
        {
            if (!Fresh(epoch, stamp)) return;
            var bytes = await _rest.DownloadAsync(selected, _request!.Token);
            if (!Fresh(epoch, stamp)) return;
            var stage = await _transfer.StageZipImportAsync(bytes, selected.Name[..^4], _request.Token);
            if (!Fresh(epoch, stamp)) return;
            _stage = stage; _previewStamp = stamp;
            Notice = stage.Valid ? "Staged locally only. Accept to create a NEW local project; the current draft stays open." :
                "ZIP validation failed. Nothing was published. Review validation errors.";
        }
        catch (DriveFailure e) { if (Fresh(epoch, stamp)) Failure(e); }
        catch (OperationCanceledException) { if (Fresh(epoch, stamp)) Notice = DriveNotices.For(DriveCode.Cancelled); }
        catch (Exception) { if (Fresh(epoch, stamp)) Notice = DriveNotices.For(DriveCode.Malformed); }
        finally { End(); }
    }
    public async Task<ImportPublishResult?> AcceptImportAsync()
    {
        if (!CanAccept || _stage is not { } stage || _previewStamp is not { } stamp) return null;
        var epoch = _epoch;
        if (!Begin()) return null;
        // Consume acceptance once, before the only publish invocation.
        ClearPreviews();
        try
        {
            if (!Fresh(epoch, stamp)) return null;
            var receipt = await _transfer.PublishAsync(stage.Staged, stage.Staged.Name, _request!.Token);
            if (!Fresh(epoch, stamp)) return null;
            Notice = receipt.Published ? "A NEW local project was created. Your current draft remains open; open the import separately when safe." :
                DriveNotices.For(DriveCode.LocalFailure);
            return receipt;
        }
        catch (Exception) { if (Fresh(epoch, stamp)) Notice = DriveNotices.For(DriveCode.LocalFailure); return null; }
        finally { End(); }
    }
    public async Task PrepareExportAsync()
    {
        if (_workspace.Project is not { } project) return;
        var epoch = _epoch;
        var initial = Current();
        if (!Begin()) return;
        ClearPreviews();
        try
        {
            if (!SameDraft(epoch, initial)) return;
            // This is explicitly labelled as a local save in the panel.
            var source = await _workspace.CaptureExportSourceAsync(_request!.Token);
            if (!SameDraft(epoch, initial)) return;
            var captured = Current();
            if (source.Revision.ProjectId != captured.Project || source.Revision.Id != captured.Head)
                throw new DriveFailure(DriveCode.Stale);
            var archive = await _export.BuildZipAsync(source, _request.Token);
            if (!Fresh(epoch, captured)) return;
            if (archive.Bytes.LongLength > ArchiveLimits.ZipInputSize) throw new DriveFailure(DriveCode.TooLarge);
            _archive = archive; _previewStamp = captured;
            var name = new string(project.Name.Where(c => !char.IsControl(c)).Take(240).ToArray()).Trim();
            if (name.Length == 0) name = "project";
            ExportPreview = new(project.Name, project.Id, archive.CapturedRevision, archive.FileCount,
                archive.Bytes.LongLength, name + ".zip");
            Notice = "Local revision captured. Review it, then confirm upload of a NEW Drive file. Later edits invalidate this preview.";
        }
        catch (DriveFailure e) { if (SameDraft(epoch, initial)) Failure(e); }
        catch (OperationCanceledException) { if (SameDraft(epoch, initial)) Notice = DriveNotices.For(DriveCode.Cancelled); }
        catch (Exception) { if (SameDraft(epoch, initial)) Notice = DriveNotices.For(DriveCode.LocalFailure); }
        finally { End(); }
    }
    public async Task UploadNewAsync()
    {
        if (!CanUpload || _archive is not { } archive || ExportPreview is not { } preview || _previewStamp is not { } stamp) return;
        var epoch = _epoch;
        if (!Begin()) return;
        // Consume confirmation even on failure: retry requires prepare/review again.
        ClearPreviews();
        try
        {
            if (!Fresh(epoch, stamp)) return;
            await _rest.CreateZipAsync(preview.ProposedName, archive.Bytes, _request!.Token);
            if (Fresh(epoch, stamp)) Notice = "Upload settled. See remote transfer status; local save state is separate.";
        }
        catch (DriveFailure e)
        {
            // The adapter's separate remote ledger preserves uncertainty even
            // after unmount/session change. Never repaint a new project's view.
            if (Fresh(epoch, stamp)) Failure(e);
        }
        finally { End(); }
    }
    private bool Begin()
    {
        if (_disposed) return false;
        if (Busy) { Notice = DriveNotices.For(DriveCode.Busy); Notify(); return false; }
        Busy = true; _request = new(); Notify();
        if (!_disposed) return true;
        _request.Dispose(); _request = null; Busy = false; return false;
    }
    private void End()
    {
        _request?.Dispose(); _request = null; Busy = false; Notify();
    }
    private void Failure(DriveFailure failure) => Notice = DriveNotices.For(failure.Uncertain ? DriveCode.Uncertain : failure.Code);
    private void ClearPreviews() { _archive = null; _stage = null; ExportPreview = null; _previewStamp = null; }
    private Stamp Current() => new(_workspace.SessionId, _workspace.Project?.Id, _workspace.WorkingGeneration, _workspace.CurrentRevision?.Id);
    private bool Fresh(long epoch, Stamp stamp) => !_disposed && epoch == _epoch && Current() == stamp;
    private bool SameDraft(long epoch, Stamp stamp)
    {
        var now = Current();
        return !_disposed && epoch == _epoch && now.Session == stamp.Session && now.Project == stamp.Project && now.Generation == stamp.Generation;
    }
    private void WorkspaceChanged()
    {
        var now = Current();
        // Saving during capture may advance the head without changing draft identity.
        var draftChanged = now.Session != _observed.Session || now.Project != _observed.Project || now.Generation != _observed.Generation;
        var previewChanged = _previewStamp is not null && _previewStamp != now;
        _observed = now;
        if (!draftChanged && !previewChanged) return;
        _epoch++; _request?.Cancel(); _auth.CancelPending(); ClearPreviews(); Selection = null;
        Notice = DriveNotices.For(DriveCode.Stale); Notify();
    }
    private void AuthChanged()
    {
        if (_auth.Status is not DriveCode.Ready)
        {
            // Initial disconnect and final status of our explicit Connect are
            // expected. Cancel/workspace changes invalidate their own epoch.
            if (!_connecting) { _epoch++; _request?.Cancel(); }
            ClearPreviews(); Selection = null;
        }
        Notify();
    }
    private void Notify() { if (!_disposed) Changed?.Invoke(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _epoch++; _request?.Cancel(); _auth.CancelPending(); ClearPreviews(); Selection = null;
        _workspace.WorkspaceChanged -= WorkspaceChanged; _auth.Changed -= AuthChanged;
        _rest.Changed -= Notify;
        // Borrowed services are not disposed here; active work retains ownership
        // until its finally block, not merely until cancellation/unmount.
    }
    private sealed record Stamp(Guid Session, ProjectId? Project, long Generation, RevisionId? Head);
}
