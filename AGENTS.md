# AGENTS.md

Guidance for coding agents working in this repository. See [README.md](README.md) for the user-facing introduction and tutorials.

## What this project is

The Airbyte .NET CDK: a framework for building Airbyte source and destination connectors in C#. It implements the Airbyte protocol, a CLI entrypoint, and an HTTP stream abstraction with a fluent builder. Connectors are generated from templates and published as Docker images.

## Repository map

| Path | Purpose |
|------|---------|
| `Airbyte.Cdk/` | CDK library, CLI entrypoint, models, and connector generator (`OutputType=Exe`, targets `net10.0`) |
| `Airbyte.Cdk.Tests/` | xUnit test project for the CDK |
| `tests/Airbyte.Cdk.IntegrationTests/` | Console app that runs a connector against its live API and validates records against its schema |
| `tests/Airbyte.Cdk.IntegrationTests.Tests/` | xUnit tests for the integration test harness |
| `airbyte-integrations/connectors/` | Concrete connectors (e.g. `source-exchange-rates-free`) |
| `airbyte-integrations/connector-templates/` | Handlebars (`.hbs`) templates used by `dotnet run init` |
| `Dockerfile` | Builds/tests the CDK and publishes the NuGet package (multi-stage) |
| `.github/workflows/` | CI for the CDK and for connector image builds/releases |

There is a single solution, `Airbyte.Cdk.sln`, containing `Airbyte.Cdk` and `Airbyte.Cdk.Tests`. Connector directories have their own solution/project files and reference the published `Airbyte.Cdk` NuGet package, not a `ProjectReference`.

## Prerequisites and commands

- .NET SDK 10.0.x. `global.json` pins `10.0.100` with `rollForward: latestFeature`.
- Docker is required for the generator, connector image builds, and release workflows.

```bash
dotnet test                                   # run all CDK tests
dotnet build Airbyte.Cdk.sln                  # build the solution
dotnet run init --project Airbyte.Cdk         # scaffold a new connector interactively
docker build --target build .                 # build and test the CDK in the pinned SDK image
```

Run a connector locally from its project directory (the CLI verb is `read`, the operation is passed via `--command`):

```bash
dotnet run -- read --command spec
dotnet run -- read --command check --config sample_files/config.json
dotnet run -- read --command discover --config sample_files/config.json
dotnet run -- read --command read --config sample_files/config.json --catalog sample_files/configured_catalog.json --state sample_files/state.json
```

## Architecture

- `Airbyte.Cdk/AirbyteEntrypoint.cs` is the process entrypoint. `CommandLineParser` selects a verb (`read`, `write`, `init`, `publish`); `--command` selects `spec`, `check`, `discover`, or `read`. The implementation assembly is loaded from `AIRBYTE_IMPL_PATH` and the `Connector` subclass is resolved by `AIRBYTE_IMPL_MODULE`.
- `Connector` is the root abstraction. Sources derive from `Sources/AbstractSource.cs`; destinations derive from `Destinations/AbstractDestination.cs`.
- `AbstractSource.Streams(JsonElement config)` constructs the available streams. `AbstractSource.Read` matches the configured catalog to those streams and delegates to each `Stream.ReadRecords`.
- `Sources/Streams/Stream.cs` is transport-agnostic. `HttpStream` adds HTTP request/pagination/retry behavior; `FluentStreamBuilder` (`Sources/Utils/StreamBuilder.cs`) configures an `HttpStream` through callbacks and produces a `GenericStream`.
- `Models/` contains the Airbyte protocol DTOs and enums (`AirbyteMessage`, `AirbyteCatalog`, `SyncMode`, etc.) targeting protocol **0.19.0** over STDIO/JSON. Sources emit per-stream `STREAM` state and accept legacy stream maps, `STREAM`, `GLOBAL`, and `AirbyteMessage` envelopes. Socket/PROTOBUF transport is not implemented.
- `Sources/Utils/` contains schema loading, catalog helpers, casing extensions, and JSON helpers. `ResourceSchemaLoader` resolves `schemas/<stream-name>.json` from the output directory.

## Coding conventions

- Match the surrounding style: block-scoped namespaces, 4-space indentation, PascalCase for types/members, `_camelCase` for private fields.
- Stream names are converted to `snake_case`; schema files must match that name (e.g. stream `vatRates` → `schemas/vat_rates.json`).
- Tests use xUnit with Shouldly and Moq. Name tests `Test_<Behavior>` and place them under the matching namespace/folder in `Airbyte.Cdk.Tests`.
- The fluent builder is immutable: each method returns a new `FluentStreamBuilder`. Add new builder options as `AddProperty`/`AddFunc` entries and expose them on `GenericStream` with the matching `TryGetProperty`/`TryGetMethod` fallback pattern.
- Package versions are centralized in `Directory.Packages.props` (Central Package Management). Add a `PackageVersion` there instead of a `Version` attribute on `PackageReference`.
- Connector Docker images must be built from the repository root so that file is in the build context: `docker build -f airbyte-integrations/connectors/<connector>/Dockerfile -t <tag> .`.
- There is no `.editorconfig` or formatter configuration. Do not introduce new formatting/linting tooling as part of an unrelated change.
- Keep new XML doc comments consistent with the existing prose in `Stream.cs` and `StreamBuilder.cs`.

