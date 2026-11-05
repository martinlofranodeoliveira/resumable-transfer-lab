# Development 0.1.0 contract

This document defines the supported API/CLI subset for this development version. It is not a released-version or general HTTP compliance claim. All artifact fixtures are synthetic.

## Public API

`ResumableTransferClient.ExecuteAsync(TransferRequest, CancellationToken)` returns a `TransferResult` with `Completed`, `Interrupted` or `Failed`. Only `Completed` has `DestinationPublished = true` and a non-null `VerifiedSha256`. `Interrupted` is an intentional committed checkpoint, not an arbitrary cancellation. `Failed` never claims publication. Counts describe the last successfully recorded checkpoint known to that invocation; they do not repair evidence after local I/O failure. A null request throws `ArgumentNullException`. Programming errors/resource exhaustion are not converted into success or promised recoverable failures.

`InspectWorkspace(string)` acquires exclusive cooperating ownership, validates state and hashes the entire committed partial. It returns consistency or a structured error, never repairs or truncates files. Failed inspections need not include parsed state/length. `TransferGuards` expose the same input predicates. `StateSerializer.TryParse` accepts bounded UTF-8 state; serialization produces the eight-field schema from a typed state, while parsing is the validation boundary. Direct serializer callers must supply a non-null, semantically valid typed state.

Required request fields: artifact ID, source URL, workspace directory, destination file, expected byte length, expected SHA-256. Optional: absolute stop boundary, chunk size, timeout. Length is 0 through 67,108,864 inclusive. Chunk is 1 through 65,536. Stop is 0 through expected length inclusive. Timeout defaults to 15 seconds and must be positive and at most 120 seconds. The deadline/cancellation is cooperative, checked through request/body/local copy/hash steps; synchronous filesystem operations are not preemptible hard real-time operations.

IDs contain 1–128 ASCII letters/digits/dot/underscore/hyphen and cannot equal `.` or `..`. Digests contain exactly 64 ASCII hex characters, normalized lowercase. A supported ETag is a non-empty quoted printable ASCII tag, at most 256 supplied characters, excluding embedded quote, backslash and whitespace inside the tag. Optional surrounding HTTP SP/HTAB is trimmed. Weak, wildcard, empty, multiple and unsupported validators are refused. This intentionally excludes valid HTTP ETag forms outside that subset.

## URL and HTTP

The supplied URL is at most 512 characters, starts with exact lowercase `http://`, and has authority `127.0.0.1:PORT` or `[::1]:PORT`. Port is 1–65535, supplied explicitly using 1–5 decimal digits. No DNS names, alternate numeric IP spellings, scoped/mapped IP addresses, credentials, query/fragment delimiters, backslashes, controls or whitespace. `Uri.AbsoluteUri` is the persisted identity; default-port/path canonicalization can therefore affect identity comparisons.

GET only. Redirects, proxies, cookies, credentials and automatic decompression are disabled. Response headers are capped at 16 KiB by `SocketsHttpHandler`. Initial request has no Range/If-Range and requires 200, one supported strong ETag, no Content-Range, no Content-Encoding (even identity), and no multipart media type. Optional Content-Length must equal the full expected length.

Resume sends exactly `Range: bytes=OFFSET-` and `If-Range: "persisted-tag"`. Before append it requires 206, the identical strong ETag, no encoding/multipart, and exactly one Content-Range describing `bytes OFFSET-(LENGTH-1)/LENGTH`. Bounds are non-negative decimal integers within Int64. The lowercase bytes unit is required. Optional Content-Length must equal `LENGTH-OFFSET`. A 200 full replacement is always refused, not restarted or appended.

Initial intentional interruption reads only the requested prefix and closes the response. Thus an unread tail is not validated. Initial completion and every resumed response read the exact expected response bytes plus at most one EOF-probe byte. Truncated/oversized bodies are refused before commit. A resumed response is staged and validated in full even if only a smaller prefix will be committed for another orderly stop. Wire bytes outside the HTTP-framed body are not represented as body bytes by HttpClient; this is not an HTTP request-smuggling detector.

## Workspace and state

A first invocation requires an absent workspace leaf in a trusted filesystem. It creates that directory and an exclusive `transfer.lock` ownership marker containing `Resumable Transfer Lab workspace 1` plus LF. Existing empty/unmarked directories are not adopted. The marker persists, but exclusivity is the OS-held `FileShare.None` handle, not marker presence. Cooperating processes attempting the same workspace are refused. A process-wide gate independently permits only one transfer. Workspace-lock acquisition I/O failures are conservatively reported as `concurrent_transfer_not_allowed`.

Reusable layouts are marker only (no checkpoint yet), or exactly marker + `transfer-state.json` + `artifact.part`. Marker-only workspaces left by initial transport refusal may retry. Unexpected entries, directories, leftover `artifact.resume.stage` or `transfer-state.next`, missing members, malformed state, or inconsistent partials fail closed. Enumeration stops on the first unexpected entry or after the small permitted entry count. State reads allocate at most 65,537 input bytes and probe past the 64 KiB limit, rather than trusting a prior stat.

State schema 1 has exactly these required, case-sensitive properties:

