using System.Text;
using System.Text.Json;
using ResumableTransferLab.Fixture;

namespace ResumableTransferLab.Cli;

public sealed record DemoScenarioOneReport(string ArtifactId, long ExpectedLength, string ExpectedSha256,
    long InterruptedAtBytes, string InterruptedPrefixText, string PersistedStrongETag, string ResumeRangeHeader,
    string ResumeIfRangeHeader, int ResumeHttpStatus, string FinalText, string VerifiedSha256, bool DestinationPublished);
public sealed record DemoScenarioTwoReport(string ArtifactId, long InitialCommittedBytes, string InitialETag,
    string MutatedETag, int ServerResponseStatusOnResume, string RefusalCode, long PreservedPartialBytes,
    string PreservedPartialText, bool DestinationCreated);
public sealed record DemoLabReport(string Version, DemoScenarioOneReport ResumeAndVerifyScenario,
    DemoScenarioTwoReport ChangedETagRefusalScenario);

public static class DemoRunner
{
    private const string ExpectedDigest = "73337f479fe170d73e53e247f3052e4243cc9c2a0ffa621853d9385c619efb77";

    public static async Task<DemoLabReport> RunLocalLabAsync(CancellationToken cancellationToken = default)
    {
        string root = Path.Combine(Path.GetTempPath(), $"resumable-transfer-demo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            TransferRequest request = new()
            {
                ArtifactId = SyntheticArtifactFixture.DefaultArtifactId, SourceUrl = server.ArtifactUrl,
                WorkspaceDirectory = Path.Combine(root, "resume"), DestinationPath = Path.Combine(root, "output", "artifact.bin"),
                ExpectedLength = 32, ExpectedSha256 = ExpectedDigest, StopAfterCommittedBytes = 10
            };
            TransferResult first = await new ResumableTransferClient().ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            Require(first.Status == TransferStatus.Interrupted && first.CommittedBytes == 10);
            WorkspaceInspectionResult inspection = ResumableTransferClient.InspectWorkspace(request.WorkspaceDirectory);
            Require(inspection.IsConsistent && inspection.State is not null);
            string prefix = File.ReadAllText(Path.Combine(request.WorkspaceDirectory, TransferLimits.PartialFileName), Encoding.ASCII);
            Require(prefix == "0123456789");
            TransferResult complete = await new ResumableTransferClient().ExecuteAsync(request with { StopAfterCommittedBytes = null }, cancellationToken).ConfigureAwait(false);
            RecordedHttpRequest http = server.RecordedRequests[^1];
            string final = File.ReadAllText(request.DestinationPath, Encoding.ASCII);
            Require(complete.Status == TransferStatus.Completed && complete.VerifiedSha256 == ExpectedDigest &&
                final == "0123456789abcdefghijklmnopqrstuv" && http.RangeHeader == "bytes=10-" &&
                http.IfRangeHeader == SyntheticArtifactFixture.DefaultStrongETag && http.ContentRangeHeader == "bytes 10-31/32");
            DemoScenarioOneReport one = new(request.ArtifactId, 32, ExpectedDigest, 10, prefix,
                inspection.State!.StrongETag, http.RangeHeader!, http.IfRangeHeader!, http.ResponseStatusCode,
                final, complete.VerifiedSha256!, complete.DestinationPublished);

            TransferRequest changed = request with
            {
                WorkspaceDirectory = Path.Combine(root, "changed"), DestinationPath = Path.Combine(root, "changed.bin")
            };
            TransferResult checkpoint = await new ResumableTransferClient().ExecuteAsync(changed, cancellationToken).ConfigureAwait(false);
            Require(checkpoint.Status == TransferStatus.Interrupted);
            string statePath = Path.Combine(changed.WorkspaceDirectory, TransferLimits.StateFileName);
            byte[] saved = File.ReadAllBytes(statePath);
            server.UpdateRepresentation(SyntheticArtifactFixture.MutatedBytes, SyntheticArtifactFixture.MutatedStrongETag);
            TransferResult refused = await new ResumableTransferClient().ExecuteAsync(changed with { StopAfterCommittedBytes = null }, cancellationToken).ConfigureAwait(false);
            string preserved = File.ReadAllText(Path.Combine(changed.WorkspaceDirectory, TransferLimits.PartialFileName), Encoding.ASCII);
            http = server.RecordedRequests[^1];
            Require(refused.Error?.Code == TransferErrorCode.ValidatorChangedOrFullResponseRefused && http.ResponseStatusCode == 200 &&
                preserved == "0123456789" && saved.AsSpan().SequenceEqual(File.ReadAllBytes(statePath)) && !File.Exists(changed.DestinationPath));
            DemoScenarioTwoReport two = new(changed.ArtifactId, 10, SyntheticArtifactFixture.DefaultStrongETag,
                SyntheticArtifactFixture.MutatedStrongETag, 200, refused.Error!.ContractCode, 10, preserved, false);
            return new("0.1.0", one, two);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Synthetic lab invariant failed.");
    }

    public static string FormatHumanReadable(DemoLabReport report)
    {
        DemoScenarioOneReport one = report.ResumeAndVerifyScenario;
        DemoScenarioTwoReport two = report.ChangedETagRefusalScenario;
        StringBuilder text = new();
        text.AppendLine("Resumable Transfer Lab 0.1.0 (development)");
        text.AppendLine("Local synthetic HTTP; expected-byte integrity, not origin authenticity.");
        text.AppendLine($"Checkpoint: {one.InterruptedAtBytes}/32 bytes ({one.InterruptedPrefixText})");
        text.AppendLine($"Resume: Range {one.ResumeRangeHeader}; If-Range {one.ResumeIfRangeHeader}");
        text.AppendLine($"Response: {one.ResumeHttpStatus}; Content-Range bytes 10-31/32");
        text.AppendLine($"Final: {one.FinalText}");
        text.AppendLine($"SHA-256: {one.VerifiedSha256}");
        text.AppendLine($"Published: {one.DestinationPublished}");
        text.AppendLine($"Changed ETag: HTTP {two.ServerResponseStatusOnResume}; {two.RefusalCode}");
        text.AppendLine($"Preserved: {two.PreservedPartialBytes} bytes ({two.PreservedPartialText}); destination created: {two.DestinationCreated}");
        return text.ToString();
    }

    public static string FormatJson(DemoLabReport report) => JsonSerializer.Serialize(report,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
}
