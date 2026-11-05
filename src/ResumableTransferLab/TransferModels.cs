namespace ResumableTransferLab;

public sealed record TransferRequest
{
    public required string ArtifactId { get; init; }
    public required string SourceUrl { get; init; }
    public required string WorkspaceDirectory { get; init; }
    public required string DestinationPath { get; init; }
    public required long ExpectedLength { get; init; }
    public required string ExpectedSha256 { get; init; }
    public long? StopAfterCommittedBytes { get; init; }
    public int ChunkSizeBytes { get; init; } = TransferLimits.MaxChunkBytes;
    public TimeSpan? Timeout { get; init; }
}

public sealed record TransferState
{
    public required int SchemaVersion { get; init; }
    public required string ArtifactId { get; init; }
    public required string SourceUrl { get; init; }
    public required string StrongETag { get; init; }
    public required long ExpectedLength { get; init; }
    public required string ExpectedSha256 { get; init; }
    public required long CommittedBytes { get; init; }
    public required string CommittedPrefixSha256 { get; init; }
}

public sealed record TransferResult
{
    public required TransferStatus Status { get; init; }
    public required string ArtifactId { get; init; }
    public required bool WasResumed { get; init; }
    public required long CommittedBytes { get; init; }
    public required long ExpectedLength { get; init; }
    public string? StrongETag { get; init; }
    public string? VerifiedSha256 { get; init; }
    public required bool DestinationPublished { get; init; }
    public TransferError? Error { get; init; }

    public static TransferResult Completed(
        string artifactId,
        bool wasResumed,
        long committedBytes,
        long expectedLength,
        string strongETag,
        string verifiedSha256) =>
        new()
        {
            Status = TransferStatus.Completed,
            ArtifactId = artifactId,
            WasResumed = wasResumed,
            CommittedBytes = committedBytes,
            ExpectedLength = expectedLength,
            StrongETag = strongETag,
            VerifiedSha256 = verifiedSha256,
            DestinationPublished = true,
            Error = null
        };

    public static TransferResult Interrupted(
        string artifactId,
        bool wasResumed,
        long committedBytes,
        long expectedLength,
        string strongETag) =>
        new()
        {
            Status = TransferStatus.Interrupted,
            ArtifactId = artifactId,
            WasResumed = wasResumed,
            CommittedBytes = committedBytes,
            ExpectedLength = expectedLength,
            StrongETag = strongETag,
            VerifiedSha256 = null,
            DestinationPublished = false,
            Error = null
        };

    public static TransferResult Failed(
        string artifactId,
        bool wasResumed,
        long committedBytes,
        long expectedLength,
        TransferError error,
        string? strongETag = null,
        string? verifiedSha256 = null) =>
        new()
        {
            Status = TransferStatus.Failed,
            ArtifactId = artifactId ?? string.Empty,
            WasResumed = wasResumed,
            CommittedBytes = committedBytes,
            ExpectedLength = expectedLength,
            StrongETag = strongETag,
            VerifiedSha256 = verifiedSha256,
            DestinationPublished = false,
            Error = error
        };
}

public sealed record WorkspaceInspectionResult(
    bool IsConsistent,
    TransferState? State,
    long? ActualPartialLength,
    TransferError? Error);
