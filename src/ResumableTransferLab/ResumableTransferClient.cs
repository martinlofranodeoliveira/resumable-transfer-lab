using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace ResumableTransferLab;

public sealed class ResumableTransferClient
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<TransferResult> ExecuteAsync(TransferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool entered = false;
        bool resumed = false;
        TransferState? state = null;
        string safeId = TransferGuards.ValidateArtifactId(request.ArtifactId, out _) ? request.ArtifactId : string.Empty;
        TransferResult Failure(TransferError error) => TransferResult.Failed(
            safeId, resumed, state?.CommittedBytes ?? 0, request.ExpectedLength, error, state?.StrongETag);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            entered = Gate.Wait(0);
            if (!entered)
                throw TransferFailure.For(TransferErrorCode.ConcurrentTransferNotAllowed, "Only one transfer is permitted per process.");

            ValidateRequest(request);
            TransferGuards.ValidateLoopbackHttpUrl(request.SourceUrl, out Uri? source, out _);
            TransferGuards.ValidateSha256Hex(request.ExpectedSha256, out string digest, out _);
            TransferGuards.ValidatePaths(request.WorkspaceDirectory, request.DestinationPath,
                out string workspace, out string destination, out _);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(request.Timeout ?? TransferLimits.DefaultTimeout);
            CancellationToken ct = deadline.Token;

            using WorkspaceLease lease = WorkspaceLease.Open(workspace, create: true);
            resumed = File.Exists(lease.StatePath);
            state = lease.LoadState(ct);
            if (state is not null &&
                (state.ArtifactId != request.ArtifactId || state.SourceUrl != source!.AbsoluteUri ||
                 state.ExpectedLength != request.ExpectedLength || state.ExpectedSha256 != digest))
                throw TransferFailure.For(TransferErrorCode.AmbiguousLocalState, "Request identity does not match persisted state.");

            if (state?.CommittedBytes == request.ExpectedLength)
                return Publish(request, state, destination, lease.PartPath, resumed, ct);
            if (state is not null && request.StopAfterCommittedBytes is long boundary && boundary <= state.CommittedBytes)
                return TransferResult.Interrupted(safeId, true, state.CommittedBytes, request.ExpectedLength, state.StrongETag);

            using SocketsHttpHandler handler = new()
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                Proxy = null,
                Credentials = null,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                MaxConnectionsPerServer = 1,
                MaxResponseHeadersLength = TransferLimits.MaxResponseHeadersKilobytes,
                ConnectTimeout = request.Timeout ?? TransferLimits.DefaultTimeout
            };
            using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using HttpRequestMessage message = new(HttpMethod.Get, source);
            if (state is not null)
            {
                message.Headers.Range = new RangeHeaderValue(state.CommittedBytes, null);
                message.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(state.StrongETag));
            }
            using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            string etag = ValidateResponse(response, state, request.ExpectedLength);
            long offset = state?.CommittedBytes ?? 0;
            bool stop = request.StopAfterCommittedBytes is long stopAt && stopAt < request.ExpectedLength;
            long commitTo = stop ? request.StopAfterCommittedBytes!.Value : request.ExpectedLength;
            long commitCount = commitTo - offset;

            // An initial orderly stop deliberately closes a response after its prefix.
            // A resume validates the ENTIRE declared suffix before any append, even when
            // the caller requests another intermediate checkpoint.
            long stageCount = state is null ? commitCount : request.ExpectedLength - offset;
            await StageAsync(response, lease.StagePath, stageCount,
                exactEof: state is not null || !stop, request.ChunkSizeBytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (state is null)
                File.Move(lease.StagePath, lease.PartPath, overwrite: false);
            else
                TransferStorage.AppendPrefix(lease.StagePath, lease.PartPath, commitCount, ct);

            string prefix = TransferStorage.HashFile(lease.PartPath, commitTo, ct);
            TransferState next = new()
            {
                SchemaVersion = TransferLimits.CurrentStateSchemaVersion,
                ArtifactId = safeId,
                SourceUrl = source!.AbsoluteUri,
                StrongETag = etag,
                ExpectedLength = request.ExpectedLength,
                ExpectedSha256 = digest,
                CommittedBytes = commitTo,
                CommittedPrefixSha256 = prefix
            };
            TransferStorage.WriteState(lease.StatePath, next, ct);
            state = next;
            if (File.Exists(lease.StagePath)) File.Delete(lease.StagePath);
            if (stop)
                return TransferResult.Interrupted(safeId, resumed, commitTo, request.ExpectedLength, etag);
            return Publish(request, next, destination, lease.PartPath, resumed, ct);
        }
        catch (TransferFailure failure) { return Failure(failure.Error); }
        catch (OperationCanceledException)
        {
            return Failure(TransferError.Create(TransferErrorCode.TransferTimeoutOrNetworkError, "Transfer deadline expired or cancellation was requested."));
        }
        catch (HttpRequestException)
        {
            return Failure(TransferError.Create(TransferErrorCode.TransferTimeoutOrNetworkError, "Loopback HTTP request failed."));
        }
        catch (Exception ex) when (TransferStorage.IsLocalIo(ex))
        {
            return Failure(TransferError.Create(TransferErrorCode.LocalIoError, "Local I/O failed; preserve the workspace for inspection."));
        }
        finally { if (entered) Gate.Release(); }
    }

    public static WorkspaceInspectionResult InspectWorkspace(string workspaceDirectory)
    {
        try
        {
            if (!TransferGuards.ValidateWorkspaceOnlyPath(workspaceDirectory, out string workspace, out TransferError? error))
                return new(false, null, null, error);
            using WorkspaceLease lease = WorkspaceLease.Open(workspace, create: false);
            TransferState? state = lease.LoadState(CancellationToken.None);
            if (state is null)
                throw TransferFailure.For(TransferErrorCode.AmbiguousLocalState, "Workspace has no committed checkpoint.");
            return new(true, state, state.CommittedBytes, null);
        }
        catch (TransferFailure failure) { return new(false, null, null, failure.Error); }
        catch (Exception ex) when (TransferStorage.IsLocalIo(ex))
        {
            return new(false, null, null, TransferError.Create(TransferErrorCode.LocalIoError, "Workspace inspection could not read local files."));
        }
    }

    private static void ValidateRequest(TransferRequest request)
    {
        if (!TransferGuards.ValidateArtifactId(request.ArtifactId, out TransferError? error)) throw new TransferFailure(error!);
        if (request.ExpectedLength < 0 || request.ChunkSizeBytes <= 0)
            throw TransferFailure.For(TransferErrorCode.InvalidRequest, "Length must be non-negative and chunk size must be positive.");
        if (request.ExpectedLength > TransferLimits.MaxArtifactBytes || request.ChunkSizeBytes > TransferLimits.MaxChunkBytes)
            throw TransferFailure.For(TransferErrorCode.LimitExceeded, "Artifact or chunk size exceeds the configured cap.");
        if (request.StopAfterCommittedBytes is long stop && (stop < 0 || stop > request.ExpectedLength))
            throw TransferFailure.For(TransferErrorCode.InvalidRequest, "Stop boundary must be within the expected artifact.");
        TimeSpan timeout = request.Timeout ?? TransferLimits.DefaultTimeout;
        if (timeout <= TimeSpan.Zero || timeout > TransferLimits.MaxTimeout)
            throw TransferFailure.For(TransferErrorCode.LimitExceeded, "Deadline must be positive and at most 120 seconds.");
        if (!TransferGuards.ValidateSha256Hex(request.ExpectedSha256, out _, out error) ||
            !TransferGuards.ValidateLoopbackHttpUrl(request.SourceUrl, out _, out error) ||
            !TransferGuards.ValidatePaths(request.WorkspaceDirectory, request.DestinationPath, out _, out _, out error))
            throw new TransferFailure(error!);
    }

    private static TransferResult Publish(TransferRequest request, TransferState state, string destination,
        string partPath, bool resumed, CancellationToken ct)
    {
        string digest = TransferStorage.Publish(partPath, destination, request.ExpectedLength, state.ExpectedSha256, ct);
        return TransferResult.Completed(request.ArtifactId, resumed, request.ExpectedLength,
            request.ExpectedLength, state.StrongETag, digest);
    }

    private static string ValidateResponse(HttpResponseMessage response, TransferState? state, long total)
    {
        int status = (int)response.StatusCode;
        void Refuse(TransferErrorCode code, string message) => throw new TransferFailure(TransferError.Create(code, message, status));
        if (status is >= 300 and < 400) Refuse(TransferErrorCode.RedirectRefused, "Redirects are disabled and refused.");
        if (state is not null && response.StatusCode == HttpStatusCode.OK)
            Refuse(TransferErrorCode.ValidatorChangedOrFullResponseRefused, "A full response cannot be appended to a checkpoint.");
        if (response.StatusCode != (state is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent))
            Refuse(TransferErrorCode.UnexpectedStatusCode, "Response status does not match the transfer phase.");
        if (response.Content.Headers.Contains("Content-Encoding"))
            Refuse(TransferErrorCode.UnexpectedContentEncoding, "Content-Encoding is not permitted, including identity.");
        if (response.Content.Headers.TryGetValues("Content-Type", out IEnumerable<string>? types) &&
            types.Any(v => v.Contains("multipart/", StringComparison.OrdinalIgnoreCase)))
            Refuse(TransferErrorCode.MultipartNotSupported, "Multipart content is not supported.");
        string[] tags = response.Headers.TryGetValues("ETag", out IEnumerable<string>? values) ? values.ToArray() : [];
        if (tags.Length != 1 || !TransferGuards.ValidateStrongETag(tags[0], out _, out _))
            Refuse(TransferErrorCode.MissingOrWeakETag, "Exactly one supported strong ETag is required.");
        TransferGuards.ValidateStrongETag(tags[0], out string etag, out _);
        if (state is not null && etag != state.StrongETag)
            Refuse(TransferErrorCode.ETagMismatchOnPartial, "Partial response validator differs from the checkpoint.");

        if (state is null)
        {
            if (response.Content.Headers.Contains("Content-Range"))
                Refuse(TransferErrorCode.InvalidContentRange, "Initial responses must not contain Content-Range.");
        }
        else
        {
            string[] ranges = response.Content.Headers.TryGetValues("Content-Range", out values) ? values.ToArray() : [];
            if (ranges.Length != 1)
                Refuse(TransferErrorCode.InvalidContentRange, "Exactly one Content-Range is required.");
            string raw = ranges[0].Trim();
            if (!raw.StartsWith("bytes ", StringComparison.Ordinal))
                Refuse(TransferErrorCode.InvalidContentRange, "Only the lowercase bytes range unit is supported.");
            string[] numbers = raw[6..].Split(['-', '/']);
            if (numbers.Length != 3 || numbers.Any(n => n.Length == 0 || n.Any(c => c is < '0' or > '9')))
                Refuse(TransferErrorCode.InvalidContentRange, "Malformed Content-Range.");
            long[] bounds = new long[3];
            for (int i = 0; i < 3; i++)
                if (!long.TryParse(numbers[i], NumberStyles.None, CultureInfo.InvariantCulture, out bounds[i]))
                    Refuse(TransferErrorCode.NumericOverflow, "Content-Range exceeds integer bounds.");
            if (!ContentRangeHeaderValue.TryParse(raw, out ContentRangeHeaderValue? parsed) || parsed is null ||
                !parsed.HasRange || !parsed.HasLength || bounds[0] != state.CommittedBytes ||
                bounds[1] != total - 1 || bounds[2] != total || parsed.From != bounds[0] ||
                parsed.To != bounds[1] || parsed.Length != bounds[2])
                Refuse(TransferErrorCode.InvalidContentRange, "Content-Range does not describe the exact expected suffix.");
        }
        if (response.Content.Headers.TryGetValues("Content-Length", out values))
        {
            string[] lengths = values.ToArray();
            if (lengths.Length != 1) Refuse(TransferErrorCode.ContentLengthMismatch, "Multiple content lengths are not supported.");
            string length = lengths[0].Trim();
            if (length.Length == 0 || length.Any(c => c is < '0' or > '9'))
                Refuse(TransferErrorCode.ContentLengthMismatch, "Invalid content length.");
            if (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out long count))
                Refuse(TransferErrorCode.NumericOverflow, "Content length exceeds integer bounds.");
            if (count != total - (state?.CommittedBytes ?? 0))
                Refuse(TransferErrorCode.ContentLengthMismatch, "Content length differs from the expected response size.");
        }
        return etag;
    }

    private static async Task StageAsync(HttpResponseMessage response, string path, long count,
        bool exactEof, int chunk, CancellationToken ct)
    {
        bool owned = false;
        bool successful = false;
        try
        {
            await using FileStream stage = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            owned = true;
            await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] buffer = new byte[chunk];
            long remaining = count;
            while (remaining > 0)
            {
                int read = await ReadBodyAsync(body, buffer.AsMemory(0, (int)Math.Min(remaining, chunk)), ct).ConfigureAwait(false);
                if (read == 0) throw TransferFailure.For(TransferErrorCode.TruncatedBody, "Response ended before the expected byte count.");
                await stage.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
            }
            if (exactEof && await ReadBodyAsync(body, buffer.AsMemory(0, 1), ct).ConfigureAwait(false) != 0)
                throw TransferFailure.For(TransferErrorCode.OversizedBody, "Response exceeded the expected byte count.");
            ct.ThrowIfCancellationRequested();
            stage.Flush(flushToDisk: true);
            successful = true;
        }
        finally
        {
            if (owned && !successful) TransferStorage.DeleteOwnedStage(path);
        }
    }

    private static async Task<int> ReadBodyAsync(Stream body, Memory<byte> buffer, CancellationToken ct)
    {
        try { return await body.ReadAsync(buffer, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw TransferFailure.For(TransferErrorCode.TruncatedBody, "Response stream terminated prematurely.");
        }
    }
}
