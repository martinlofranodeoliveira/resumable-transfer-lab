using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResumableTransferLab;
using ResumableTransferLab.Cli;
using ResumableTransferLab.Fixture;
using CliProgram = ResumableTransferLab.Cli.Program;

namespace ResumableTransferLab.Tests;

internal static class Program
{
    private const string Digest = "73337f479fe170d73e53e247f3052e4243cc9c2a0ffa621853d9385c619efb77";
    private const string Text = "0123456789abcdefghijklmnopqrstuv";
    private static readonly byte[] Bytes = Encoding.ASCII.GetBytes(Text);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "hold-lock")
        {
            using FileStream held = new(args[1], FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Console.WriteLine("ready");
            await Console.In.ReadLineAsync();
            return 0;
        }
        (string Name, Func<Task> Run)[] tests =
        [
            ("persistent HTTP resume and exact independent bytes", Happy),
            ("changed validator preserves checkpoint", Changed),
            ("adversarial initial responses", InitialFaults),
            ("adversarial resume responses preserve evidence", ResumeFaults),
            ("intermediate resume validates full suffix", IntermediateFaults),
            ("state parsing rejects malformed and ambiguous input", StateFailures),
            ("tampered partial and offset fail closed", Tampering),
            ("digest mismatch and existing destination", DigestAndDestination),
            ("publication mid-write failure and destination race", PublicationFailures),
            ("empty one-byte and end boundaries", Boundaries),
            ("multiple orderly checkpoints", MultipleStops),
            ("request limits and literal URL policy", Guards),
            ("workspace and deep symlink misuse", Paths),
            ("cross-process workspace exclusion", CrossProcessLock),
            ("cancellation deadline and process gate", Cancellation),
            ("CLI sanitized syntax JSON and broken output", Cli),
            ("deterministic actual HTTP demo", Demo)
        ];
        int failures = 0;
        foreach ((string name, Func<Task> run) in tests)
        {
            try { await run().WaitAsync(TimeSpan.FromSeconds(60)); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name} ({ex.GetType().Name})"); }
        }
        Console.WriteLine($"Tests: {tests.Length}; failures: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private sealed class OwnedRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rtl-test-{Guid.NewGuid():N}");
        public OwnedRoot() => Directory.CreateDirectory(Path);
        public string At(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Regression invariant failed.");
    }
    private static void Equal(byte[] actual, byte[] expected) => Check(actual.AsSpan().SequenceEqual(expected));
    private static void Failed(TransferResult result, TransferErrorCode code)
    {
        Check(result.Status == TransferStatus.Failed && !result.DestinationPublished && result.Error?.Code == code);
    }
    private static TransferRequest Request(OwnedRoot root, LoopbackArtifactServer server) => new()
    {
        ArtifactId = "synthetic-32b", SourceUrl = server.ArtifactUrl, WorkspaceDirectory = root.At("workspace"),
        DestinationPath = root.At("output.bin"), ExpectedLength = 32, ExpectedSha256 = Digest
    };
    private static Task<TransferResult> Run(TransferRequest request, CancellationToken ct = default) => new ResumableTransferClient().ExecuteAsync(request, ct);
    private static string Part(TransferRequest request) => System.IO.Path.Combine(request.WorkspaceDirectory, TransferLimits.PartialFileName);
    private static string State(TransferRequest request) => System.IO.Path.Combine(request.WorkspaceDirectory, TransferLimits.StateFileName);
    private static async Task Checkpoint(TransferRequest request)
    {
        TransferResult result = await Run(request with { StopAfterCommittedBytes = 10 });
        Check(result.Status == TransferStatus.Interrupted && result.CommittedBytes == 10);
        Equal(File.ReadAllBytes(Part(request)), "0123456789"u8.ToArray());
    }
    private static void Preserved(TransferRequest request, byte[] state, byte[] partial)
    {
        Equal(File.ReadAllBytes(State(request)), state);
        Equal(File.ReadAllBytes(Part(request)), partial);
        Check(!File.Exists(request.DestinationPath));
        Check(Directory.GetFileSystemEntries(request.WorkspaceDirectory).Length == 3);
    }

    private static async Task Happy()
    {
        using OwnedRoot root = new();
        await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync();
        TransferRequest request = Request(root, server);
        Check(Convert.ToHexStringLower(SHA256.HashData(Bytes)) == Digest);
        await Checkpoint(request);
        WorkspaceInspectionResult inspection = ResumableTransferClient.InspectWorkspace(request.WorkspaceDirectory);
        Check(inspection.IsConsistent && inspection.State?.CommittedBytes == 10 && inspection.State.ExpectedSha256 == Digest);
        TransferResult complete = await Run(request);
        Check(complete.Status == TransferStatus.Completed && complete.WasResumed && complete.VerifiedSha256 == Digest);
        Equal(File.ReadAllBytes(request.DestinationPath), Bytes);
        IReadOnlyList<RecordedHttpRequest> requests = server.RecordedRequests;
        Check(requests.Count == 2 && requests[0].RangeHeader is null && requests[0].IfRangeHeader is null);
        Check(requests[1].RangeHeader == "bytes=10-" && requests[1].IfRangeHeader == "\"lab-artifact-32b-v1\"" &&
            requests[1].ContentRangeHeader == "bytes 10-31/32" && requests[1].ResponseStatusCode == 206);
        TransferResult republish = await Run(request with { DestinationPath = root.At("second.bin") });
        Check(republish.Status == TransferStatus.Completed && server.RecordedRequests.Count == 2);
        Equal(File.ReadAllBytes(root.At("second.bin")), Bytes);
        Failed(await Run(request), TransferErrorCode.DestinationAlreadyExists);
        Equal(File.ReadAllBytes(request.DestinationPath), Bytes);
    }

    private static async Task Changed()
    {
        using OwnedRoot root = new();
        await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync();
        TransferRequest request = Request(root, server);
        await Checkpoint(request);
        byte[] state = File.ReadAllBytes(State(request));
        byte[] partial = File.ReadAllBytes(Part(request));
        server.UpdateRepresentation(Enumerable.Repeat((byte)'Z', 32).ToArray(), "\"changed\"");
        Failed(await Run(request), TransferErrorCode.ValidatorChangedOrFullResponseRefused);
        Check(server.RecordedRequests[^1].ResponseStatusCode == 200);
        Preserved(request, state, partial);
    }

    private static async Task InitialFaults()
    {
        (FaultInjectionMode Mode, TransferErrorCode Code)[] cases =
        [
            (FaultInjectionMode.Redirect302, TransferErrorCode.RedirectRefused),
            (FaultInjectionMode.WeakETagOnInitial, TransferErrorCode.MissingOrWeakETag),
            (FaultInjectionMode.DuplicateETagOnInitial, TransferErrorCode.MissingOrWeakETag),
            (FaultInjectionMode.TruncatedBodyOnInitial, TransferErrorCode.TruncatedBody),
            (FaultInjectionMode.OversizedBodyOnInitial, TransferErrorCode.OversizedBody),
            (FaultInjectionMode.UnexpectedContentEncodingGzip, TransferErrorCode.UnexpectedContentEncoding),
            (FaultInjectionMode.LargeResponseHeader, TransferErrorCode.TransferTimeoutOrNetworkError)
        ];
        foreach ((FaultInjectionMode mode, TransferErrorCode code) in cases)
        {
            using OwnedRoot root = new();
            await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync(faultMode: mode);
            TransferRequest request = Request(root, server);
            Failed(await Run(request), code);
            Check(!File.Exists(request.DestinationPath) && !File.Exists(Part(request)) && !File.Exists(State(request)));
            Check(Directory.GetFileSystemEntries(request.WorkspaceDirectory).Length == 1);
            Check(server.RecordedRequests.Count == 1); // A redirect must not reach even its local destination.
        }
    }

    private static async Task ResumeFaults()
    {
        (FaultInjectionMode Mode, TransferErrorCode Code)[] cases =
        [
            (FaultInjectionMode.Redirect302, TransferErrorCode.RedirectRefused),
            (FaultInjectionMode.AlwaysReturn200IgnoreRange, TransferErrorCode.ValidatorChangedOrFullResponseRefused),
            (FaultInjectionMode.WeakETagOnResume, TransferErrorCode.MissingOrWeakETag),
            (FaultInjectionMode.DuplicateETagOnResume, TransferErrorCode.MissingOrWeakETag),
            (FaultInjectionMode.MismatchedETagOn206, TransferErrorCode.ETagMismatchOnPartial),
            (FaultInjectionMode.WrongContentRangeStart, TransferErrorCode.InvalidContentRange),
            (FaultInjectionMode.WrongContentRangeEnd, TransferErrorCode.InvalidContentRange),
            (FaultInjectionMode.WrongContentRangeTotal, TransferErrorCode.InvalidContentRange),
            (FaultInjectionMode.OverflowContentRangeTotal, TransferErrorCode.NumericOverflow),
            (FaultInjectionMode.MissingContentRangeOn206, TransferErrorCode.InvalidContentRange),
            (FaultInjectionMode.TruncatedBodyOnResume, TransferErrorCode.TruncatedBody),
            (FaultInjectionMode.OversizedBodyOnResume, TransferErrorCode.OversizedBody),
            (FaultInjectionMode.ContentLengthMismatchOnResume, TransferErrorCode.ContentLengthMismatch),
            (FaultInjectionMode.UnexpectedContentEncodingGzip, TransferErrorCode.UnexpectedContentEncoding),
            (FaultInjectionMode.MultipartByteRangesOn206, TransferErrorCode.MultipartNotSupported)
        ];
        foreach ((FaultInjectionMode mode, TransferErrorCode code) in cases)
        {
            using OwnedRoot root = new();
            await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync();
            TransferRequest request = Request(root, server);
            await Checkpoint(request);
            byte[] state = File.ReadAllBytes(State(request));
            byte[] part = File.ReadAllBytes(Part(request));
            server.SetFaultMode(mode);
            Failed(await Run(request), code);
            Preserved(request, state, part);
            Check(server.RecordedRequests.Count == 2 && server.RecordedRequests[^1].RangeHeader == "bytes=10-");
        }
    }

    private static async Task IntermediateFaults()
    {
        foreach (FaultInjectionMode mode in new[] { FaultInjectionMode.TruncatedBodyOnResume, FaultInjectionMode.OversizedBodyOnResume })
        {
            using OwnedRoot root = new();
            await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync();
            TransferRequest request = Request(root, server);
            await Checkpoint(request);
            byte[] state = File.ReadAllBytes(State(request));
            byte[] partial = File.ReadAllBytes(Part(request));
            server.SetFaultMode(mode);
            Failed(await Run(request with { StopAfterCommittedBytes = 15 }),
                mode == FaultInjectionMode.TruncatedBodyOnResume ? TransferErrorCode.TruncatedBody : TransferErrorCode.OversizedBody);
            Preserved(request, state, partial);
        }
    }

    private static async Task StateFailures()
    {
        using OwnedRoot root = new();
        await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync();
        TransferRequest request = Request(root, server);
        await Checkpoint(request);
        string original = File.ReadAllText(State(request));
        string With(string key, JsonNode? value)
        {
            JsonNode node = JsonNode.Parse(original)!;
            node[key] = value;
            return node.ToJsonString();
        }
        (byte[] Data, TransferErrorCode Code)[] cases =
        [
            (Encoding.UTF8.GetBytes(original.Insert(1, "\"schema\\u0056ersion\":1,")), TransferErrorCode.DuplicateStateKey),
            (Encoding.UTF8.GetBytes(With("schemaVersion", JsonValue.Create(2))), TransferErrorCode.UnsupportedStateVersion),
            (Encoding.UTF8.GetBytes(With("unknown", JsonValue.Create(1))), TransferErrorCode.InvalidStateFormat),
            (Encoding.UTF8.GetBytes(With("artifactId", null)), TransferErrorCode.InvalidStateFormat),
            (Encoding.UTF8.GetBytes(original.Replace("\"committedBytes\": 10", "\"committedBytes\": 999999999999999999999999")), TransferErrorCode.NumericOverflow),
            (Encoding.UTF8.GetBytes(original[..^1]), TransferErrorCode.InvalidStateFormat),
            (Encoding.UTF8.GetBytes(original + " {}"), TransferErrorCode.InvalidStateFormat),
            (Encoding.UTF8.GetBytes("{\"\\uD800\":1}"), TransferErrorCode.InvalidStateFormat),
            (Encoding.UTF8.GetBytes(original.Replace("synthetic-32b", "\\uD800")), TransferErrorCode.InvalidStateFormat),
            // The artifactId string contains raw FF, which is not UTF-8. Test
            // bytes directly so Encoding.UTF8 cannot replace the invalid input.
            (Convert.FromHexString("7b2261727469666163744964223a22ff227d"), TransferErrorCode.InvalidStateFormat),
            (new byte[TransferLimits.MaxStateFileBytes + 1], TransferErrorCode.LimitExceeded)
        ];
        foreach ((byte[] data, TransferErrorCode code) in cases)
        {
            Check(!StateSerializer.TryParse(data, out _, out TransferError? error) && error?.Code == code);
            File.WriteAllBytes(State(request), data);
            byte[] part = File.ReadAllBytes(Part(request));
            Failed(await Run(request), code);
            Preserved(request, data, part);
            Check(server.RecordedRequests.Count == 1);
        }
        File.WriteAllText(State(request), original);
        File.WriteAllText(System.IO.Path.Combine(request.WorkspaceDirectory, TransferLimits.StateStagingFileName), "incomplete checkpoint");
        Failed(await Run(request), TransferErrorCode.AmbiguousLocalState);
        Check(File.ReadAllText(State(request)) == original);
    }

    private static async Task Tampering()
    {
        for (int mode = 0; mode < 4; mode++)
        {
            using OwnedRoot root = new();
            await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync();
            TransferRequest request = Request(root, server);
            await Checkpoint(request);
            if (mode == 0) File.WriteAllBytes(Part(request), "X123456789"u8.ToArray());
            if (mode == 1) File.AppendAllText(Part(request), "X");
            if (mode == 2)
            {
                JsonNode state = JsonNode.Parse(File.ReadAllText(State(request)))!;
                state["committedBytes"] = 9;
                File.WriteAllText(State(request), state.ToJsonString());
            }
            if (mode == 3) File.Delete(Part(request));
            byte[] saved = File.ReadAllBytes(State(request));
            byte[]? part = File.Exists(Part(request)) ? File.ReadAllBytes(Part(request)) : null;
            Failed(await Run(request), TransferErrorCode.AmbiguousLocalState);
            Check(!ResumableTransferClient.InspectWorkspace(request.WorkspaceDirectory).IsConsistent);
            Equal(File.ReadAllBytes(State(request)), saved);
            if (part is not null) Equal(File.ReadAllBytes(Part(request)), part);
            Check(server.RecordedRequests.Count == 1 && !File.Exists(request.DestinationPath));
        }
    }

    private static async Task DigestAndDestination()
    {
        using OwnedRoot root = new();
        await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync();
        TransferRequest request = Request(root, server) with { ExpectedSha256 = new string('0', 64) };
        Failed(await Run(request), TransferErrorCode.DigestMismatch);
        Check(!File.Exists(request.DestinationPath));
        Equal(File.ReadAllBytes(Part(request)), Bytes);
        Check(ResumableTransferClient.InspectWorkspace(request.WorkspaceDirectory).IsConsistent);
        using OwnedRoot second = new();
        TransferRequest existing = Request(second, server);
        byte[] sentinel = "caller-owned"u8.ToArray();
        File.WriteAllBytes(existing.DestinationPath, sentinel);
        Failed(await Run(existing), TransferErrorCode.DestinationAlreadyExists);
        Equal(File.ReadAllBytes(existing.DestinationPath), sentinel);
        Equal(File.ReadAllBytes(Part(existing)), Bytes); // Even after a successful digest check, never replace it.
        Directory.CreateDirectory(second.At("directory-target"));
        Failed(await Run(existing with { DestinationPath = second.At("directory-target") }), TransferErrorCode.DestinationAlreadyExists);
    }

    private static Task PublicationFailures()
    {
        using OwnedRoot root = new();
        string source = root.At("source");
        string destination = root.At("final");
        File.WriteAllBytes(source, Bytes);
        bool failed = false;
        try
        {
            TransferStorage.Publish(source, destination, 32, Digest, CancellationToken.None, (_, output, _) =>
            {
                output.Write("012"u8);
                throw new IOException("Synthetic mid-copy failure.");
            });
        }
        catch (IOException) { failed = true; }
        Check(failed && !File.Exists(destination));
        Check(Directory.GetFiles(root.Path).Length == 1);
        Equal(File.ReadAllBytes(source), Bytes);
        failed = false;
        try
        {
            TransferStorage.Publish(source, destination, 32, Digest, CancellationToken.None, (input, output, _) =>
            {
                input.CopyTo(output);
                File.WriteAllText(destination, "racing caller data");
            });
        }
        catch (TransferFailure failure) { failed = failure.Error.Code == TransferErrorCode.DestinationAlreadyExists; }
        Check(failed && File.ReadAllText(destination) == "racing caller data");
        Check(Directory.GetFiles(root.Path).Length == 2);
        File.Delete(destination);
        failed = false;
        try
        {
            TransferStorage.Publish(source, destination, 32, Digest, CancellationToken.None, (_, output, _) => output.Write(new byte[32]));
        }
        catch (TransferFailure failure) { failed = failure.Error.Code == TransferErrorCode.DigestMismatch; }
        Check(failed && !File.Exists(destination) && Directory.GetFiles(root.Path).Length == 1);
        return Task.CompletedTask;
    }

    private static async Task Boundaries()
    {
        foreach (byte[] payload in new[] { Array.Empty<byte>(), new byte[] { 0x71 }, Bytes })
        {
            using OwnedRoot root = new();
            await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync(initialBytes: payload);
            TransferRequest request = Request(root, server) with
            {
                ExpectedLength = payload.Length, ExpectedSha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
                StopAfterCommittedBytes = payload.Length == 1 ? 0 : payload.Length, ChunkSizeBytes = 1
            };
            TransferResult result = await Run(request);
            if (payload.Length == 1)
            {
                Check(result.Status == TransferStatus.Interrupted && new FileInfo(Part(request)).Length == 0);
                result = await Run(request with { StopAfterCommittedBytes = null });
                Check(server.RecordedRequests[^1].RangeHeader == "bytes=0-" && server.RecordedRequests[^1].ContentRangeHeader == "bytes 0-0/1");
            }
            Check(result.Status == TransferStatus.Completed);
            Equal(File.ReadAllBytes(request.DestinationPath), payload);
        }
    }

    private static async Task MultipleStops()
    {
        using OwnedRoot root = new();
        await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync();
        TransferRequest request = Request(root, server);
        await Checkpoint(request);
        Check((await Run(request with { StopAfterCommittedBytes = 10 })).Status == TransferStatus.Interrupted);
        Check(server.RecordedRequests.Count == 1);
        TransferResult intermediate = await Run(request with { StopAfterCommittedBytes = 15 });
        Check(intermediate.Status == TransferStatus.Interrupted && intermediate.CommittedBytes == 15);
        Equal(File.ReadAllBytes(Part(request)), Encoding.ASCII.GetBytes(Text[..15]));
        Check(ResumableTransferClient.InspectWorkspace(request.WorkspaceDirectory).IsConsistent);
        Check((await Run(request)).Status == TransferStatus.Completed);
        Check(server.RecordedRequests[^1].RangeHeader == "bytes=15-");
        Equal(File.ReadAllBytes(request.DestinationPath), Bytes);
    }

    private static async Task Guards()
    {
        string[] invalidUrls =
        [
            "http://localhost:1234/artifact", "http://127.0.0.2:1234/artifact", "https://127.0.0.1:1234/a",
            "http://127.0.0.1/a", "http://127.0.0.1:0/a", "http://127.0.0.1:65536/a",
            "http://127.1:1234/a", "http://2130706433:1234/a", "http://127.0.0.1:1234/a?",
            "http://127.0.0.1:1234/a#", "http://user:secret@127.0.0.1:1234/a", " http://127.0.0.1:1234/a",
            "http://[::ffff:127.0.0.1]:1234/a", "http://[::1%25lo]:1234/a", "http://127.0.0.1:1234\\evil/a"
        ];
        foreach (string url in invalidUrls) Check(!TransferGuards.ValidateLoopbackHttpUrl(url, out _, out _));
        Check(TransferGuards.ValidateLoopbackHttpUrl("http://127.0.0.1:80/a", out _, out _));
        Check(TransferGuards.ValidateLoopbackHttpUrl("http://[::1]:1234/a", out _, out _));
        Check(!TransferGuards.ValidateStrongETag("W/\"weak\"", out _, out _));
        Check(!TransferGuards.ValidateStrongETag("\"\"", out _, out _));
        Check(!TransferGuards.ValidateStrongETag("\"a\", \"b\"", out _, out _));
        Check(!TransferGuards.ValidateStrongETag("\"a\nb\"", out _, out _));
        using OwnedRoot root = new();
        await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync();
        TransferRequest request = Request(root, server);
        (TransferRequest Request, TransferErrorCode Code)[] invalid =
        [
            (request with { ExpectedLength = long.MaxValue }, TransferErrorCode.LimitExceeded),
            (request with { ExpectedLength = -1 }, TransferErrorCode.InvalidRequest),
            (request with { ChunkSizeBytes = int.MaxValue }, TransferErrorCode.LimitExceeded),
            (request with { ChunkSizeBytes = 0 }, TransferErrorCode.InvalidRequest),
            (request with { StopAfterCommittedBytes = long.MaxValue }, TransferErrorCode.InvalidRequest),
            (request with { Timeout = TimeSpan.MaxValue }, TransferErrorCode.LimitExceeded),
            (request with { Timeout = TimeSpan.Zero }, TransferErrorCode.LimitExceeded),
            (request with { ArtifactId = "unsafe\nidentifier" }, TransferErrorCode.InvalidRequest),
            (request with { ExpectedSha256 = null! }, TransferErrorCode.InvalidRequest),
            (request with { SourceUrl = null! }, TransferErrorCode.NonLoopbackOrInvalidUrl)
        ];
        foreach ((TransferRequest bad, TransferErrorCode code) in invalid) Failed(await Run(bad), code);
        Check(!Directory.Exists(request.WorkspaceDirectory) && server.RecordedRequests.Count == 0);
        Check(TransferGuards.ValidateSha256Hex(Digest.ToUpperInvariant(), out string normalized, out _) && normalized == Digest);
    }

    private static async Task Paths()
    {
        using OwnedRoot root = new();
        await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync();
        TransferRequest request = Request(root, server);
        Failed(await Run(request with { DestinationPath = System.IO.Path.Combine(request.WorkspaceDirectory, "final") }), TransferErrorCode.WorkspacePathUnsafe);
        Failed(await Run(request with { WorkspaceDirectory = root.At("x/../workspace") }), TransferErrorCode.WorkspacePathUnsafe);
        Directory.CreateDirectory(request.WorkspaceDirectory);
        Failed(await Run(request), TransferErrorCode.AmbiguousLocalState);
        Check(Directory.GetFileSystemEntries(request.WorkspaceDirectory).Length == 0);
        Directory.Delete(request.WorkspaceDirectory);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(root.At("real/deep"));
            Directory.CreateSymbolicLink(root.At("alias"), root.At("real"));
            Failed(await Run(request with { WorkspaceDirectory = root.At("alias/deep/workspace") }), TransferErrorCode.WorkspacePathUnsafe);
            Failed(await Run(request with { DestinationPath = root.At("alias/deep/final") }), TransferErrorCode.WorkspacePathUnsafe);
            Check(!Directory.Exists(root.At("real/deep/workspace")));
            await Checkpoint(request);
            string outside = root.At("outside");
            File.WriteAllText(outside, "0123456789");
            File.Delete(Part(request));
            File.CreateSymbolicLink(Part(request), outside);
            Failed(await Run(request), TransferErrorCode.WorkspacePathUnsafe);
            Check(!ResumableTransferClient.InspectWorkspace(request.WorkspaceDirectory).IsConsistent);
            Check(File.ReadAllText(outside) == "0123456789");
        }
    }

    private static async Task CrossProcessLock()
    {
        using OwnedRoot root = new();
        await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync();
        TransferRequest request = Request(root, server);
        await Checkpoint(request);
        ProcessStartInfo info = new(Environment.ProcessPath!)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        if (System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("hold-lock");
        info.ArgumentList.Add(System.IO.Path.Combine(request.WorkspaceDirectory, TransferLimits.LockFileName));
        using Process child = Process.Start(info)!;
        try
        {
            Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ready");
            byte[] saved = File.ReadAllBytes(State(request));
            Failed(await Run(request), TransferErrorCode.ConcurrentTransferNotAllowed);
            Check(ResumableTransferClient.InspectWorkspace(request.WorkspaceDirectory).Error?.Code == TransferErrorCode.ConcurrentTransferNotAllowed);
            Equal(File.ReadAllBytes(State(request)), saved);
            Check(server.RecordedRequests.Count == 1);
        }
        finally
        {
            child.StandardInput.Close();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
        Check(child.ExitCode == 0 && (await Run(request)).Status == TransferStatus.Completed);
    }

    private static async Task Cancellation()
    {
        using OwnedRoot root = new();
        await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync(faultMode: FaultInjectionMode.DelayedResponse);
        TransferRequest request = Request(root, server);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        Failed(await Run(request, canceled.Token), TransferErrorCode.TransferTimeoutOrNetworkError);
        Check(!Directory.Exists(request.WorkspaceDirectory));
        using CancellationTokenSource running = new();
        Task<TransferResult> first = Run(request, running.Token);
        for (int i = 0; i < 200 && server.RecordedRequests.Count == 0; i++) await Task.Delay(10);
        Check(server.RecordedRequests.Count > 0);
        Failed(await Run(request with { WorkspaceDirectory = root.At("second") }), TransferErrorCode.ConcurrentTransferNotAllowed);
        running.Cancel();
        Failed(await first, TransferErrorCode.TransferTimeoutOrNetworkError);
        Failed(await Run(request with { Timeout = TimeSpan.FromMilliseconds(100) }), TransferErrorCode.TransferTimeoutOrNetworkError);
        Check(!File.Exists(State(request)) && !File.Exists(Part(request)));
        server.SetFaultMode(FaultInjectionMode.None);
        Check((await Run(request)).Status == TransferStatus.Completed); // The gate and lock must have been released.
    }

    private sealed class BrokenWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(string? value) => throw new IOException("Synthetic output failure.");
        public override void WriteLine(string? value) => throw new IOException("Synthetic output failure.");
    }
    private static async Task Cli()
    {
        TextWriter oldOut = Console.Out;
        TextWriter oldError = Console.Error;
        using StringWriter output = new();
        using StringWriter error = new();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            Check(await CliProgram.Main(["private-path\nsecret"]) == 1);
            Check(await CliProgram.Main(["inspect-state", "--workspace", "x", "--workspace", "y"]) == 1);
            Check(await CliProgram.Main(["transfer", "--expected-length", "999999999999999999999999"]) == 1);
            Check(!error.ToString().Contains("secret", StringComparison.Ordinal) && !error.ToString().Contains("private-path", StringComparison.Ordinal));
            using OwnedRoot root = new();
            await using LoopbackArtifactServer server = await LoopbackArtifactServer.StartAsync(faultMode: FaultInjectionMode.Redirect302);
            Check(await CliProgram.Main(["transfer", "--url", server.ArtifactUrl, "--artifact-id", "synthetic-32b",
                "--workspace", root.At("workspace"), "--destination", root.At("final"), "--expected-length", "32",
                "--expected-sha256", Digest, "--json"]) == 2);
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            Check(document.RootElement.GetProperty("status").GetString() == "failed" &&
                document.RootElement.GetProperty("error").GetProperty("code").GetString() == "redirect_refused");
            Check(!output.ToString().Contains(root.Path, StringComparison.Ordinal));
            Console.SetOut(new BrokenWriter()); Console.SetError(new BrokenWriter());
            Check(await CliProgram.Main(["--help"]) == 2);
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    private static async Task Demo()
    {
        DemoLabReport first = await DemoRunner.RunLocalLabAsync();
        DemoLabReport second = await DemoRunner.RunLocalLabAsync();
        Check(DemoRunner.FormatJson(first) == DemoRunner.FormatJson(second));
        Check(first.ResumeAndVerifyScenario.FinalText == Text && first.ResumeAndVerifyScenario.VerifiedSha256 == Digest &&
            first.ChangedETagRefusalScenario.RefusalCode == "validator_changed_or_full_response_refused" &&
            !first.ChangedETagRefusalScenario.DestinationCreated);
        using JsonDocument json = JsonDocument.Parse(DemoRunner.FormatJson(first));
        Check(json.RootElement.GetProperty("version").GetString() == "0.1.0");
        Check(DemoRunner.FormatHumanReadable(first).Contains("bytes 10-31/32", StringComparison.Ordinal));
    }
}
