# CPM Conversion Report

## 1. Conversion overview

- **Scope:** `airbyte.cdk.dotnet` repository.
- **Projects converted:**
  - `Airbyte.Cdk/Airbyte.Cdk.csproj`
  - `Airbyte.Cdk.Tests/Airbyte.Cdk.Tests.csproj`
  - `airbyte-integrations/connectors/source-exchange-rates-free/source_exchange_rates.csproj`
- **CLI targets used for baseline and validation:**
  - `Airbyte.Cdk.sln` (covers the CDK and test projects)
  - `airbyte-integrations/connectors/source-exchange-rates-free/source_exchange_rates.csproj` (not part of the solution)
- **Central file:** `Directory.Packages.props` at the repository root governs all three projects.
- **Unique packages centralized:** 16.
- **Skipped projects/packages:** none. No `packages.config` files exist.
- **MSBuild version properties:** none were used, so none were inlined or removed.
- **Shared `.props`/`.targets` files inspected or changed:** none exist in this repository. The sibling `SecretsManager` layout was used only as a style reference.
- **Conditional references preserved:** none; all references are unconditional.

## 2. Version conflict resolutions

No conflicts existed. Packages shared across projects already used the same version:

| Package | Versions and projects | Decision | Impact |
|---------|-----------------------|----------|--------|
| `Flurl.Http` | `3.2.0` in CDK and sample connector | Kept `3.2.0` centrally | None |
| `Newtonsoft.Json` | `13.0.4` in CDK and sample connector | Kept `13.0.4` centrally | None |

## 3. Package comparison: baseline vs. result

**Changes**

No resolved package versions, additions, or removals changed. The conversion is version-neutral: 65 resolved package entries before and after, with zero differences.

**Unchanged (direct/top-level packages)**

| Project | Framework | Package | Version |
|---------|-----------|---------|---------|
| Airbyte.Cdk.csproj | net10.0 | CliWrap | 3.3.3 |
| Airbyte.Cdk.csproj | net10.0 | CommandLineParser | 2.8.0 |
| Airbyte.Cdk.csproj | net10.0 | Flurl.Http | 3.2.0 |
| Airbyte.Cdk.csproj | net10.0 | Handlebars.Net | 2.0.10 |
| Airbyte.Cdk.csproj | net10.0 | JsonCons.JsonPath | 1.1.0 |
| Airbyte.Cdk.csproj | net10.0 | JsonSchema.Net | 1.12.0 |
| Airbyte.Cdk.csproj | net10.0 | Newtonsoft.Json | 13.0.4 |
| Airbyte.Cdk.csproj | net10.0 | Polly | 7.2.2 |
| Airbyte.Cdk.csproj | net10.0 | Spectre.Console | 0.43.0 |
| Airbyte.Cdk.Tests.csproj | net10.0 | coverlet.collector | 3.1.0 |
| Airbyte.Cdk.Tests.csproj | net10.0 | FluentAssertions | 6.3.0 |
| Airbyte.Cdk.Tests.csproj | net10.0 | Microsoft.NET.Test.Sdk | 17.0.0 |
| Airbyte.Cdk.Tests.csproj | net10.0 | Moq | 4.16.1 |
| Airbyte.Cdk.Tests.csproj | net10.0 | xunit | 2.4.1 |
| Airbyte.Cdk.Tests.csproj | net10.0 | xunit.runner.visualstudio | 2.4.3 |
| source_exchange_rates.csproj | net10.0 | Airbyte.Cdk | 0.0.2-preview |
| source_exchange_rates.csproj | net10.0 | Flurl.Http | 3.2.0 |
| source_exchange_rates.csproj | net10.0 | Newtonsoft.Json | 13.0.4 |

Non-version metadata (`PrivateAssets`, `IncludeAssets`, `ExcludeAssets`) was preserved on `xunit.runner.visualstudio` and `coverlet.collector`.

## 4. Risk assessment

**Low risk.** The conversion is version-neutral, all three projects restore and build with 0 warnings and 0 errors, and the test suite passes.

- `dotnet test Airbyte.Cdk.sln --no-build --no-restore`: 22 passed, 11 skipped (pre-existing skips), 0 failed.
- No `VersionOverride` entries were introduced.
- No MSBuild version properties were removed.
- No conditional-version behavior changed.

## 5. Follow-up items

1. Verify that `dotnet run init --project Airbyte.Cdk` still scaffolds connectors correctly under CPM. `InitCli` calls `dotnet add package`, which now updates `Directory.Packages.props`; this was not exercised because it generates a new connector and builds a Docker image.
2. The sample connector still references the published `Airbyte.Cdk` `0.0.2-preview` package. Update the central version when a newer CDK package is published.

## 6. Artifacts and usage

- `cpm-artifacts/baseline-solution.binlog` and `cpm-artifacts/after-cpm-solution.binlog`
- `cpm-artifacts/baseline-connector.binlog` and `cpm-artifacts/after-cpm-connector.binlog`
- `cpm-artifacts/baseline-packages-solution.json` and `cpm-artifacts/after-cpm-packages-solution.json`
- `cpm-artifacts/baseline-packages-connector.json` and `cpm-artifacts/after-cpm-packages-connector.json`
- `cpm-artifacts/convert-to-cpm.md` (this report)

The binlogs support manual MSBuild comparison; the JSON snapshots support machine-readable resolved-package comparison.

**Action before merge:** none required for correctness. Decide whether to commit or exclude the `cpm-artifacts/` evidence directory.
