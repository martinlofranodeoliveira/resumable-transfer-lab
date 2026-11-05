using System.Globalization;
using System.Text.Json;
using ResumableTransferLab.Fixture;

namespace ResumableTransferLab.Cli;

public static class Program
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        try { return await RunAsync(args).ConfigureAwait(false); }
        catch (CliInputException) { Error("Invalid command options. Use --help for the supported syntax."); return 1; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or
            System.Security.SecurityException or InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            Error("Operation or output failed. No raw local or HTTP diagnostics are emitted.");
            return 2;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 32 || args.Any(a => a.Length > 4096)) throw new CliInputException();
        if (args.Length == 0 || (args.Length == 1 && args[0] is "--help" or "-h" or "help"))
        {
            Console.WriteLine("Resumable Transfer Lab 0.1.0 (development) — Lofrano Tecnologia\n" +
                "demo [--text | --json | --both]\n" +
                "fixture (prints its URL; Enter or EOF stops it)\n" +
                "transfer --url URL --artifact-id ID --workspace DIR --destination FILE --expected-length N --expected-sha256 HEX [--stop-after-bytes N] [--chunk-size N] [--timeout-ms N] [--json]\n" +
                "inspect-state --workspace DIR [--json]");
            return 0;
        }
        switch (args[0])
        {
            case "demo":
            {
                if (args.Length > 2 || (args.Length == 2 && args[1] is not ("--text" or "--json" or "--both"))) throw new CliInputException();
                string format = args.Length == 1 ? "--text" : args[1];
                DemoLabReport report = await DemoRunner.RunLocalLabAsync().ConfigureAwait(false);
                if (format != "--json") Console.Write(DemoRunner.FormatHumanReadable(report));
                if (format == "--both") Console.WriteLine("--- JSON ---");
                if (format != "--text") Console.WriteLine(DemoRunner.FormatJson(report));
                return 0;
            }
            case "fixture":
            {
                if (args.Length != 1) throw new CliInputException();
                await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync().ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    url = server.ArtifactUrl, artifactId = server.ArtifactId,
                    expectedLength = 32, expectedSha256 = SyntheticArtifactFixture.DefaultSha256
                }, Json));
                await Console.In.ReadLineAsync().ConfigureAwait(false);
                return 0;
            }
            case "transfer":
                return await TransferAsync(Parse(args[1..], ["--url", "--artifact-id", "--workspace", "--destination",
                    "--expected-length", "--expected-sha256", "--stop-after-bytes", "--chunk-size", "--timeout-ms", "--json"])).ConfigureAwait(false);
            case "inspect-state":
            {
                Dictionary<string, string> options = Parse(args[1..], ["--workspace", "--json"]);
                WorkspaceInspectionResult result = ResumableTransferClient.InspectWorkspace(Required(options, "--workspace"));
                object report = new
                {
                    result.IsConsistent, result.ActualPartialLength, result.State?.ArtifactId,
                    result.State?.StrongETag, result.State?.CommittedBytes, result.State?.ExpectedLength,
                    result.State?.ExpectedSha256,
                    error = result.Error is null ? null : new { code = result.Error.ContractCode, result.Error.Message }
                };
                if (options.ContainsKey("--json")) Console.WriteLine(JsonSerializer.Serialize(report, Json));
                else Console.WriteLine($"Consistent: {result.IsConsistent}\nCommitted bytes: {result.State?.CommittedBytes}\nError: {result.Error?.ContractCode ?? "none"}");
                return result.IsConsistent ? 0 : 2;
            }
            default: throw new CliInputException();
        }
    }

    private static async Task<int> TransferAsync(Dictionary<string, string> options)
    {
        long length = Integer(Required(options, "--expected-length"));
        long? stop = options.TryGetValue("--stop-after-bytes", out string? value) ? Integer(value) : null;
        long chunk = options.TryGetValue("--chunk-size", out value) ? Integer(value) : TransferLimits.MaxChunkBytes;
        long timeout = options.TryGetValue("--timeout-ms", out value) ? Integer(value) : 15000;
        if (chunk < int.MinValue || chunk > int.MaxValue || timeout < 1 || timeout > 120000) throw new CliInputException();
        TransferRequest request = new()
        {
            ArtifactId = Required(options, "--artifact-id"), SourceUrl = Required(options, "--url"),
            WorkspaceDirectory = Required(options, "--workspace"), DestinationPath = Required(options, "--destination"),
            ExpectedLength = length, ExpectedSha256 = Required(options, "--expected-sha256"),
            StopAfterCommittedBytes = stop, ChunkSizeBytes = (int)chunk, Timeout = TimeSpan.FromMilliseconds(timeout)
        };
        TransferResult result = await new ResumableTransferClient().ExecuteAsync(request).ConfigureAwait(false);
        if (options.ContainsKey("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = result.Status.ToString().ToLowerInvariant(), result.ArtifactId, result.WasResumed,
                result.CommittedBytes, result.ExpectedLength, result.StrongETag, result.VerifiedSha256, result.DestinationPublished,
                error = result.Error is null ? null : new { code = result.Error.ContractCode, result.Error.Message, result.Error.HttpStatusCode }
            }, Json));
        }
        else
        {
            Console.WriteLine($"Status: {result.Status}\nArtifact: {result.ArtifactId}\nCommitted bytes: {result.CommittedBytes}/{result.ExpectedLength}\nPublished: {result.DestinationPublished}\nSHA-256: {result.VerifiedSha256 ?? "not verified"}\nError: {result.Error?.ContractCode ?? "none"}");
        }
        return result.Status == TransferStatus.Failed ? 2 : 0;
    }

    private static Dictionary<string, string> Parse(string[] args, string[] allowed)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (!allowed.Contains(key) || result.ContainsKey(key)) throw new CliInputException();
            if (key == "--json") result.Add(key, string.Empty);
            else
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new CliInputException();
                result.Add(key, args[i]);
            }
        }
        return result;
    }
    private static string Required(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string? value) && value.Length != 0 ? value : throw new CliInputException();
    private static long Integer(string value) => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number)
        ? number : throw new CliInputException();
    private static void Error(string message)
    {
        try { Console.Error.WriteLine(message); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* Broken diagnostic output cannot be repaired here. */ }
    }
    private sealed class CliInputException : Exception { }
}
