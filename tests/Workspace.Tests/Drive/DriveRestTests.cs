using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Client.Workspace.Drive;
using Client.Workspace.Transfer;
using Xunit;

namespace Workspace.Tests.Drive;

public sealed class DriveRestTests
{
    [Fact]
    public async Task ExplicitDownload_FixedUriBearerNoStoreOmitAndStreaming()
    {
        var auth = new Auth();
        var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://www.googleapis.com/drive/v3/files/selected_id?alt=media", request.RequestUri!.AbsoluteUri);
            Assert.Equal("synthetic", request.Headers.Authorization?.Parameter);
            Assert.DoesNotContain("synthetic", request.RequestUri.AbsoluteUri);
            Assert.False(request.Headers.Contains("Cache-Control"));
            var options = request.Options.SelectMany(pair =>
                pair.Value as IEnumerable<KeyValuePair<string, object>> ?? []).ToDictionary(pair => pair.Key, pair => pair.Value);
            Assert.Equal("no-store", options["cache"]);
            Assert.Equal("omit", options["credentials"]);
            Assert.Equal("cors", options["mode"]);
            Assert.Equal("error", options["redirect"]);
            Assert.Contains(request.Options, pair => pair.Key.Contains("StreamingResponse") && Equals(pair.Value, true));
            return Response([1, 2, 3]);
        });
        using var rest = new DriveRestClient(new HttpClient(handler), auth);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(new byte[] { 1, 2, 3 }, await rest.DownloadAsync(new("selected_id", "x.zip"), default));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, auth.Authorizations);
    }

    [Theory]
    [InlineData(DriveCode.Disconnected)]
    [InlineData(DriveCode.Expired)]
    [InlineData(DriveCode.Denied)]
    public async Task AuthFailureDispatchesNothing(DriveCode code)
    {
        var handler = new Handler(_ => Task.FromException<HttpResponseMessage>(new InvalidOperationException()));
        using var rest = new DriveRestClient(new HttpClient(handler), new Auth { Status = code });
        Assert.Equal(code, (await Assert.ThrowsAsync<DriveFailure>(() => rest.DownloadAsync(new("id", "x.zip"), default))).Code);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("../bad", "x.zip")]
    [InlineData("https://www.googleapis.com/drive/v3/files/id", "x.zip")]
    [InlineData("id?access_token=bad", "x.zip")]
    [InlineData("id\n", "x.zip")]
    [InlineData("id", "x.pdf")]
    [InlineData("id", "bad\n.zip")]
    public async Task InvalidSelectionDispatchesNothing(string id, string name)
    {
        var handler = new Handler(_ => Task.FromException<HttpResponseMessage>(new InvalidOperationException()));
        using var rest = new DriveRestClient(new HttpClient(handler), new Auth());
        Assert.Equal(DriveCode.InvalidSelection,
            (await Assert.ThrowsAsync<DriveFailure>(() => rest.DownloadAsync(new(id, name), default))).Code);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("{\"id\":\"first\",\"id\":\"second\"}")]
    [InlineData("{\"id\":\"first\",\"i\\u0064\":\"second\"}")]
    [InlineData("{\"id\":\"valid\",\"ignored\":{\"value\":1,\"value\":2}}")]
    [InlineData("{\"id\":\"valid\\n\"}")]
    [InlineData("{\"id\":\"\\ud800\"}")]
    public async Task AmbiguousOrInvalidUploadReceiptRemainsUncertainWithoutRetry(string json)
    {
        var handler = new Handler(_ => Response(Encoding.UTF8.GetBytes(json), "application/json"));
        using var rest = new DriveRestClient(new HttpClient(handler), new Auth());
        var failure = await Assert.ThrowsAsync<DriveFailure>(() => rest.CreateZipAsync("synthetic.zip", new byte[] { 1 }, default));
        Assert.Equal(DriveCode.Uncertain, failure.Code);
        Assert.True(failure.Uncertain);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("Check Drive before", rest.UploadNotice);
        Assert.DoesNotContain("created a NEW ZIP", rest.UploadNotice);
    }

    [Fact]
    public async Task InvalidUtf8InIgnoredUploadReceiptPropertyRemainsUncertain()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"id\":\"valid\",\"ignored\":\"X\"}");
        bytes[Array.IndexOf(bytes, (byte)'X')] = 0xff;
        var handler = new Handler(_ => Response(bytes, "application/json"));
        using var rest = new DriveRestClient(new HttpClient(handler), new Auth());
        var failure = await Assert.ThrowsAsync<DriveFailure>(() => rest.CreateZipAsync("synthetic.zip", new byte[] { 1 }, default));
        Assert.Equal(DriveCode.Uncertain, failure.Code);
        Assert.True(failure.Uncertain);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("Check Drive before", rest.UploadNotice);
    }

    [Theory]
    [InlineData(401, DriveCode.Unauthorized)]
    [InlineData(403, DriveCode.Forbidden)]
    [InlineData(429, DriveCode.RateLimited)]
    public async Task RejectionsAreTypedAndDoNotEchoErrorBodies(int status, DriveCode code)
    {
        var body = new CountingStream(1000000);
        var handler = new Handler(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(body) });
        using var rest = new DriveRestClient(new HttpClient(handler), new Auth());
        var failure = await Assert.ThrowsAsync<DriveFailure>(() => rest.CreateZipAsync("x.zip", new byte[] { 1 }, default));
        Assert.Equal(code, failure.Code); Assert.False(failure.Uncertain);
        Assert.Equal(0, body.ReadBytes);
        Assert.Equal("Drive operation did not complete.", failure.Message);
    }

    [Fact]
    public async Task HeaderLimitRejectedWithoutReading_AndContentTypeRejected()
    {
        var body = new CountingStream(1);
        var response = Response(body);
        response.Content.Headers.ContentLength = ArchiveLimits.ZipInputSize + 1;
        var handler = new Handler(_ => response);
        using var rest = new DriveRestClient(new HttpClient(handler), new Auth());
        Assert.Equal(DriveCode.TooLarge, (await Assert.ThrowsAsync<DriveFailure>(() => rest.DownloadAsync(new("id", "x.zip"), default))).Code);
        Assert.Equal(0, body.ReadBytes);
        handler.Next = _ => Task.FromResult(Response([1], "text/html"));
        Assert.Equal(DriveCode.Malformed, (await Assert.ThrowsAsync<DriveFailure>(() => rest.DownloadAsync(new("id", "x.zip"), default))).Code);
    }

    [Fact]
    public async Task MissingLengthStreamingCapReadsAtMostBudgetPlusSentinel()
    {
        var body = new CountingStream(ArchiveLimits.ZipInputSize + 1000);
        var handler = new Handler(_ => Response(body));
        using var rest = new DriveRestClient(new HttpClient(handler), new Auth());
        Assert.Equal(DriveCode.TooLarge, (await Assert.ThrowsAsync<DriveFailure>(() => rest.DownloadAsync(new("id", "x.zip"), default))).Code);
        Assert.Equal(ArchiveLimits.ZipInputSize + 1, body.ReadBytes);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task EncodedResponseWireLengthDoesNotRejectDecodedBytes()
    {
        byte[] decoded = [1, 2, 3, 4, 5];
        var response = Response(decoded);
        response.Content.Headers.ContentEncoding.Add("gzip");
        response.Content.Headers.ContentLength = 2; // Fetch delivered already-decoded bytes
        var handler = new Handler(_ => response);
        using var rest = new DriveRestClient(new HttpClient(handler), new Auth());
        Assert.Equal(decoded, await rest.DownloadAsync(new("id", "x.zip"), default));
    }

    [Fact]
    public async Task EncodedResponseSmallWireLengthStillEnforcesDecodedStreamCap()
    {
        var body = new CountingStream(ArchiveLimits.ZipInputSize + 1000);
        var response = Response(body);
        response.Content.Headers.ContentEncoding.Add("gzip");
        response.Content.Headers.ContentLength = 10;
        var handler = new Handler(_ => response);
        using var rest = new DriveRestClient(new HttpClient(handler), new Auth());
        Assert.Equal(DriveCode.TooLarge,
            (await Assert.ThrowsAsync<DriveFailure>(() => rest.DownloadAsync(new("id", "x.zip"), default))).Code);
        Assert.Equal(ArchiveLimits.ZipInputSize + 1, body.ReadBytes);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task NewFilePostOnly_MetadataAndExactArchiveBytes()
    {
        byte[] zip = [0, 1, 255, 80, 75];
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id", request.RequestUri!.AbsoluteUri);
            var parts = Assert.IsType<MultipartContent>(request.Content).ToArray();
            Assert.Equal(2, parts.Length);
            var metadata = await parts[0].ReadAsStringAsync();
            Assert.Contains("application/zip", metadata); Assert.DoesNotContain("synthetic", metadata);
            Assert.Equal(zip, await parts[1].ReadAsByteArrayAsync());
            Assert.Equal("multipart/related", request.Content.Headers.ContentType!.MediaType);
            return Response(Encoding.UTF8.GetBytes("{\"id\":\"new_id\"}"), "application/json");
        });
        using var rest = new DriveRestClient(new HttpClient(handler), new Auth());
        Assert.Equal("new_id", await rest.CreateZipAsync("Україна🙂.zip", zip, default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UploadLostResponseOrMalformedSuccessIsUncertain_NoRetry()
    {
        var handler = new Handler(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("must not surface raw details")));
        using var rest = new DriveRestClient(new HttpClient(handler), new Auth());
        var failure = await Assert.ThrowsAsync<DriveFailure>(() => rest.CreateZipAsync("x.zip", new byte[] { 1 }, default));
        Assert.True(failure.Uncertain); Assert.DoesNotContain("raw", failure.Message);
        Assert.Equal(1, handler.Calls);
        handler.Next = _ => Task.FromResult(Response(Encoding.UTF8.GetBytes("{\"id\":\"https://bad.invalid\"}"), "application/json"));
        Assert.True((await Assert.ThrowsAsync<DriveFailure>(() => rest.CreateZipAsync("x.zip", new byte[] { 1 }, default))).Uncertain);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task OversizeReceiptIsUncertainAndBounded()
    {
        var body = new CountingStream(DriveRestClient.ReceiptBudget + 100);
        var handler = new Handler(_ => Response(body, "application/json"));
        using var rest = new DriveRestClient(new HttpClient(handler), new Auth());
        var failure = await Assert.ThrowsAsync<DriveFailure>(() => rest.CreateZipAsync("x.zip", new byte[] { 1 }, default));
        Assert.True(failure.Uncertain); Assert.Equal(DriveCode.TooLarge, failure.Code);
        Assert.Equal(DriveRestClient.ReceiptBudget + 1, body.ReadBytes);
    }

    [Fact]
    public async Task PreDispatchCancellationIsCertainAndSendsNothing()
    {
        var handler = new Handler(_ => Task.FromException<HttpResponseMessage>(new InvalidOperationException()));
        using var rest = new DriveRestClient(new HttpClient(handler), new Auth());
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var failure = await Assert.ThrowsAsync<DriveFailure>(() => rest.CreateZipAsync("x.zip", new byte[] { 1 }, cts.Token));
        Assert.False(failure.Uncertain); Assert.Equal(DriveCode.Cancelled, failure.Code); Assert.Equal(0, handler.Calls);
    }

    internal sealed class Auth : IDriveAuth
    {
        public DriveCode Status { get; set; } = DriveCode.Ready;
        internal int Authorizations;
        public event Action? Changed;
        public void Authorize(HttpRequestMessage request)
        {
            if (Status != DriveCode.Ready) throw new DriveFailure(Status);
            Authorizations++; request.Headers.Authorization = new("Bearer", "synthetic");
        }
        public Task ConnectAsync(DriveConfiguration config) { Status = DriveCode.Ready; Changed?.Invoke(); return Task.CompletedTask; }
        public Task<DriveSelection?> ChooseZipAsync(DriveConfiguration config) => Task.FromResult<DriveSelection?>(new("selected_id", "selected.zip"));
        public void CancelPending() { }
        public void Disconnect() { Status = DriveCode.Disconnected; Changed?.Invoke(); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal sealed class Handler : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> Next;
        internal int Calls;
        internal Handler(Func<HttpRequestMessage, HttpResponseMessage> next) : this(request => Task.FromResult(next(request))) { }
        internal Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> next) => Next = next;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Next(request); }
    }
    internal static HttpResponseMessage Response(byte[] body, string type = "application/zip")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type); return response;
    }
    internal static HttpResponseMessage Response(Stream body, string type = "application/zip")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type); return response;
    }
    private sealed class CountingStream(long remaining) : Stream
    {
        internal long ReadBytes;
        internal bool Disposed;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, remaining); Array.Fill<byte>(buffer, 65, offset, read);
            ReadBytes += read; remaining -= read; return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); var read = (int)Math.Min(buffer.Length, remaining);
            buffer.Span[..read].Fill(65); ReadBytes += read; remaining -= read; return ValueTask.FromResult(read);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
