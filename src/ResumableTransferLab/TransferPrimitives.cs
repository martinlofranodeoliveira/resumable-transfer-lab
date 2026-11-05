namespace ResumableTransferLab;

public static class TransferLimits
{
    public const int CurrentStateSchemaVersion = 1;
    public const long MaxArtifactBytes = 64L * 1024 * 1024;
    public const int MaxStateFileBytes = 64 * 1024;
    public const int MaxChunkBytes = 64 * 1024;
    public const int MaxResponseHeadersKilobytes = 16;
    public const int MaxArtifactIdLength = 128;
    public const int MaxUrlLength = 512;
    public const int MaxETagLength = 256;
    public const int Sha256HexLength = 64;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(120);
    public const string StateFileName = "transfer-state.json";
    public const string PartialFileName = "artifact.part";
    public const string StagingFileName = "artifact.resume.stage";
    public const string StateStagingFileName = "transfer-state.next";
    public const string LockFileName = "transfer.lock";
}

public enum TransferStatus { Completed = 0, Interrupted = 1, Failed = 2 }

public enum TransferErrorCode
{
    None = 0, InvalidRequest, LimitExceeded, NumericOverflow, NonLoopbackOrInvalidUrl,
    WorkspacePathUnsafe, ConcurrentTransferNotAllowed, DuplicateStateKey, UnsupportedStateVersion,
    InvalidStateFormat, AmbiguousLocalState, DestinationAlreadyExists, RedirectRefused,
    ValidatorChangedOrFullResponseRefused, UnexpectedStatusCode, MissingOrWeakETag,
    ETagMismatchOnPartial, UnexpectedContentEncoding, MultipartNotSupported, InvalidContentRange,
    ContentLengthMismatch, TruncatedBody, OversizedBody, DigestMismatch, TransferTimeoutOrNetworkError,
    LocalIoError
}

public static class TransferErrorCodeExtensions
{
    public static string ToContractCode(this TransferErrorCode code) => code switch
    {
        TransferErrorCode.None => "none",
        TransferErrorCode.InvalidRequest => "invalid_request",
        TransferErrorCode.LimitExceeded => "limit_exceeded",
        TransferErrorCode.NumericOverflow => "numeric_overflow",
        TransferErrorCode.NonLoopbackOrInvalidUrl => "non_loopback_or_invalid_url",
        TransferErrorCode.WorkspacePathUnsafe => "workspace_path_unsafe",
        TransferErrorCode.ConcurrentTransferNotAllowed => "concurrent_transfer_not_allowed",
        TransferErrorCode.DuplicateStateKey => "duplicate_state_key",
        TransferErrorCode.UnsupportedStateVersion => "unsupported_state_version",
        TransferErrorCode.InvalidStateFormat => "invalid_state_format",
        TransferErrorCode.AmbiguousLocalState => "ambiguous_local_state",
        TransferErrorCode.DestinationAlreadyExists => "destination_already_exists",
        TransferErrorCode.RedirectRefused => "redirect_refused",
        TransferErrorCode.ValidatorChangedOrFullResponseRefused => "validator_changed_or_full_response_refused",
        TransferErrorCode.UnexpectedStatusCode => "unexpected_status_code",
        TransferErrorCode.MissingOrWeakETag => "missing_or_weak_etag",
        TransferErrorCode.ETagMismatchOnPartial => "etag_mismatch_on_partial",
        TransferErrorCode.UnexpectedContentEncoding => "unexpected_content_encoding",
        TransferErrorCode.MultipartNotSupported => "multipart_not_supported",
        TransferErrorCode.InvalidContentRange => "invalid_content_range",
        TransferErrorCode.ContentLengthMismatch => "content_length_mismatch",
        TransferErrorCode.TruncatedBody => "truncated_body",
        TransferErrorCode.OversizedBody => "oversized_body",
        TransferErrorCode.DigestMismatch => "digest_mismatch",
        TransferErrorCode.TransferTimeoutOrNetworkError => "transfer_timeout_or_network_error",
        TransferErrorCode.LocalIoError => "local_io_error",
        _ => "unknown_error"
    };
}

public sealed record TransferError(TransferErrorCode Code, string ContractCode, string Message, int? HttpStatusCode = null)
{
    public static TransferError Create(TransferErrorCode code, string message, int? httpStatusCode = null) =>
        new(code, code.ToContractCode(), message, httpStatusCode);
}
