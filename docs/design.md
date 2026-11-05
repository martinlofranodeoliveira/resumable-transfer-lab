# Design and independent review boundaries

## Components

- `ResumableTransferClient`: request validation, process gate, headers-first HTTP, transfer/checkpoint orchestration, structured results.
- `TransferGuards`: bounded identity, digest, URL, validator and ancestor-path policy.
- `WorkspaceLease` / `TransferStorage`: cooperating process exclusion, bounded state reads, prefix verification, staged checkpoint replacement, bounded copy and verified exclusive publication.
- `StateSerializer`: explicit schema with duplicate decoded-key detection instead of permissive object deserialization.
- `LoopbackArtifactServer`: ephemeral ASP.NET Core synthetic fixture, bounded request records and explicit adversarial response modes; no general file server.
- CLI: small strict parser and sanitized diagnostics. Demo reports contain deterministic synthetic facts, not ports or host paths.
- Console tests: actual HTTP plus independent byte/digest oracles and internal storage failure injection; no test-framework package or mocked HTTP transport is required.

## State transitions

1. Validate bounded inputs and visible path ancestors; acquire the process gate and workspace's exclusive OS-held marker handle.
2. Load only a recognized workspace layout. Read state with a cap-plus-probe, parse strictly, verify partial length and SHA-256, then compare caller identity.
3. Receive headers with `ResponseHeadersRead`. Reject status/validator/range/encoding/multipart mismatches before creating a body stage.
4. Stage bounded bytes. Initial orderly stop closes the response at a requested prefix; resume always validates the entire suffix before append.
5. Move the initial prefix or append a validated suffix prefix, flush, calculate the prefix digest, write/flush/close `transfer-state.next`, replace checkpoint, remove owned body stage.
6. For completion, check expected digest, copy to an exclusive same-parent publication stage, flush and rehash the stage, then move without overwrite. Keep completed workspace evidence.

A network refusal before step 5 leaves previous checkpoint/partial bytes unchanged. A failure during step 5 may be ambiguous: old state, changed partial and residual stages are not silently reconciled. Inspection is diagnostic, not repair. A process crash can release the lock while leaving transition evidence; the next invocation refuses that evidence. There is no claim of directory fsync, arbitrary crash recovery or cross-platform transactional filesystem semantics.

## Why stage twice?

A resume body stage prevents malformed/truncated/oversized suffixes from contaminating committed partials. Publication staging prevents a failed destination write from exposing corrupt bytes at the final name. The publication stage is rehashed, not merely trusted because a prior partial hash matched. Both choices cost bounded local storage and additional I/O; no benchmark claim is made.

A test-only internal copier delegate injects a mid-write IOException after actual stage bytes have been written. Tests assert the final name remains absent, the source remains identical and only owned stages are removed. Another test creates caller data at the final name during copy and verifies it survives the exclusive move. The seam is internal, not a public runtime fault option.

## Threat model

Adversarial HTTP responses, accidental/malicious state JSON, partial-byte tampering, visible symlinks and cooperating concurrent processes are in scope. There is no trust in ETag alone: the caller's expected digest controls publication. A party controlling both the caller digest and bytes can choose arbitrary content. Loopback is not sender authentication.

The filesystem must be trusted and regular-file based. Ancestor checks are deterministic misuse prevention, not openat-style confinement. Hard links, devices/FIFOs, adversarial rename races, mounts and noncooperating writers are outside the security boundary. State hashes are not keyed authenticators. Avoid sensitive artifact IDs even though the bundled fixture is synthetic.

## Resource and lifecycle decisions

No dependencies beyond BCL/shared ASP.NET Core, no runtime models/APIs, no redirects/proxies/DNS names/decompression, no retries, one transfer per process. HTTP body staging is bounded to the declared artifact/suffix plus one probe; hashing/copy buffers are capped. State input uses a bounded read even if a file grows after opening. Cooperative cancellation covers HTTP and bounded local loops; ordinary local filesystem calls may still block.

Fixture payloads are copied on update, capped at 64 MiB, and never loaded from arbitrary files. Request history is a 128-entry ring. Servers use ephemeral IPv4 loopback and are disposed by tests/demo. The client also accepts literal IPv6 loopback. Test symlink cases run on non-Windows systems; Linux is the CI target. No claim is made for every filesystem/platform combination.

Build preparation is an explicit restore with cleared sources and the installed pinned SDK. Test/demo builds use `--no-restore`, bounded MSBuild/compiler parallelism and ignored local SDK/output directories. CI has read-only repository permissions and no publication/deployment steps.

## References and scope

- [RFC 9110 If-Range](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.5): unchanged representations may produce ranges; changed ones may produce a full response. This client refuses full responses on resume.
- [RFC 9110 Range](https://www.rfc-editor.org/rfc/rfc9110.html#section-14): only the single open-ended byte request and exact suffix response subset is supported here.
- [.NET ContentRangeHeaderValue](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.headers.contentrangeheadervalue?view=net-10.0): typed parsing complements explicit numeric/exact-bound checks.
- [.NET SHA256.HashData](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.sha256.hashdata?view=net-10.0): fixture/test one-shot hashes; the client uses BCL incremental SHA-256 for cooperative bounded local loops.
- [Redirect control](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.allowautoredirect?view=net-10.0): automatic redirects are disabled; application code rejects 3xx.

These sources explain primitives and semantics, not certification or broad standards compliance.
