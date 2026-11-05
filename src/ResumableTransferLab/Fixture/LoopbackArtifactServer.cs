using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ResumableTransferLab.Fixture;

public static class SyntheticArtifactFixture
{
    public const string DefaultArtifactId = "synthetic-32b";
    public const string DefaultPayloadText = "0123456789abcdefghijklmnopqrstuv";
    public const string DefaultStrongETag = "\"lab-artifact-32b-v1\"";
    public const string MutatedPayloadText = "ZZZZZZZZZZSTUVWXYZ0123456789abcdef";
    public const string MutatedStrongETag = "\"lab-artifact-32b-v2\"";
    public static byte[] DefaultBytes => Encoding.ASCII.GetBytes(DefaultPayloadText);
    public static string DefaultSha256 => Convert.ToHexStringLower(SHA256.HashData(DefaultBytes));
    public static byte[] MutatedBytes => Encoding.ASCII.GetBytes(MutatedPayloadText);
    public static string MutatedSha256 => Convert.ToHexStringLower(SHA256.HashData(MutatedBytes));
}

public enum FaultInjectionMode
{
    None = 0, Redirect302, WeakETagOnInitial, WeakETagOnResume, MismatchedETagOn206,
    WrongContentRangeStart, WrongContentRangeEnd, WrongContentRangeTotal, OverflowContentRangeTotal,
    MissingContentRangeOn206, TruncatedBodyOnInitial, TruncatedBodyOnResume,
    OversizedBodyOnInitial, OversizedBodyOnResume, ContentLengthMismatchOnResume,
    UnexpectedContentEncodingGzip, MultipartByteRangesOn206, AlwaysReturn200IgnoreRange,
    DuplicateETagOnInitial, DuplicateETagOnResume, LargeResponseHeader, DelayedResponse
}

public sealed record RecordedHttpRequest(string Method, string Path, string? RangeHeader,
    string? IfRangeHeader, int ResponseStatusCode, string? ContentRangeHeader = null);

