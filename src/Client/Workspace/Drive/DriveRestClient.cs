using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Client.Workspace.Transfer;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Client.Workspace.Drive;

/// <summary>C# owns fixed-origin REST, authorization headers, budgets and upload policy.
/// No list-all-files shortcut, arbitrary URL, overwrite, retry or background work.</summary>
public sealed class DriveRestClient(HttpClient http, IDriveAuthorization auth) : IDisposable
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);
    public const int ReceiptBudget = 16 * 1024;
    private int _active;
    public event Action? Changed;
    public string UploadNotice { get; private set; } = "No remote upload has been requested.";
    private void RemoteStatus(string notice) { UploadNotice = notice; Changed?.Invoke(); }

    public async Task<byte[]> DownloadAsync(DriveSelection selection, CancellationToken ct)
    {
        if (!selection.Valid) throw new DriveFailure(DriveCode.InvalidSelection);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://www.googleapis.com/drive/v3/files/{selection.Id}?alt=media");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        return await Send(request, ArchiveLimits.ZipInputSize, false, ct);
    }

    public async Task<string> CreateZipAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl) ||
            !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new DriveFailure(DriveCode.InvalidSelection);
        if (bytes.Length == 0 || bytes.Length > ArchiveLimits.ZipInputSize)
            throw new DriveFailure(DriveCode.TooLarge);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id");
        // multipart/related, not form-data. Metadata contains only the proposed
        // ZIP name and media type. Never serialize application/auth state.
        var content = new MultipartContent("related", "drive_" + Guid.NewGuid().ToString("N"));
        content.Add(new StringContent(JsonSerializer.Serialize(new { name, mimeType = "application/zip" }),
            Encoding.UTF8, "application/json"));
        var archive = new ReadOnlyMemoryContent(bytes);
        archive.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(archive);
        request.Content = content;
        try
        {
            var reply = await Send(request, ReceiptBudget, true, ct);
            try
            {
                using var json = JsonDocument.Parse(reply, new JsonDocumentOptions { MaxDepth = 8 });
                if (json.RootElement.ValueKind != JsonValueKind.Object ||
                    !json.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    !DriveSelection.ValidId(id.GetString())) throw new JsonException();
                RemoteStatus("The confirmed upload created a NEW ZIP in Drive. Local save state is separate.");
                return id.GetString()!;
            }
            catch (JsonException) { throw new DriveFailure(DriveCode.Uncertain, true); }
        }
        catch (DriveFailure failure)
        {
            if (failure.Code != DriveCode.Busy)
                RemoteStatus(DriveNotices.For(failure.Uncertain ? DriveCode.Uncertain : failure.Code));
            throw;
        }
    }

    private async Task<byte[]> Send(HttpRequestMessage request, long budget, bool upload, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) throw new DriveFailure(DriveCode.Busy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Deadline);
        var dispatched = false;
        var definitiveRejection = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            request.SetBrowserRequestCache(BrowserRequestCache.NoStore);
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Omit);
            request.SetBrowserRequestMode(BrowserRequestMode.Cors);
            request.SetBrowserRequestOption("redirect", "error");
            request.SetBrowserResponseStreamingEnabled(true);
            auth.Authorize(request); // expiry checked immediately before dispatch
            dispatched = true;
            if (upload) RemoteStatus("A confirmed NEW-file upload is settling. Cancellation cannot guarantee that no file was created.");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                // Never read/echo raw errors, headers or exception messages. An
                // explicit 4xx rejection (except timeout) is a known non-creation.
                definitiveRejection = (int)response.StatusCode is >= 400 and < 500 &&
                    response.StatusCode != HttpStatusCode.RequestTimeout;
                var code = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => DriveCode.Unauthorized,
                    HttpStatusCode.Forbidden => DriveCode.Forbidden,
                    HttpStatusCode.TooManyRequests => DriveCode.RateLimited,
                    _ => DriveCode.Network
                };
                throw new DriveFailure(code, upload && !definitiveRejection);
            }
            if (response.Content.Headers.ContentLength is long length && (length < 0 || length > budget))
                throw new DriveFailure(DriveCode.TooLarge, upload);
            var type = response.Content.Headers.ContentType?.MediaType;
            if (upload ? type != "application/json" :
                type is not ("application/zip" or "application/x-zip-compressed" or "application/octet-stream"))
                throw new DriveFailure(DriveCode.Malformed, upload);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var result = new MemoryStream();
            var buffer = new byte[32 * 1024];
            while (true)
            {
                // At most budget bytes are read, plus one sentinel byte to detect
                // dishonest/absent Content-Length. Never materialize excess bodies.
                var count = (int)Math.Min(buffer.Length, budget - result.Length + 1);
                var read = await stream.ReadAsync(buffer.AsMemory(0, count), deadline.Token);
                if (read == 0) break;
                if (result.Length + read > budget) throw new DriveFailure(DriveCode.TooLarge, upload);
                result.Write(buffer, 0, read);
            }
            deadline.Token.ThrowIfCancellationRequested();
            // Fetch exposes decoded bytes while Content-Length may describe the
            // compressed wire representation. The declared-size bound above and
            // decoded streaming cap are authoritative; do not compare lengths.
            return result.ToArray();
        }
        catch (DriveFailure) { throw; }
        catch (OperationCanceledException)
        { throw new DriveFailure(upload && dispatched ? DriveCode.Uncertain : DriveCode.Cancelled, upload && dispatched); }
        catch (HttpRequestException)
        { throw new DriveFailure(upload && dispatched ? DriveCode.Uncertain : DriveCode.Network, upload && dispatched); }
        catch (IOException)
        { throw new DriveFailure(upload && dispatched ? DriveCode.Uncertain : DriveCode.Network, upload && dispatched); }
        finally
        {
            // Physical request ownership lasts until this method settles, even
            // when a caller changes session or cancels. No WaitAsync detachment.
            request.Headers.Authorization = null;
            Volatile.Write(ref _active, 0);
        }
    }
    public void Dispose() => http.Dispose();
}
