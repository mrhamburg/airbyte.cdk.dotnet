# Connector Integration Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-connector integration test harness that runs a source connector against its live API and fails when the returned records no longer match the stream's JSON schema.

**Architecture:** A console app (`tests/Airbyte.Cdk.IntegrationTests`) acts as a black-box Airbyte client: it builds the connector project, spawns the built DLL with `read --command check|discover|read`, parses the JSONL protocol from stdout, and validates every emitted RECORD against the schema advertised by `discover`. Each connector declares its scenarios in `integration_tests.yaml`. A path-filtered GitHub Actions workflow runs only the connectors touched by a PR or a push to `main`.

**Tech Stack:** .NET 10 console app, System.Text.Json, JsonSchema.Net 9.5.0, YamlDotNet 18.1.0, xUnit + Shouldly for unit tests, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-30-connector-integration-tests-design.md` (design captured in the conversation of this branch; the plan restates every decision that matters).

## Global Constraints

- Target framework `net10.0`; SDK pinned by `global.json` (`10.0.100`, `rollForward: latestFeature`).
- Central Package Management: every package gets a `<PackageVersion>` in the root `Directory.Packages.props`; never a `Version` attribute on a `PackageReference`.
- The runner must not reference `Airbyte.Cdk` or any connector project. It is an independent Airbyte-protocol client.
- Integration test connectors resolve `Airbyte.Cdk` from nuget.org (currently `0.0.8`), not from the local source tree.
- Connectors are built and run out-of-tree in a temp artifacts directory; the harness never writes into the connector directory.
- Secrets are resolved through `ISecretSource`. `SecretProviderBuilder`/`SecretsManager.PostgreSql` is NOT a dependency of this PR — the NuGet package is not published. v1 ships `EnvSecretSource`; the Postgres adapter is a later, isolated change.
- Missing `integration_tests.yaml` for a changed connector is a failure, not a skip.
- No connector may opt into "run zero assertions".

## Review Focus

- **A connector whose API returns fewer records than before.** A green run with `min_records: 1` must not mask a stream that now returns nothing; empty output must fail.
- **An API that adds a top-level field.** Strict mode must fail on an undeclared envelope property but must not fail on new keys inside typed maps (`rates`, `symbols`), which are routine data.
- **A connector that exits 0 while logging an error trace.** The run must still fail.
- **Missing credentials in CI.** A connector needing secrets must be reported as SKIPPED, never as passed, and must fail on `main` where secrets are expected.
- **A hung or rate-limited endpoint.** A connector run must time out and be reported as a failure rather than blocking the job.

---

## File Structure

| Path | Responsibility |
|------|----------------|
| `tests/Airbyte.Cdk.IntegrationTests/Airbyte.Cdk.IntegrationTests.csproj` | Console app, `OutputType=Exe` |
| `tests/Airbyte.Cdk.IntegrationTests/Program.cs` | CLI verbs (`run`, `list-changed`), exit codes, report printing |
| `tests/Airbyte.Cdk.IntegrationTests/ConnectorSpec.cs` | YAML model + loader + validation |
| `tests/Airbyte.Cdk.IntegrationTests/DockerfileMetadata.cs` | `AIRBYTE_IMPL_MODULE` fallback |
| `tests/Airbyte.Cdk.IntegrationTests/ConnectorLocator.cs` | Connector dir → project file, artifacts dir |
| `tests/Airbyte.Cdk.IntegrationTests/ChangedConnectors.cs` | `git diff --name-only` output → connector dirs |
| `tests/Airbyte.Cdk.IntegrationTests/ISecretSource.cs` | Secret seam + env implementation |
| `tests/Airbyte.Cdk.IntegrationTests/ConnectorProcess.cs` | Build + spawn + JSONL parsing + timeout |
| `tests/Airbyte.Cdk.IntegrationTests/CatalogBuilder.cs` | discover output → configured catalog |
| `tests/Airbyte.Cdk.IntegrationTests/StrictSchema.cs` | Draft-7 schema + strict `additionalProperties` injection |
| `tests/Airbyte.Cdk.IntegrationTests/RecordValidator.cs` | Record → violations with JSON pointers |
| `tests/Airbyte.Cdk.IntegrationTests/Runner.cs` | Per-test orchestration, result model, exit-code mapping |
| `tests/Airbyte.Cdk.IntegrationTests.Tests/*` | xUnit unit tests for the pure parts |
| `airbyte-integrations/connectors/source-exchange-rates-free/integration_tests.yaml` | First connector scenario |
| `.github/workflows/integration-tests.yml` | Path-filtered matrix workflow |
| `README.md`, `AGENTS.md` | Documentation |
| `airbyte-integrations/connector-templates/source-dotnet-http-api/integration_tests.yaml.hbs` | Template stub |
| `Airbyte.Cdk/InitCli.cs` | Emit the stub for generated connectors |

---

## Task 1: Project scaffold, YAML model, changed-connector mapping

**Files:** create both projects, `ConnectorSpec.cs`, `DockerfileMetadata.cs`, `ConnectorLocator.cs`, `ChangedConnectors.cs`, tests.

**Interfaces:**
- Produces: `record ConnectorSpec(string Directory, string Module, string ProjectFile, IReadOnlyDictionary<string,string> Secrets, IReadOnlyList<TestCaseSpec> Tests)`; `record TestCaseSpec(string Name, JsonElement Config, int? Limit, int? PageSize, int TimeoutSeconds, IReadOnlyList<StreamSpec> Streams)`; `record StreamSpec(string Name, string SyncMode, JsonElement? State, int MinRecords, string[]? CursorField)`; `ConnectorSpecLoader.Load(string connectorDir)`; `ChangedConnectors.FromChangedFiles(IEnumerable<string> paths)` → `IReadOnlyList<string>` of absolute connector dirs.

- [ ] Failing tests: parses a minimal YAML; derives module from Dockerfile when omitted; rejects unknown `sync_mode`; `FromChangedFiles` de-duplicates, ignores non-connector paths and deleted files.
- [ ] Implement; `dotnet test tests/Airbyte.Cdk.IntegrationTests.Tests` → PASS.
- [ ] Commit.

## Task 2: Secret seam

**Files:** `ISecretSource.cs`, tests.

**Interfaces:** `interface ISecretSource { Task<string?> GetAsync(string key, CancellationToken ct); }`; `sealed class EnvSecretSource : ISecretSource`; `static ISecretSource SecretSourceFactory.Create()` → `EnvSecretSource`.

- [ ] Failing tests: env hit returns value; miss returns null; factory returns env source.
- [ ] Implement; PASS; commit.

## Task 3: Connector process

**Files:** `ConnectorProcess.cs`, tests for the message parser.

**Interfaces:** `record ProcessOutcome(int ExitCode, IReadOnlyList<JsonElement> Messages, string StdErr, bool TimedOut)`; `Task<ProcessOutcome> ConnectorProcess.BuildAsync(CancellationToken)`; `Task<ProcessOutcome> ConnectorProcess.RunAsync(string command, string? config, string? catalog, string? state, TimeSpan timeout, CancellationToken)`.

- [ ] Tests: JSONL parser accepts RECORD/LOG/TRACE, ignores blank lines and non-JSON lines, preserves order.
- [ ] Implement with `AIRBYTE_IMPL_PATH`/`AIRBYTE_IMPL_MODULE` set, `dotnet <dll> read --command <cmd>`, kill-on-timeout.
- [ ] Commit.

## Task 4: Strict schema validation

**Files:** `StrictSchema.cs`, `RecordValidator.cs`, tests.

**Interfaces:** `JsonSchema StrictSchema.Build(JsonElement discoveredSchema)`; `IReadOnlyList<string> RecordValidator.Validate(JsonSchema schema, JsonElement record)`.

- [ ] Tests: extra envelope field fails; extra key inside `additionalProperties` map passes; nested `items` enforced; wrong type fails; `patternProperties` untouched; `anyOf`/`oneOf` alternatives and `allOf` + `$ref` compositions still validate; unsupported dialects rejected with a readable message; valid record passes.

**Ruling (implementation):** strict injection is skipped for `$ref` targets, `allOf`-composed objects and
`allOf`/`anyOf`/`oneOf` branches, and JsonSchema.Net 9.x dropped draft-04, so dialect 4 is rejected rather
than normalized. Cost if wrong: an undeclared field inside a composed schema is not detected as drift.
- [ ] Implement; PASS; commit.

## Task 5: Catalog building and orchestration

**Files:** `CatalogBuilder.cs`, `Runner.cs`, `Program.cs`, tests.

**Interfaces:** `string CatalogBuilder.Build(JsonElement discoverMessage, IReadOnlyList<StreamSpec> streams)`; `Task<ConnectorResult> Runner.RunAsync(ConnectorSpec spec, RunOptions options, CancellationToken)`; `static int ExitCode(ConnectorResult result, bool noSkip)`.

- [ ] Tests: catalog contains only requested streams with requested sync modes; incremental stream gets `destination_sync_mode=append`; exit-code mapping (pass → 0, fail → 1, skip with `--no-skip` → 1, skip without → 0).
- [ ] Implement `check` → `discover` → `read` sequence, per-stream assertions, temp config/state/catalog.
- [ ] Commit.

## Task 6: First connector + real run

**Files:** `airbyte-integrations/connectors/source-exchange-rates-free/integration_tests.yaml`.

- [ ] Scenarios: full refresh (`symbols`, `latest`, `limit: 50`), incremental (`historical`, state `{historical: {USD: "2026-10-20"}}`, `limit: 5`).
- [ ] `dotnet run --project tests/Airbyte.Cdk.IntegrationTests -- run --connector airbyte-integrations/connectors/source-exchange-rates-free` → PASS against the live Frankfurter API.
- [ ] Commit.

**Ruling (implementation):** the CDK's `_limit`/`_page_size` config keys are rejected by any connector whose spec
sets `additionalProperties: false` (`GetConfig` validates the config against the spec). Verified against the live
connector: `Config does not match spec schema`. So `limit` is enforced by the harness — the process is stopped
after N RECORD messages and the resulting non-zero exit code is not treated as a failure — and `page_size` was
dropped from the YAML entirely. Cost if wrong: a scenario is shorter than the connector's natural run, so a
failure that only appears after the cap is not observed.

## Task 7: CI workflow

**Files:** `.github/workflows/integration-tests.yml`.

- [ ] `pull_request` + `push: main` path-filtered to `airbyte-integrations/connectors/**`, plus `workflow_dispatch`.
- [ ] `changed` job: `fetch-depth: 0`, diff, `list-changed`, expose `has`/`connectors` outputs.
- [ ] `integration` job: matrix over connectors, `fail-fast: false`, `timeout-minutes: 30`, `--no-skip` on push to main, secret env passed through.
- [ ] Commit.

## Task 8: Docs and template

**Files:** `README.md`, `AGENTS.md`, `connector-templates/.../integration_tests.yaml.hbs`, `Airbyte.Cdk/InitCli.cs`.

- [ ] Document the YAML, the CLI, and the secret seam; add the connector checklist entry.
- [ ] `dotnet run init` produces the stub (verified by build).
- [ ] Commit, open PR.