public sealed class LoopbackArtifactServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Lock _sync = new();
    private readonly Queue<RecordedHttpRequest> _requests = new();
    private byte[] _bytes;
    private string _etag;
    private FaultInjectionMode _mode;
    private LoopbackArtifactServer(WebApplication app, string baseUrl, string id, byte[] bytes, string etag, FaultInjectionMode mode)
    {
        _app = app; BaseUrl = baseUrl; ArtifactId = id; _bytes = [.. bytes]; _etag = etag; _mode = mode;
    }
    public string BaseUrl { get; }
    public string ArtifactId { get; }
    public string ArtifactUrl => $"{BaseUrl}/artifact/{ArtifactId}";
    public IReadOnlyList<RecordedHttpRequest> RecordedRequests { get { lock (_sync) return _requests.ToArray(); } }

    public static async Task<LoopbackArtifactServer> StartAsync(string artifactId = SyntheticArtifactFixture.DefaultArtifactId,
        byte[]? initialBytes = null, string initialStrongETag = SyntheticArtifactFixture.DefaultStrongETag,
        FaultInjectionMode faultMode = FaultInjectionMode.None, CancellationToken cancellationToken = default)
    {
        byte[] bytes = initialBytes ?? SyntheticArtifactFixture.DefaultBytes;
        ValidateRepresentation(bytes, initialStrongETag);
        if (!TransferGuards.ValidateArtifactId(artifactId, out _)) throw new ArgumentException("Invalid synthetic artifact identifier.", nameof(artifactId));
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = AppContext.BaseDirectory
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxConcurrentConnections = 4;
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
            options.Listen(IPAddress.Loopback, 0);
        });
        WebApplication app = builder.Build();
        LoopbackArtifactServer? server = null;
        app.Map("/artifact/{id}", async (HttpContext context, string id) =>
        {
            if (server is null) { context.Response.StatusCode = 503; return; }
            await server.HandleAsync(context, id).ConfigureAwait(false);
        });
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            int port = new Uri(app.Urls.Single()).Port;
            server = new(app, $"http://127.0.0.1:{port}", artifactId, bytes, initialStrongETag, faultMode);
            return server;
        }
        catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static void ValidateRepresentation(byte[] bytes, string etag)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.LongLength > TransferLimits.MaxArtifactBytes) throw new ArgumentException("Synthetic representation exceeds the artifact cap.", nameof(bytes));
        if (!TransferGuards.ValidateStrongETag(etag, out _, out _)) throw new ArgumentException("Invalid synthetic validator.", nameof(etag));
    }

    public void UpdateRepresentation(byte[] newBytes, string newStrongETag)
    {
        ValidateRepresentation(newBytes, newStrongETag);
        lock (_sync) { _bytes = [.. newBytes]; _etag = newStrongETag; }
    }
    public void SetFaultMode(FaultInjectionMode mode) { lock (_sync) _mode = mode; }

    private async Task HandleAsync(HttpContext context, string id)
    {
        string? range = context.Request.Headers.Range.Count == 0 ? null : context.Request.Headers.Range.ToString();
        string? ifRange = context.Request.Headers.IfRange.Count == 0 ? null : context.Request.Headers.IfRange.ToString();
        byte[] bytes;
        string etag;
        FaultInjectionMode mode;
        lock (_sync) { bytes = _bytes; etag = _etag; mode = _mode; }
        void Record()
        {
            static string? Bound(string? value) => value is null ? null : value[..Math.Min(value.Length, 1024)];
            lock (_sync)
            {
                if (_requests.Count == 128) _requests.Dequeue();
                _requests.Enqueue(new(context.Request.Method, Bound(context.Request.Path.Value) ?? string.Empty,
                    Bound(range), Bound(ifRange), context.Response.StatusCode,
                    context.Response.Headers.ContentRange.Count == 0 ? null : context.Response.Headers.ContentRange.ToString()));
            }
        }
        if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 405; Record(); return; }
        if (id != ArtifactId) { context.Response.StatusCode = 404; Record(); return; }
        if (mode == FaultInjectionMode.Redirect302)
        {
            context.Response.StatusCode = 302;
            context.Response.Headers.Location = $"{BaseUrl}/artifact/redirected";
            Record(); return;
        }
        if (mode == FaultInjectionMode.DelayedResponse)
        {
            context.Response.StatusCode = 200;
            Record();
            try { await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        int start = 0;
        bool partial = range is not null && range.StartsWith("bytes=", StringComparison.Ordinal) && range.EndsWith('-') &&
            int.TryParse(range.AsSpan(6, range.Length - 7), NumberStyles.None, CultureInfo.InvariantCulture, out start) &&
            (ifRange is null || ifRange == etag) && mode != FaultInjectionMode.AlwaysReturn200IgnoreRange;
        if (partial && start >= bytes.Length)
        {
            context.Response.StatusCode = 416;
            context.Response.Headers.ContentRange = $"bytes */{bytes.Length}";
            Record(); return;
        }
        if (!partial) start = 0;
        int count = bytes.Length - start;
        context.Response.StatusCode = partial ? 206 : 200;
        context.Response.ContentType = partial && mode == FaultInjectionMode.MultipartByteRangesOn206
            ? "multipart/byteranges; boundary=LAB" : "application/octet-stream";
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.ETag = mode switch
        {
            FaultInjectionMode.WeakETagOnInitial when !partial => "W/\"weak-initial\"",
            FaultInjectionMode.WeakETagOnResume when partial => "W/\"weak-resume\"",
            FaultInjectionMode.MismatchedETagOn206 when partial => "\"other\"",
            _ => etag
        };
        if ((!partial && mode == FaultInjectionMode.DuplicateETagOnInitial) || (partial && mode == FaultInjectionMode.DuplicateETagOnResume))
            context.Response.Headers.Append("ETag", "\"second\"");
        if (mode == FaultInjectionMode.UnexpectedContentEncodingGzip) context.Response.Headers.ContentEncoding = "gzip";
        if (mode == FaultInjectionMode.LargeResponseHeader) context.Response.Headers["X-Lab-Padding"] = new string('x', 18 * 1024);
        if (partial && mode != FaultInjectionMode.MissingContentRangeOn206)
        {
            context.Response.Headers.ContentRange = mode switch
            {
                FaultInjectionMode.WrongContentRangeStart => $"bytes {Math.Max(0, start - 1)}-{bytes.Length - 1}/{bytes.Length}",
                FaultInjectionMode.WrongContentRangeEnd => $"bytes {start}-{bytes.Length - 2}/{bytes.Length}",
                FaultInjectionMode.WrongContentRangeTotal => $"bytes {start}-{bytes.Length - 1}/{bytes.Length + 1}",
                FaultInjectionMode.OverflowContentRangeTotal => $"bytes {start}-{bytes.Length - 1}/99999999999999999999999",
                _ => $"bytes {start}-{bytes.Length - 1}/{bytes.Length}"
            };
        }
        bool truncated = mode == (partial ? FaultInjectionMode.TruncatedBodyOnResume : FaultInjectionMode.TruncatedBodyOnInitial);
        bool oversized = mode == (partial ? FaultInjectionMode.OversizedBodyOnResume : FaultInjectionMode.OversizedBodyOnInitial);
        if (truncated) count /= 2;
        else if (partial && mode == FaultInjectionMode.ContentLengthMismatchOnResume)
        {
            count = Math.Max(0, count - 1);
            context.Response.ContentLength = count;
        }
        else if (!oversized) context.Response.ContentLength = count;
        Record();
        try
        {
            await context.Response.Body.WriteAsync(bytes.AsMemory(start, count), context.RequestAborted).ConfigureAwait(false);
            if (oversized) await context.Response.Body.WriteAsync("EXTRA"u8.ToArray(), context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { /* Intentional client closure. */ }
    }

    public async ValueTask DisposeAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        try { await _app.StopAsync(timeout.Token).ConfigureAwait(false); }
        finally { await _app.DisposeAsync().ConfigureAwait(false); }
    }
}
