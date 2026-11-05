# Security policy

## Reporting

If enabled for this repository, use [GitHub private vulnerability reporting](https://github.com/martinlofranodeoliveira/resumable-transfer-lab/security/advisories/new). Otherwise, request a private reporting channel through the [Lofrano Tecnologia homepage](https://lofranotecnologia.com.br). Do not put sensitive exploit details, credentials, private workspaces or personal data in public issues. No response-time SLA is promised.

## Scope

Version 0.1.0 is a development version. It is a bounded local synthetic HTTP lab, not a production transport, sender-authentication mechanism or malware scanner. The expected SHA-256 establishes expected-byte integrity only. Strong ETags are validators, not cryptographic identity proofs. Loopback services can be controlled by other local actors.

Use a trusted regular-file filesystem and prevent hostile path replacement. Visible symlinks/reparse points and parent traversal are refused, but ordinary BCL path checks are not a sandbox. Hard-link aliases, device/FIFO paths, hostile mounts, noncooperating file mutation and adversarial rename races are outside the security boundary. The lock excludes cooperating .NET processes from a workspace; it is not an access-control mechanism.

State may retain supplied URLs, IDs, validators and hashes. Reports intentionally show selected IDs/validators. Escaping does not anonymize. The fixture contains only generated synthetic bytes. Do not use confidential URLs/identifiers or real operational data.

Orderly checkpoints are supported; arbitrary process crashes and power loss can leave evidence that fails closed. Do not delete stages or edit offsets to force a resume. Preserve ambiguous workspaces for local inspection without publishing their content publicly. The utility never intentionally overwrites an existing final destination and never sends uploads, credentials or telemetry.
