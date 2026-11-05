# Resumable Transfer Lab

**Development version 0.1.0 — Lofrano Tecnologia.** An original MIT-licensed .NET utility and actual local HTTP laboratory. It checkpoints an orderly interruption, reopens persisted state, resumes with a strong ETag, and publishes a new file only after checking the caller's SHA-256.

[Português](README.pt-BR.md) · [Precise contract](docs/contract.md) · [Design](docs/design.md) · [Official homepage](https://lofranotecnologia.com.br)

This is not a production download client, an origin-authenticity proof, a malware check, a filesystem sandbox, or arbitrary crash/power-loss recovery. Only the documented loopback single-range subset is implemented; this is not a general HTTP standards-compliance claim.

## Install, prepare, test, demo

Prerequisites: Bash and the installed **.NET SDK 10.0.403**, including its .NET/ASP.NET Core shared frameworks. `global.json` deliberately disables SDK roll-forward. No NuGet runtime or test packages are used. Obtain the SDK separately using official .NET distribution instructions; the scripts do not install it.

From the project directory:

```sh
bash scripts/prepare.sh
bash scripts/test.sh
bash scripts/demo.sh --text
bash scripts/demo.sh --json
```

Preparation explicitly restores all three projects with cleared package sources. Tests/demo **never restore**: they build with `--no-restore`, then execute DLLs directly. Missing prepared assets fail rather than fetching dependencies. SDK homes, packages, intermediates and outputs stay under ignored `build/`, not the host HOME. The test harness is a dependency-free console executable, not `dotnet test`. Checks require permission to bind ephemeral loopback ports and create owned temporary directories; no outside network is needed after SDK preparation. CI separates restore from these checks but is not itself a network sandbox.

Example deterministic text output:

```text
Resumable Transfer Lab 0.1.0 (development)
Local synthetic HTTP; expected-byte integrity, not origin authenticity.
Checkpoint: 10/32 bytes (0123456789)
Resume: Range bytes=10-; If-Range "lab-artifact-32b-v1"
Response: 206; Content-Range bytes 10-31/32
Final: 0123456789abcdefghijklmnopqrstuv
SHA-256: 73337f479fe170d73e53e247f3052e4243cc9c2a0ffa621853d9385c619efb77
Published: True
Changed ETag: HTTP 200; validator_changed_or_full_response_refused
Preserved: 10 bytes (0123456789); destination created: False
```

The JSON report contains `version`, `resumeAndVerifyScenario`, and `changedETagRefusalScenario`. For example, `resumeAndVerifyScenario.interruptedAtBytes` is `10`, `resumeRangeHeader` is `bytes=10-`, and `changedETagRefusalScenario.destinationCreated` is `false`. `--json` writes only JSON to stdout; build messages go to stderr. `--both` combines text and JSON and is not a JSON document.

## CLI workflow

After building, use this shell function to keep commands short:

```sh
rtl() { dotnet build/bin/ResumableTransferLab.Cli/Release/net10.0/ResumableTransferLab.Cli.dll "$@"; }
rtl --help
rtl fixture
```

`fixture` prints the actual ephemeral URL, artifact ID, length and digest as JSON. Leave it running in the first terminal; Enter or EOF closes it. In another terminal, set `URL` to that printed URL and choose **absent** workspace/output paths under a trusted directory:

```sh
rtl transfer --url "$URL" --artifact-id synthetic-32b --workspace ./lab-workspace --destination ./lab-output.bin --expected-length 32 --expected-sha256 73337f479fe170d73e53e247f3052e4243cc9c2a0ffa621853d9385c619efb77 --stop-after-bytes 10 --json
rtl inspect-state --workspace ./lab-workspace --json
rtl transfer --url "$URL" --artifact-id synthetic-32b --workspace ./lab-workspace --destination ./lab-output.bin --expected-length 32 --expected-sha256 73337f479fe170d73e53e247f3052e4243cc9c2a0ffa621853d9385c619efb77 --json
```

Exit codes: `0` successful completion, orderly interruption, consistent inspection or successful demo; `1` CLI syntax error; `2` transfer refusal, inconsistent inspection or operational/output failure. No command removes caller workspaces or replaces a destination. The demo/tests remove only their own temporary roots.

## Library example

Reference `src/ResumableTransferLab/ResumableTransferLab.csproj` from a .NET 10 project. In an async method:

```csharp
await using var server = await ResumableTransferLab.Fixture.LoopbackArtifactServer.StartAsync();
var request = new ResumableTransferLab.TransferRequest
{
    ArtifactId = "synthetic-32b",
    SourceUrl = server.ArtifactUrl,
    WorkspaceDirectory = "owned-new-workspace",
    DestinationPath = "new-output.bin",
    ExpectedLength = 32,
    ExpectedSha256 = "73337f479fe170d73e53e247f3052e4243cc9c2a0ffa621853d9385c619efb77",
    StopAfterCommittedBytes = 10
};
var first = await new ResumableTransferLab.ResumableTransferClient().ExecuteAsync(request);
var resumed = await new ResumableTransferLab.ResumableTransferClient().ExecuteAsync(
    request with { StopAfterCommittedBytes = null });
```

Check each result's `Status` and `Error.ContractCode`; do not assume success. A null request throws `ArgumentNullException`; ordinary request, transport and local I/O failures return structured failures. Inspection does not repair files.

## Why this design / limits

Responses are inspected before reading bodies. Resume stages and validates the entire remaining body before appending; this deliberately trades extra local disk use for a clear no-mixed-content rule. State includes a prefix digest as well as an offset. An OS-held marker file excludes cooperating processes from the same workspace. Publication copies into an exclusively created same-parent stage, verifies that stage, then moves it without overwrite.

Caps: artifact 64 MiB, JSON state 64 KiB, chunks 64 KiB, response headers 16 KiB, default deadline 15 seconds (maximum 120). One transfer per process; one cooperating owner per workspace. Resume can temporarily require roughly three artifact-sized files across workspace and destination staging. No unbounded retries, redirects, proxies, DNS names, credentials, uploads, decompression or multipart transfers.

An initial orderly stop validates only the received prefix, not an unread suffix. Local append/state failures can leave an ambiguous workspace; stages are evidence, not something to delete automatically. Completed workspaces retain their partial/state files and can publish another new destination without HTTP. Ordinary filesystem operations are not a hard real-time deadline. Ancestor checks reject visible symlinks/reparse points, but require a trusted regular-file filesystem with no hostile replacement races, hard-link aliases or device files. The tool is not a multi-user security boundary.

Reports and state can retain supplied identifiers, validators and loopback URLs. Escaping is not anonymization. No telemetry, tracking, model or external API is used at runtime.
