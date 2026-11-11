using System.Text.RegularExpressions;

namespace Client.Workspace.Drive;

public enum DriveCode
{
    Ready, Unconfigured, Offline, Configuration, Popup, Denied, Disconnected, Expired,
    Cancelled, Unauthorized, Forbidden, RateLimited, Network, TooLarge, Malformed,
    InvalidSelection, Busy, Stale, LocalFailure, Uncertain
}

public sealed class DriveFailure(DriveCode code, bool uncertain = false) : Exception("Drive operation did not complete.")
{
    public DriveCode Code { get; } = code;
    public bool Uncertain { get; } = uncertain;
}

// Public identifiers only. Deliberately no credential/key defaults or persistence.
public sealed record DriveConfiguration(string ClientId = "", string PickerApiKey = "", string ProjectNumber = "")
{
    public bool OAuthConfigured => ClientId is { Length: > 0 and <= 256 } &&
        Regex.IsMatch(ClientId, @"\A[A-Za-z0-9._-]+\.apps\.googleusercontent\.com\z");
    public bool PickerConfigured => OAuthConfigured && PickerApiKey is { Length: > 0 and <= 256 } &&
        Regex.IsMatch(PickerApiKey, @"\A[A-Za-z0-9_-]+\z") &&
        ProjectNumber is { Length: > 0 and <= 30 } && Regex.IsMatch(ProjectNumber, @"\A[0-9]+\z");
    public override string ToString() => "Drive public configuration [redacted]";
}

public sealed record DriveSelection(string Id, string Name)
{
    public static bool ValidId(string? id) => id is { Length: > 0 and <= 256 } &&
        Regex.IsMatch(id, @"\A[A-Za-z0-9_-]+\z");
    public bool Valid => ValidId(Id) && Name is { Length: > 0 and <= 256 } &&
        !Name.Any(char.IsControl) && Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}

public interface IDriveAuthorization
{
    DriveCode Status { get; }
    void Authorize(HttpRequestMessage request);
}

public interface IDriveAuth : IDriveAuthorization, IAsyncDisposable
{
    event Action? Changed;
    Task ConnectAsync(DriveConfiguration configuration);
    Task<DriveSelection?> ChooseZipAsync(DriveConfiguration configuration);
    /// <summary>Cancel only pending consent/Picker work; retain an existing valid token when cancelling Picker.</summary>
    void CancelPending();
    void Disconnect();
}

public static class DriveNotices
{
    public static string For(DriveCode code) => code switch
    {
        DriveCode.Ready => "Connected. Transfers require separate confirmation.",
        DriveCode.Unconfigured => "Configure your public OAuth client ID. Local work is unaffected.",
        DriveCode.Configuration => "Check public OAuth/Picker configuration and allowed deployed origin.",
        DriveCode.Offline => "Drive is offline. Local editing and saving remain available.",
        DriveCode.Popup => "Popup could not open or closed. Use Connect again explicitly.",
        DriveCode.Denied => "Consent was denied. Local work is unaffected.",
        DriveCode.Disconnected => "Disconnected. The application has cleared its token from memory.",
        DriveCode.Expired => "Access expired. Reconnect explicitly before another transfer.",
        DriveCode.Unauthorized => "Drive rejected authorization. Disconnect and reconnect explicitly.",
        DriveCode.Forbidden => "Drive denied file access. Select an authorized ZIP using Picker.",
        DriveCode.RateLimited => "Drive quota/rate limit reached. No automatic retry was made.",
        DriveCode.TooLarge => "Transfer exceeds the configured byte budget.",
        DriveCode.Malformed => "Drive returned an unsupported or malformed response.",
        DriveCode.InvalidSelection => "Choose a ZIP using Google Picker; links and arbitrary URLs are not accepted.",
        DriveCode.Cancelled => "Cancelled. No automatic retry was made.",
        DriveCode.Stale => "Project/session changed. Review and prepare again.",
        DriveCode.Busy => "A request is still settling. Wait before another transfer.",
        DriveCode.LocalFailure => "Local preparation/publication failed. Your current draft was not replaced.",
        DriveCode.Uncertain => "Remote outcome uncertain. Check Drive before any explicit retry; a new file may already exist.",
        _ => "Drive request failed. Check connectivity and deployed-origin CORS; no proxy or automatic retry is used."
    };
}