| Property | Type / meaning |
| --- | --- |
| schemaVersion | integer 1 |
| artifactId | validated identity |
| sourceUrl | normalized loopback URL |
| strongETag | supported strong ETag |
| expectedLength | bounded non-negative integer |
| expectedSha256 | normalized expected digest |
| committedBytes | integer 0 through expectedLength |
| committedPrefixSha256 | SHA-256 of exactly the committed partial |

Unknown properties/versions, duplicate decoded property names (including escaped duplicates), invalid UTF-8, unpaired escaped surrogates, comments, trailing commas/tokens, missing/null/wrong-type fields and excess nesting are refused. Numbers that cannot be represented by the required integer parser produce `numeric_overflow`, including fractional/exponent forms not accepted by that integer parser. Negative state lengths are invalid; committed offset above total is ambiguous. A schema error is reported without rewriting checkpoint/partial files.

Reuse first checks exact request identity, partial length and prefix hash. Offset/length or byte mismatches produce `ambiguous_local_state`. Prefix hashes detect accidental or partial tampering, not a malicious party able to rewrite both state and bytes. The final caller digest remains mandatory.

Body stage is exclusively created. Network/header/body refusals preserve the saved checkpoint and partial; only this invocation's uncommitted body stage is cleaned up best-effort. Append flushes before a new state is serialized into exclusively created `transfer-state.next`, flushed, closed and moved over the old state. Local write/cancellation failures during these transitions can leave a partial with an old checkpoint and/or stages; these are refused on reopening, never guessed, truncated or automatically rolled back. Orderly success closes/flushes all state. This is not arbitrary crash/power-loss recovery or a filesystem durability guarantee.

A stop at the current or earlier offset returns the existing interruption without HTTP. A stop equal to total completes. Empty artifacts require initial 200/strong ETag, exact empty body and the empty digest, then publish. A completed workspace re-verifies and can publish a different new destination without HTTP. State does not bind the destination.

## Paths and publication

Paths have at most 4096 characters, no controls or parent-traversal components. Roots cannot be workspaces. Destination must be outside and distinct from the workspace (case-insensitive comparison on Windows); Windows alternate streams are excluded. Existing ancestors, marker/state/partial/stage paths and destination are checked for symlink/reparse points, including deep ancestors and dangling links where detectable.

This is not a sandbox: callers must provide trusted regular-file paths and prevent concurrent malicious path replacement, hard-link aliasing, devices/FIFOs, mount changes and external file mutation. No portable BCL-only check makes these ordinary path operations race-proof. Do not use attacker-writable shared directories.

The completed partial is hashed against the caller digest. Publication creates a unique exclusive `.rtl-<random>.stage` in the destination parent, copies bounded bytes, flushes and rehashes that stage, closes it and moves it to the destination with overwrite disabled. The final name is not exposed during copying. A failed copy cannot leave a partial final file. A destination created before/during this operation is preserved. No caller-existing file is deleted. Only an actually owned stage is cleaned best-effort. Failure can leave an owned stage or newly created empty parent directory. No cross-filesystem final move is needed; no power-loss durability is promised.

Maximum staged artifact body is 64 MiB plus a one-byte read probe; buffers are at most 64 KiB. Disk usage may approach three artifact sizes plus bounded state. No automatic retries or unbounded background work. Fixture records retain only the newest 128 requests with bounded recorded fields and expose no filesystem paths.

## Errors and CLI

Stable machine codes for this version:

- Input/local: `invalid_request`, `limit_exceeded`, `numeric_overflow`, `non_loopback_or_invalid_url`, `workspace_path_unsafe`, `concurrent_transfer_not_allowed`, `duplicate_state_key`, `unsupported_state_version`, `invalid_state_format`, `ambiguous_local_state`, `destination_already_exists`, `local_io_error`.
- HTTP/content: `redirect_refused`, `validator_changed_or_full_response_refused`, `unexpected_status_code`, `missing_or_weak_etag`, `etag_mismatch_on_partial`, `unexpected_content_encoding`, `multipart_not_supported`, `invalid_content_range`, `content_length_mismatch`, `truncated_body`, `oversized_body`, `digest_mismatch`, `transfer_timeout_or_network_error`.

Transport parser failures, including header-limit/framing failures rejected by HttpClient before application parsing, can map to `transfer_timeout_or_network_error`; stream termination maps to `truncated_body`. Validation precedence is not a promised ordering when several independent faults coexist. Messages are fixed diagnostics, not raw exception/response/host-path dumps. IDs and validators are intentional report data, not anonymized data.

CLI commands: `demo [--text|--json|--both]`, `fixture`, `transfer` with the options listed by `--help`, and `inspect-state --workspace DIR [--json]`. Duplicate/unknown/missing options are syntax errors; maximum 32 arguments, each at most 4096 characters. Integer options reject overflow. `fixture` serves only the generated artifact until Enter/EOF. Transfer/inspection JSON uses camelCase; errors expose `code` and `message` (transfer also `httpStatusCode`). Demo reports omit random ports, paths and times. Syntax diagnostics use stderr, never echo supplied arguments. Broken output is handled as an operational failure where possible.

Exit 0: help, fixture shutdown, completed/interrupted transfer, consistent inspection, successful demo. Exit 1: CLI syntax. Exit 2: refusal, inconsistent inspection, operational/output failure. `--json` operational exceptions before a report can still produce only a fixed stderr diagnostic, not JSON.