## Adding or changing a connector

1. Generate scaffolding with `dotnet run init --project Airbyte.Cdk`. Only the `source-dotnet-http-api` template is supported today.
2. Implement `CheckConnection` and `Streams` in the generated `Program`/connector class. Build HTTP streams from `UrlBase.HttpStream()` and configure parsing, paging, auth, cursor fields, and retries fluently.
3. Add `spec.json` and `schemas/<stream>.json`. Ensure the `.csproj` copies `spec.json` and `schemas/**` to the output directory (the generated project already does this).
4. Set the Docker runtime contract in the connector `Dockerfile`: `AIRBYTE_IMPL_PATH` must point at the built `.dll`, and `AIRBYTE_IMPL_MODULE` must match the `Connector` subclass name.
5. Declare live scenarios in `integration_tests.yaml` (streams, optional secrets, optional state). `dotnet run --project tests/Airbyte.Cdk.IntegrationTests -- run --connector <path>` builds the connector and validates every record against the stream schema.
6. Add the connector to the table in `README.md` and bump the version in the connector's `CHANGELOG.md` (SemVer). The release workflow reads the highest version from that file and refuses to overwrite an existing image tag.

## Release and CI

- CDK NuGet publishing is driven by `.github/workflows/release.yml` on a published GitHub release: the job packs `Airbyte.Cdk` with `-p:Version=<release tag>` and pushes it to nuget.org. Authentication is [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) — `NuGet/login@v1` exchanges the GitHub OIDC token for a short-lived key, so no NuGet API key is stored. The job therefore needs `id-token: write`, the `release` environment (secret `NUGET_USER` = nuget.org profile name), and a matching policy on nuget.org. The `Dockerfile` only builds/tests and never receives a credential.
- Connector images are built/published by `.github/workflows/build-connector.yml` and `release-connector.yml`, which invoke `dotnet run publish -b` from `Airbyte.Cdk`. That command derives changed connectors from `git diff HEAD HEAD~1` and the version from `CHANGELOG.md`.
- CI paths are narrow: CDK changes trigger `build.yml` (`Airbyte.Cdk/**`, `Airbyte.Cdk.Tests/**`); connector changes trigger `build-connector.yml` (`airbyte-integrations/connectors/**`) and `integration-tests.yml`, which runs only the connectors touched by the change.
- Connector integration tests live in `tests/Airbyte.Cdk.IntegrationTests`. It builds a connector and drives it through `check`/`discover`/`read`, so it never references the connector project and always exercises the `Airbyte.Cdk` version the connector restores from nuget.org.
- `.github/workflows/pr-agent.yml` runs PR-Agent against OpenRouter on every pull request — advisory review only, not a merge gate.

### AI pull request review

`pr-agent.yml` runs [PR-Agent](https://github.com/The-PR-Agent/pr-agent) against OpenRouter on every
pull request. It is **advisory only** — it posts comments and never blocks a merge, so it is not a
required status check.

It triggers on `pull_request_target`, not `pull_request`: fork pull requests get a read-only
`GITHUB_TOKEN` and no secrets, so a `pull_request` job can neither call the model nor comment.
PR-Agent reads the PR through the GitHub API and the workflow deliberately has no `actions/checkout`
step, so no contributor-controlled code or configuration runs with the key.

The OpenRouter key is the `OPENROUTER_API_KEY` repository secret (injected as `OPENROUTER__KEY`) and is
never committed. Model selection lives in `.pr_agent.toml`, which contains no secrets; endpoint and
key settings stay host-controlled there.

## Gotchas for agents

- The CDK project is an executable and generates a package on build (`GeneratePackageOnBuild=true`); building it also produces a `.nupkg`. Because of that, a bare `dotnet pack Airbyte.Cdk` fails with `NU5026` (missing `runtimeconfig.json`); pass `-p:GeneratePackageOnBuild=false` (what the release workflow does) or `dotnet build` first and then `dotnet pack --no-build`.
- `AirbyteEntrypoint` reads `AIRBYTE_IMPL_PATH` before anything else; local connector runs therefore need the built DLL or must run through the connector project's own `Main`.
- Docker images pin `mcr.microsoft.com/dotnet/sdk:10.0` and `runtime:10.0`.
- Some test files are empty placeholders (e.g. `TestHttp.cs`, `TestStreamsCore.cs`). Do not assume they contain coverage.
- Avoid changing `Airbyte.Cdk/Airbyte.Cdk.nuspec` paths or icon names without verifying the package output, since the package is published from a Docker build context.
- `Connector.Spec()` defaults a missing `protocol_version` to `0.19.0`; connector `spec.json` files should declare it explicitly.
