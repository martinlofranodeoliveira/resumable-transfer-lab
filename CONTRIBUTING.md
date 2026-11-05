# Contributing

Resumable Transfer Lab is an original Lofrano Tecnologia development project, version 0.1.0, under MIT. Keep contributions original and compatible with that license. Do not submit private client code, credentials, real personal data or confidential artifacts.

## Local checks

Install the exact SDK in `global.json` with its shared ASP.NET Core framework, then run from the project directory:

```sh
bash scripts/prepare.sh
bash scripts/test.sh
bash scripts/demo.sh --json
```

Preparation is explicit and uses cleared package sources. Tests and demo do not restore. Do not add runtime/test NuGet packages merely to obtain a test framework. The console harness must return nonzero on failure, clean up owned temporary roots and dispose servers/streams. Keep compiler parallelism bounded. Generated `build/` files are not source.

Changes should explain the observable contract, failure behavior and resource bound, with regression tests whose expected bytes/headers/errors are reasoned independently of implementation. Preserve existing features rather than weakening assertions. Cover malformed and boundary inputs as well as happy paths. A storage change should include failure injection or another meaningful proof of preservation, not only an already-existing-target check.

Keep README.md, README.pt-BR.md, docs/contract.md, docs/design.md and the unreleased changelog consistent. Do not claim broad HTTP compliance, crash recovery, authenticity or production readiness. Do not introduce telemetry, general remote fetching, release/publishing/deployment workflows or copied private code.

Use repository issues/pull requests for non-sensitive discussion. Use the private reporting route in SECURITY.md for vulnerabilities. General organizational contact is through the [official homepage](https://lofranotecnologia.com.br).
