# Changelog

## 0.1.0 — Unreleased

Development version; no release or deployment claim.

### Added

- Bounded loopback-only HTTP downloader API and strict CLI, including synthetic fixture command.
- Persisted schema-1 checkpoints with strong ETag, artifact identity, expected digest and committed-prefix digest.
- Actual HTTP interruption/resume demo with deterministic text/JSON reports and changed-validator refusal.
- Adversarial console tests for protocol mismatches, malformed state, local tampering, concurrency, cancellation and publication failures.
- Explicit cleared-source SDK restore, no-restore test/demo builds, bilingual documentation and read-only CI.

### Corrected during development review

- Verified same-parent publication staging instead of copying directly to the final filename.
- Bounded state reads, staged checkpoint replacement and exclusive cooperating-process workspace ownership.
- Deep-ancestor symlink rejection and refusal to adopt pre-existing unowned directories.
- Full suffix-body validation before intermediate resume appends.
- Sanitized CLI diagnostics, strict duplicate options and controlled operational/output failure handling.
- Structured malformed-state refusal for invalid UTF-8 and unpaired JSON surrogates.
