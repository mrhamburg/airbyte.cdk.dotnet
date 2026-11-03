# Local Parquet Destination Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a local Parquet destination connector and a permanent black-box Protobuf end-to-end test that runs the Exchange Rates source into it.

**Architecture:** The connector builds a `ParquetSchema` from each stream's JSON schema, converts records to dictionaries, and writes one Parquet file per stream per flush with `ParquetSerializer.SerializeUntypedAsync`. The integration harness gains a destination mode that starts the source and destination as two processes connected by Unix domain sockets.

**Tech Stack:** .NET 10, C#, Airbyte.Cdk, Parquet.Net 6.1.0, xUnit, Shouldly, Unix domain sockets, Protobuf.

**Spec:** `docs/superpowers/specs/2026-11-03-local-parquet-destination-design.md`

## Global Constraints

- Target framework: `net10.0`.
- Parquet library: `Parquet.Net` `6.1.0`, added to `Directory.Packages.props`.
- Airbyte protocol version: `0.19.0`.
- Spec flags: `supportsIncremental: true`, `supported_destination_sync_modes: ["append", "overwrite"]`, `supportsNormalization: false`, `supportsDBT: false`.
- Config: one required string property, `output_path`.
- Buffer limit: `50_000` rows, constructor parameter `flushRowLimit` with that default.
- File path: `<output_path>/<namespace?>/<stream>/part-<yyyyMMddTHHmmssfff>-<0000>.parquet`.
- Temporary file suffix: `.parquet.tmp`.
- Destination connector uses a `ProjectReference` to `Airbyte.Cdk`.
- The Exchange Rates source keeps its default package reference and gets a conditional `ProjectReference` behind `UseLocalCdk`.
- The harness never references a connector assembly.
- Test names use the `Test_<Behavior>` pattern.
- Do not add code comments unless a step says so.

## Review Focus

1. A record misses a required property: the flush fails with a message that names the stream and the field, and no `.parquet` file appears.
2. A root map stream (`symbols`): the file has one map column named `value`, and every key and value is present.
3. A stream or namespace name with `/`, `..`, or an invalid character: the file stays inside `output_path`.
4. A value contradicts the declared type (a number for a boolean field): the flush fails instead of writing a different type.
5. A mid-stream `STATE`: every buffer is flushed before the state is echoed, and an `overwrite` run deletes the old files exactly once.

---

### Task 1: Connector project, options, and check

**Files:**
- Modify: `Directory.Packages.props`
- Create: `airbyte-integrations/connectors/destination-local-parquet/destination_local_parquet.csproj`
- Create: `airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/Destination.LocalParquet.Tests.csproj`
- Create: `airbyte-integrations/connectors/destination-local-parquet/DestinationOptions.cs`
- Create: `airbyte-integrations/connectors/destination-local-parquet/LocalParquet.cs`
- Create: `airbyte-integrations/connectors/destination-local-parquet/spec.json`
- Create: `airbyte-integrations/connectors/destination-local-parquet/NuGet.Config`
- Create: `airbyte-integrations/connectors/destination-local-parquet/CHANGELOG.md`
- Create: `airbyte-integrations/connectors/destination-local-parquet/README.md`
- Create: `airbyte-integrations/connectors/destination-local-parquet/Dockerfile`
- Test: `airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/DestinationOptionsTests.cs`

**Interfaces:**
- Consumes: `Airbyte.Cdk.Destinations.AbstractDestination`, `Airbyte.Cdk.Models.AirbyteConnectionStatus`, `Airbyte.Cdk.Models.Status`.
- Produces:
  - `internal sealed class DestinationOptions` with `string OutputPath { get; }`, `static DestinationOptions FromJson(JsonElement config)`, `void EnsureReady()`, `string StreamDirectory(string? ns, string stream)`.
  - `public sealed class LocalParquet : AbstractDestination` with `static Task Main(string[] args)` and `override AirbyteConnectionStatus Check(AirbyteLogger logger, JsonElement config)`. `Write` stays a stub until Task 5.

- [ ] **Step 1: Add the package version**

Add to `Directory.Packages.props`, in the source package group:

```xml
<PackageVersion Include="Parquet.Net" Version="6.1.0" />
```

- [ ] **Step 2: Create the connector and test project files**

`destination_local_parquet.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\Airbyte.Cdk\Airbyte.Cdk.csproj" />
    <PackageReference Include="Parquet.Net" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Destination.LocalParquet.Tests" />
    <Compile Remove="tests\**" />
    <None Remove="tests\**" />
  </ItemGroup>

  <ItemGroup>
    <None Update="spec.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

`tests/Destination.LocalParquet.Tests/Destination.LocalParquet.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\destination_local_parquet.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Write the failing options and check tests**

```csharp
using System;
using System.IO;
using System.Text.Json;
using destination_local_parquet;
using Shouldly;
using Xunit;

namespace Destination.LocalParquet.Tests
{
    public class DestinationOptionsTests
    {
        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        [Fact]
        public void Test_FromJson_ReadsOutputPath()
        {
            var options = DestinationOptions.FromJson(Json("""{"output_path":"/tmp/parquet-out"}"""));

            options.OutputPath.ShouldBe(Path.GetFullPath("/tmp/parquet-out"));
        }

        [Fact]
        public void Test_FromJson_MissingOutputPath_Throws()
        {
            var error = Should.Throw<ArgumentException>(() => DestinationOptions.FromJson(Json("{}")));

            error.Message.ShouldContain("output_path");
        }

        [Fact]
        public void Test_EnsureReady_CreatesDirectoryAndRemovesProbe()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-check-" + Guid.NewGuid().ToString("N"));
            var options = DestinationOptions.FromJson(Json($$"""{"output_path":"{{root}}"}"""));

            options.EnsureReady();

            Directory.Exists(root).ShouldBeTrue();
            Directory.GetFiles(root).ShouldBeEmpty();
        }

        [Fact]
        public void Test_Check_ReturnsSucceeded()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-check-" + Guid.NewGuid().ToString("N"));
            var connector = new LocalParquet();

            var status = connector.Check(new Airbyte.Cdk.AirbyteLogger(m => { }),
                Json($$"""{"output_path":"{{root}}"}"""));

            status.Status.ShouldBe(Airbyte.Cdk.Models.Status.SUCCEEDED);
        }

        [Fact]
        public void Test_Check_FailsWhenOutputPathIsAFile()
        {
            var file = Path.Combine(Path.GetTempPath(), "parquet-check-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(file, "x");
            var connector = new LocalParquet();

            var status = connector.Check(new Airbyte.Cdk.AirbyteLogger(m => { }),
                Json($$"""{"output_path":"{{file}}"}"""));

            status.Status.ShouldBe(Airbyte.Cdk.Models.Status.FAILED);
            status.Message.ShouldNotBeNullOrWhiteSpace();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run:

```bash
dotnet test airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/Destination.LocalParquet.Tests.csproj --filter DestinationOptionsTests
```

Expected: build fails because `DestinationOptions` and `LocalParquet` do not exist.

- [ ] **Step 5: Implement `DestinationOptions`**

```csharp
using System;
using System.IO;
using System.Text.Json;

namespace destination_local_parquet
{
    internal sealed class DestinationOptions
    {
        private const string ProbeFileName = ".airbyte-write-test";

        private DestinationOptions(string outputPath) => OutputPath = outputPath;

        public string OutputPath { get; }

        public static DestinationOptions FromJson(JsonElement config)
        {
            if (config.ValueKind != JsonValueKind.Object
                || !config.TryGetProperty("output_path", out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
                throw new ArgumentException("Config property 'output_path' is required.");

            return new DestinationOptions(Path.GetFullPath(value.GetString()!));
        }

        public void EnsureReady()
        {
            if (File.Exists(OutputPath))
                throw new IOException($"output_path '{OutputPath}' is a file, not a directory.");

            Directory.CreateDirectory(OutputPath);
            var probe = Path.Combine(OutputPath, ProbeFileName);
            File.WriteAllText(probe, "airbyte");
            File.Delete(probe);
        }

        public string StreamDirectory(string? ns, string stream)
        {
            var safeStream = Sanitize(stream);
            return string.IsNullOrWhiteSpace(ns)
                ? Path.Combine(OutputPath, safeStream)
                : Path.Combine(OutputPath, Sanitize(ns!), safeStream);
        }

        private static string Sanitize(string value)
        {
            var characters = value.Replace('/', '_').Replace('\\', '_').ToArray();
            foreach (var invalid in Path.GetInvalidFileNameChars())
                for (var index = 0; index < characters.Length; index++)
                    if (characters[index] == invalid)
                        characters[index] = '_';

            var result = new string(characters).Trim();
            return string.IsNullOrWhiteSpace(result) || result == ".." || result == "." ? "stream" : result;
        }
    }
}
```

- [ ] **Step 6: Implement `LocalParquet` with `Check` and a stub `Write`**

```csharp
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk;
using Airbyte.Cdk.Destinations;
using Airbyte.Cdk.Models;

namespace destination_local_parquet
{
    public class LocalParquet : AbstractDestination
    {
        public static async Task Main(string[] args) => await AirbyteEntrypoint.Main(args);

        public override AirbyteConnectionStatus Check(AirbyteLogger logger, JsonElement config)
        {
            try
            {
                DestinationOptions.FromJson(config).EnsureReady();
                return new AirbyteConnectionStatus { Status = Status.SUCCEEDED };
            }
            catch (Exception e)
            {
                return new AirbyteConnectionStatus { Status = Status.FAILED, Message = e.Message };
            }
        }

        public override Task Write(AirbyteLogger logger, JsonElement config, ConfiguredAirbyteCatalog catalog,
            AirbyteMessage msg) => throw new NotImplementedException();
    }
}
```

- [ ] **Step 7: Create `spec.json`**

```json
{
  "documentationUrl": "https://github.com/mrhamburg/airbyte.cdk.dotnet",
  "protocol_version": "0.19.0",
  "supportsIncremental": true,
  "supported_destination_sync_modes": ["append", "overwrite"],
  "supportsNormalization": false,
  "supportsDBT": false,
  "connectionSpecification": {
    "$schema": "http://json-schema.org/draft-07/schema#",
    "title": "Local Parquet Destination Spec",
    "type": "object",
    "required": ["output_path"],
    "additionalProperties": false,
    "properties": {
      "output_path": {
        "type": "string",
        "title": "Output path",
        "description": "Directory that receives the Parquet files. It is created when missing."
      }
    }
  }
}
```

- [ ] **Step 8: Create `NuGet.Config`, `CHANGELOG.md`, `README.md`, and `Dockerfile`**

`NuGet.Config`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
```

`CHANGELOG.md`:

```markdown
# Changelog

## 0.0.1
Initial release. Writes Airbyte records to typed Parquet files under `output_path`.

Follow-up: switch the `ProjectReference` to `Airbyte.Cdk` back to the NuGet package after the next CDK
release that contains the Protobuf socket transport.
```

`README.md`:

```markdown
# Local Parquet Destination

Writes Airbyte records to typed Parquet files on the local filesystem. One file is written per stream
and per flush, under `output_path`.

## Config

| Property | Required | Description |
|---|---|---|
| `output_path` | yes | Directory that receives the Parquet files. |

## Layout

`<output_path>/<namespace?>/<stream>/part-<timestamp>-<sequence>.parquet`

`overwrite` deletes existing `.parquet` files in the stream directory before the first record.
`append` keeps them and adds new files.

## Development

```bash
dotnet build
dotnet test tests/Destination.LocalParquet.Tests/Destination.LocalParquet.Tests.csproj
```

Run the black-box integration test from the repository root:

```bash
dotnet run --project tests/Airbyte.Cdk.IntegrationTests -- \
  run --connector airbyte-integrations/connectors/destination-local-parquet
```
```

`Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG BUILD_VERSION="0.0.1"
ENV WORKDIR=/airbyte/build
WORKDIR $WORKDIR

COPY . ./

WORKDIR $WORKDIR/airbyte-integrations/connectors/destination-local-parquet

RUN dotnet test tests/Destination.LocalParquet.Tests/Destination.LocalParquet.Tests.csproj
RUN dotnet build -c Release -o output

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

ENV WORKDIR=/airbyte/integration_code
WORKDIR $WORKDIR

COPY --from=build /airbyte/build/airbyte-integrations/connectors/destination-local-parquet/output .

ENV AIRBYTE_IMPL_PATH="/airbyte/integration_code/destination_local_parquet.dll"
ENV AIRBYTE_IMPL_MODULE="LocalParquet"
ENV AIRBYTE_ENTRYPOINT "dotnet /airbyte/integration_code/destination_local_parquet.dll write --command"
ENTRYPOINT ["dotnet", "/airbyte/integration_code/destination_local_parquet.dll", "write", "--command"]

LABEL io.airbyte.version=$BUILD_VERSION
LABEL io.airbyte.name=airbytedotnet/destination-local-parquet
```

- [ ] **Step 9: Run the tests to verify they pass**

Run the command from Step 4. Expected: all `DestinationOptionsTests` pass.

- [ ] **Step 10: Commit**

```bash
git add Directory.Packages.props airbyte-integrations/connectors/destination-local-parquet
git commit -m "feat(destination-local-parquet): add connector scaffolding and output check"
```

---

### Task 2: JSON schema to Parquet schema mapper

**Files:**
- Create: `airbyte-integrations/connectors/destination-local-parquet/Parquet/FieldMapping.cs`
- Create: `airbyte-integrations/connectors/destination-local-parquet/Parquet/StreamSchema.cs`
- Create: `airbyte-integrations/connectors/destination-local-parquet/Parquet/ParquetSchemaMapper.cs`
- Test: `airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/SchemaMapperTests.cs`

**Interfaces:**
- Consumes: `Parquet.Schema.ParquetSchema`, `DataField`, `StructField`, `MapField`, `ListField`, `Field`.
- Produces:
  - `internal enum FieldShape { Scalar, Struct, Map, List }`
  - `internal enum RootShape { Struct, Map, Value }`
  - `internal sealed class FieldMapping` with `Name`, `Field`, `Shape`, `IsRequired`, `Properties`, `Value`.
  - `internal sealed class StreamSchema` with `ParquetSchema Parquet`, `RootShape Shape`, `Properties`, `Value`, and `static StreamSchema Fallback()`.
  - `internal static class ParquetSchemaMapper` with `StreamSchema FromJsonSchema(JsonElement schema)` and `StreamSchema FromRecord(JsonElement record)`.

- [ ] **Step 1: Write the failing mapper tests**

Use `Shouldly` and `Xunit`. The helper reads JSON:

```csharp
private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

private static FieldMapping Property(StreamSchema schema, string name) =>
    schema.Properties.Single(x => x.Name == name);
```

Test cases:

```csharp
[Fact]
public void Test_FromJsonSchema_RootPropertiesBecomeTopLevelColumns()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{"a":{"type":"string"},"b":{"type":"number"}},"required":["a"]}
        """));

    schema.Shape.ShouldBe(RootShape.Struct);
    schema.Parquet.Fields.Select(x => x.Name).ShouldBe(new[] { "a", "b" });
    Property(schema, "a").IsRequired.ShouldBeTrue();
    Property(schema, "b").IsRequired.ShouldBeFalse();
}

[Fact]
public void Test_FromJsonSchema_RootMapBecomesValueMapColumn()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","additionalProperties":{"type":"string"}}
        """));

    schema.Shape.ShouldBe(RootShape.Map);
    schema.Parquet.Fields.ShouldHaveSingleItem().Name.ShouldBe("value");
    schema.Value!.Shape.ShouldBe(FieldShape.Map);
}

[Fact]
public void Test_FromJsonSchema_IntegerIsLongAndNumberIsDouble()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{"i":{"type":"integer"},"n":{"type":"number"}}}
        """));

    ((DataField)Property(schema, "i").Field).ClrType.ShouldBe(typeof(long));
    ((DataField)Property(schema, "n").Field).ClrType.ShouldBe(typeof(double));
}

[Fact]
public void Test_FromJsonSchema_NullUnionIsNullable()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{"v":{"type":["number","null"]}},"required":["v"]}
        """));

    Property(schema, "v").Field.IsNullable.ShouldBeTrue();
}

[Fact]
public void Test_FromJsonSchema_RequiredStringIsNonNullable()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{"s":{"type":"string"}},"required":["s"]}
        """));

    Property(schema, "s").Field.IsNullable.ShouldBeFalse();
}

[Fact]
public void Test_FromJsonSchema_ArrayBecomesListOfElement()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{"items":{"type":"array","items":{"type":"integer"}}}}
        """));

    Property(schema, "items").Shape.ShouldBe(FieldShape.List);
    ((DataField)Property(schema, "items").Value!.Field).ClrType.ShouldBe(typeof(long));
}

[Fact]
public void Test_FromJsonSchema_AdditionalPropertiesBecomesMap()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{"rates":{"type":"object","additionalProperties":{"type":["number","null"]}}}}
        """));

    var rates = Property(schema, "rates");
    rates.Shape.ShouldBe(FieldShape.Map);
    ((DataField)rates.Value!.Field).ClrType.ShouldBe(typeof(double));
    rates.Value!.Field.IsNullable.ShouldBeTrue();
}

[Fact]
public void Test_FromJsonSchema_PropertiesWinOverAdditionalProperties()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":{"type":"number"}}
        """));

    schema.Shape.ShouldBe(RootShape.Struct);
    schema.Parquet.Fields.ShouldHaveSingleItem().Name.ShouldBe("a");
}

[Fact]
public void Test_FromJsonSchema_UnknownTypeFallsBackToString()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{"x":{"$ref":"#/nope"}}}
        """));

    ((DataField)Property(schema, "x").Field).ClrType.ShouldBe(typeof(ReadOnlyMemory<char>));
}

[Fact]
public void Test_FromRecord_InfersStructFromFirstRecord()
{
    var schema = ParquetSchemaMapper.FromRecord(Json("""{"a":"x","b":2,"c":true}"""));

    schema.Shape.ShouldBe(RootShape.Struct);
    schema.Properties.Select(x => x.Name).ShouldBe(new[] { "a", "b", "c" });
}

[Fact]
public void Test_FromJsonSchema_EmptyFallsBackToDataColumn()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("{}"));

    schema.Shape.ShouldBe(RootShape.Value);
    schema.Parquet.Fields.ShouldHaveSingleItem().Name.ShouldBe("data");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:

```bash
dotnet test airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/Destination.LocalParquet.Tests.csproj --filter SchemaMapperTests
```

Expected: build fails because the mapper types do not exist.

- [ ] **Step 3: Implement `FieldMapping` and `StreamSchema`**

```csharp
using System;
using System.Collections.Generic;
using Parquet.Schema;

namespace destination_local_parquet.Parquet
{
    internal enum FieldShape { Scalar, Struct, Map, List }

    internal enum RootShape { Struct, Map, Value }

    internal sealed class FieldMapping
    {
        public required string Name { get; init; }
        public required Field Field { get; init; }
        public required FieldShape Shape { get; init; }
        public bool IsRequired { get; init; }
        public IReadOnlyList<FieldMapping> Properties { get; init; } = Array.Empty<FieldMapping>();
        public FieldMapping? Value { get; init; }
    }

    internal sealed class StreamSchema
    {
        public required ParquetSchema Parquet { get; init; }
        public required RootShape Shape { get; init; }
        public IReadOnlyList<FieldMapping> Properties { get; init; } = Array.Empty<FieldMapping>();
        public FieldMapping? Value { get; init; }

        public static StreamSchema Fallback()
        {
            var field = new DataField("data", typeof(string), isNullable: true);
            return new StreamSchema
            {
                Parquet = new ParquetSchema(field),
                Shape = RootShape.Value,
                Value = new FieldMapping { Name = "data", Field = field, Shape = FieldShape.Scalar }
            };
        }
    }
}
```

- [ ] **Step 4: Implement `ParquetSchemaMapper`**

Required behavior:
- `FromJsonSchema`: when the root has `properties`, build a `Struct` root. When the root has only `additionalProperties`, build a `Map` root with column `value`. Otherwise use `Fallback()`.
- `FromRecord`: build a `Struct` root from the record keys. Object values become `Struct`, arrays become `List`, numbers become `double`, true/false become `bool`, strings become `string`, null becomes a nullable string. A non-object root uses `Fallback()`.
- A property is required only when its name is in the parent `required` array. Nullability comes from the type union.
- `additionalProperties: true` maps to a map with string values.
- `enum` of strings maps to string.
- `$ref`, `allOf`, `oneOf`, `anyOf`, unknown or absent type map to string.
- Scalar data fields use `new DataField(name, clrType, isNullable)`.
- Map keys use `new DataField("key", typeof(string), isNullable: false)`.
- List items are named `element`.
- `MapField`, `ListField`, and `StructField` are always nullable in Parquet.Net 6.1.0. Do not try to make them required.

- [ ] **Step 5: Run the tests to verify they pass**

Run the command from Step 2. Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add airbyte-integrations/connectors/destination-local-parquet/Parquet airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/SchemaMapperTests.cs
git commit -m "feat(destination-local-parquet): map stream schemas to parquet"
```

---

### Task 3: Record value conversion

**Files:**
- Create: `airbyte-integrations/connectors/destination-local-parquet/Parquet/RecordValueConverter.cs`
- Test: `airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/ValueConverterTests.cs`

**Interfaces:**
- Consumes: `StreamSchema`, `FieldMapping`, `FieldShape`, `RootShape` from Task 2.
- Produces:
  - `internal static class RecordValueConverter` with `Dictionary<string, object?> Convert(StreamSchema schema, JsonElement record, Action<string>? warn = null)`, `object? ConvertValue(FieldMapping mapping, JsonElement value, Action<string>? warn = null)`, and `Type IdealType(Field field)`.

- [ ] **Step 1: Write the failing converter tests**

```csharp
[Fact]
public void Test_Convert_RootMap_UsesIdealKeyAndValueTypes()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","additionalProperties":{"type":"string"}}
        """));

    var row = RecordValueConverter.Convert(schema, Json("""{"USD":"US Dollar","EUR":"Euro"}"""));

    var map = row["value"].ShouldBeAssignableTo<System.Collections.IDictionary>()!;
    map.Count.ShouldBe(2);
    map.Keys.Cast<object>().ShouldAllBe(x => x!.GetType() == typeof(ReadOnlyMemory<char>));
}

[Fact]
public void Test_Convert_StructWithNullableDoubleMap()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{
          "amount":{"type":"number"},
          "base":{"type":"string"},
          "rates":{"type":"object","additionalProperties":{"type":["number","null"]}}
        },"required":["amount","base","rates"]}
        """));

    var row = RecordValueConverter.Convert(schema, Json("""
        {"amount":1.5,"base":"USD","rates":{"EUR":0.9,"GBP":null}}
        """));

    row["amount"].ShouldBe(1.5d);
    row["base"].ShouldBeOfType<ReadOnlyMemory<char>>();
    ((System.Collections.IDictionary)row["rates"]!).Count.ShouldBe(2);
}

[Fact]
public void Test_Convert_ListOfStructs()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{"items":{"type":"array","items":{
          "type":"object","properties":{"name":{"type":"string"}}}}}}
        """));

    var row = RecordValueConverter.Convert(schema, Json("""{"items":[{"name":"a"},{"name":"b"}]}"""));

    row["items"].ShouldBeAssignableTo<System.Collections.IList>()!.Cast<object>().Count().ShouldBe(2);
}

[Fact]
public void Test_Convert_MissingNullableFieldIsNull()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{"a":{"type":"string"}}}
        """));

    RecordValueConverter.Convert(schema, Json("{}"))["a"].ShouldBeNull();
}

[Fact]
public void Test_Convert_MissingRequiredFieldThrows()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}
        """));

    var error = Should.Throw<FormatException>(() => RecordValueConverter.Convert(schema, Json("{}")));

    error.Message.ShouldContain("a");
}

[Fact]
public void Test_Convert_TypeMismatchThrows()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{"flag":{"type":"boolean"}}}
        """));

    Should.Throw<FormatException>(() =>
        RecordValueConverter.Convert(schema, Json("""{"flag":3}""")));
}

[Fact]
public void Test_Convert_ExtraKeyIsIgnoredWithWarning()
{
    var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
        {"type":"object","properties":{"a":{"type":"string"}}}
        """));
    var warnings = new List<string>();

    var row = RecordValueConverter.Convert(schema, Json("""{"a":"x","b":1}"""), warnings.Add);

    row.Keys.ShouldBe(new[] { "a" });
    warnings.ShouldContain(x => x.Contains("b"));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/Destination.LocalParquet.Tests.csproj --filter ValueConverterTests
```

Expected: build fails because `RecordValueConverter` does not exist.

- [ ] **Step 3: Implement `RecordValueConverter`**

Required behavior:
- `IdealType(Field)` returns the exact type the untyped serializer expects:
  - `DataField` returns `ClrNullableIfHasNullsType`.
  - `StructField` returns `Dictionary<string, object>`.
  - `MapField` returns `Dictionary<K,V>` with ideal key and value types.
  - `ListField` returns `List<T>` with the ideal item type.
- `Convert` for `RootShape.Struct` returns a dictionary of property name to converted value. For `RootShape.Map` and `RootShape.Value` it returns `{ "value": converted }`.
- `ConvertValue`:
  - `Scalar`: string tokens become `ReadOnlyMemory<char>`; integer tokens become `long`; number tokens become `double`; true/false become `bool`; null stays null. A mismatch throws `FormatException` with the field name and token kind.
  - `Struct`: build `Dictionary<string, object?>` from present properties. Missing required properties throw. Missing optional properties are null. Extra keys call `warn`.
  - `Map`: build the exact `Dictionary<K,V>` from `IdealType`. Iterate object properties. Convert the key to `ReadOnlyMemory<char>` and the value with the value mapping.
  - `List`: build the exact `List<T>` from `IdealType`. Convert each array item.
- Use `System.Collections.IDictionary` and `System.Collections.IList` to populate the reflected generic collections.

- [ ] **Step 4: Run the tests to verify they pass**

Run the command from Step 2. Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add airbyte-integrations/connectors/destination-local-parquet/Parquet/RecordValueConverter.cs airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/ValueConverterTests.cs
git commit -m "feat(destination-local-parquet): convert records to typed parquet values"
```

---

### Task 4: Parquet record writer

**Files:**
- Create: `airbyte-integrations/connectors/destination-local-parquet/Parquet/ParquetRecordWriter.cs`
- Create: `airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/TestData.cs`
- Test: `airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/RecordWriterTests.cs`

**Interfaces:**
- Consumes: `DestinationOptions`, `ParquetSchemaMapper`, `RecordValueConverter`, `AirbyteLogger`, `ConfiguredAirbyteCatalog`.
- Produces:
  - `internal sealed class ParquetRecordWriter` with `ParquetRecordWriter(DestinationOptions options, ConfiguredAirbyteCatalog catalog, AirbyteLogger logger, int flushRowLimit = 50_000)` and `Task WriteAsync(AirbyteMessage message)`.

- [ ] **Step 1: Write the failing writer tests**

Add a helper that builds a catalog:

```csharp
private static ConfiguredAirbyteCatalog Catalog(string streamName, string jsonSchema,
    DestinationSyncMode mode = DestinationSyncMode.overwrite) => new()
{
    Streams = new[]
    {
        new ConfiguredAirbyteStream
        {
            Stream = new AirbyteStream
            {
                Name = streamName,
                JsonSchema = JsonDocument.Parse(jsonSchema).RootElement.Clone()
            },
            SyncMode = SyncMode.full_refresh,
            DestinationSyncMode = mode
        }
    }
};

private static AirbyteMessage Record(string stream, string json) => new()
{
    Type = Airbyte.Cdk.Models.Type.RECORD,
    Record = new AirbyteRecordMessage
    {
        Stream = stream,
        EmittedAt = 0,
        Data = JsonDocument.Parse(json).RootElement.Clone()
    }
};

private static AirbyteMessage State() => new() { Type = Airbyte.Cdk.Models.Type.STATE, State = new AirbyteStateMessage() };
```

Tests:

```csharp
[Fact]
public async Task Test_WriteAsync_StateFlushesAndWritesOneFile()
{
    using var dir = new TempDirectory();
    var writer = new ParquetRecordWriter(Options(dir.Path),
        Catalog("latest", TestData.Schema), new AirbyteLogger(_ => { }));

    await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
    await writer.WriteAsync(State());

    Directory.GetFiles(Path.Combine(dir.Path, "latest"), "*.parquet").ShouldHaveSingleItem();
}

[Fact]
public async Task Test_WriteAsync_OverwriteDeletesExistingParquetFiles()
{
    using var dir = new TempDirectory();
    var streamDir = Path.Combine(dir.Path, "latest");
    Directory.CreateDirectory(streamDir);
    var stale = Path.Combine(streamDir, "stale.parquet");
    File.WriteAllText(stale, "old");
    var writer = new ParquetRecordWriter(Options(dir.Path), Catalog("latest", TestData.Schema), new AirbyteLogger(_ => { }));

    await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
    await writer.WriteAsync(State());

    File.Exists(stale).ShouldBeFalse();
}

[Fact]
public async Task Test_WriteAsync_AppendKeepsExistingParquetFiles()
{
    using var dir = new TempDirectory();
    var streamDir = Path.Combine(dir.Path, "latest");
    Directory.CreateDirectory(streamDir);
    var stale = Path.Combine(streamDir, "stale.parquet");
    File.WriteAllText(stale, "old");
    var writer = new ParquetRecordWriter(Options(dir.Path),
        Catalog("latest", TestData.Schema, DestinationSyncMode.append), new AirbyteLogger(_ => { }));

    await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
    await writer.WriteAsync(State());

    File.Exists(stale).ShouldBeTrue();
}

[Fact]
public async Task Test_WriteAsync_DoesNotLeaveTmpFile()
{
    using var dir = new TempDirectory();
    var writer = new ParquetRecordWriter(Options(dir.Path), Catalog("latest", TestData.Schema), new AirbyteLogger(_ => { }));

    await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
    await writer.WriteAsync(State());

    Directory.GetFiles(dir.Path, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
}

[Fact]
public async Task Test_WriteAsync_WritesStreamMetadata()
{
    using var dir = new TempDirectory();
    var writer = new ParquetRecordWriter(Options(dir.Path), Catalog("latest", TestData.Schema), new AirbyteLogger(_ => { }));

    await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
    await writer.WriteAsync(State());

    var path = Directory.GetFiles(Path.Combine(dir.Path, "latest"), "*.parquet").Single();
    await using var stream = File.OpenRead(path);
    await using var reader = await ParquetReader.CreateAsync(stream);
    reader.CustomMetadata["airbyte.stream"].ShouldBe("latest");
}

[Fact]
public async Task Test_WriteAsync_FlushesAtRowLimit()
{
    using var dir = new TempDirectory();
    var writer = new ParquetRecordWriter(Options(dir.Path), Catalog("latest", TestData.Schema),
        new AirbyteLogger(_ => { }), flushRowLimit: 1);

    await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));

    Directory.GetFiles(Path.Combine(dir.Path, "latest"), "*.parquet").ShouldHaveSingleItem();
}

[Fact]
public async Task Test_WriteAsync_UnknownStreamThrows()
{
    using var dir = new TempDirectory();
    var writer = new ParquetRecordWriter(Options(dir.Path), Catalog("latest", TestData.Schema), new AirbyteLogger(_ => { }));

    await Should.ThrowAsync<InvalidOperationException>(() =>
        writer.WriteAsync(Record("other", """{"a":1}""")));
}

[Fact]
public async Task Test_WriteAsync_StreamNameWithTraversalStaysInsideOutputPath()
{
    using var dir = new TempDirectory();
    var writer = new ParquetRecordWriter(Options(dir.Path),
        Catalog("../escape", TestData.Schema), new AirbyteLogger(_ => { }));

    await writer.WriteAsync(Record("../escape", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
    await writer.WriteAsync(State());

    Directory.GetDirectories(dir.Path).ShouldHaveSingleItem();
    Path.GetFileName(Directory.GetDirectories(dir.Path).Single()).ShouldBe(".._escape");
}
```

Create `TestData.cs` with the shared helpers, and add `using static Destination.LocalParquet.Tests.TestData;` to the test files that use them:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Airbyte.Cdk;
using Airbyte.Cdk.Models;

namespace Destination.LocalParquet.Tests
{
    internal static class TestData
    {
        public const string Schema = """
            {"type":"object","properties":{
              "amount":{"type":"number"},
              "base":{"type":"string"},
              "date":{"type":"string"},
              "rates":{"type":"object","additionalProperties":{"type":["number","null"]}}
            },"required":["amount","base","date","rates"]}
            """;

        public static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        public static DestinationOptions Options(string root) =>
            DestinationOptions.FromJson(Json($$"""{"output_path":"{{root}}"}"""));

        public static ConfiguredAirbyteCatalog Catalog(string streamName, string jsonSchema,
            DestinationSyncMode mode = DestinationSyncMode.overwrite) => new()
        {
            Streams = new[]
            {
                new ConfiguredAirbyteStream
                {
                    Stream = new AirbyteStream
                    {
                        Name = streamName,
                        JsonSchema = JsonDocument.Parse(jsonSchema).RootElement.Clone()
                    },
                    SyncMode = SyncMode.full_refresh,
                    DestinationSyncMode = mode
                }
            }
        };

        public static AirbyteMessage Record(string stream, string json) => new()
        {
            Type = Airbyte.Cdk.Models.Type.RECORD,
            Record = new AirbyteRecordMessage
            {
                Stream = stream,
                EmittedAt = 0,
                Data = JsonDocument.Parse(json).RootElement.Clone()
            }
        };

        public static AirbyteMessage State() => new()
        {
            Type = Airbyte.Cdk.Models.Type.STATE,
            State = new AirbyteStateMessage()
        };

        public sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "parquet-writer-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose() => Directory.Delete(Path, true);
        }
    }
}
```

Use `TestData.Schema` instead of `File.Schema`, and remove the local helper definitions from the writer test snippet. The snippet already calls `Options`, `Catalog`, `Record`, `State`, `Json`, and `TempDirectory`, so the static using resolves them.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/Destination.LocalParquet.Tests.csproj --filter RecordWriterTests
```

Expected: build fails because `ParquetRecordWriter` does not exist.

- [ ] **Step 3: Implement `ParquetRecordWriter`**

Required behavior:
- Keep a dictionary keyed by `(namespace, stream)` to a buffer with the configured stream, the `StreamSchema`, the rows, and an `overwriteApplied` flag.
- On a `RECORD`:
  - Find the configured stream by name and namespace. Throw `InvalidOperationException` when absent.
  - On the first record, resolve the schema from `Stream.JsonSchema`; when the schema is missing or `{}`, use `ParquetSchemaMapper.FromRecord(message.Record.Data)`.
  - For `DestinationSyncMode.overwrite`, delete `*.parquet` in the stream directory once.
  - Convert the record with `RecordValueConverter.Convert(schema, data, logger.Warn)` and add it to the buffer.
  - Flush the buffer when it reaches `flushRowLimit`.
- On a `STATE`, flush every non-empty buffer.
- Ignore other message types.
- Flush writes `<name>.parquet.tmp`, calls `ParquetSerializer.SerializeUntypedAsync` with `customMetadata` keys `airbyte.stream`, `airbyte.namespace` (empty string when null), `airbyte.sync_mode`, and `airbyte.destination` (`local-parquet`), then moves the file to the final name.
- File names use `DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture)` and a zero-padded sequence that increments per writer instance.
- Create the stream directory before writing.
- Clear the rows after a successful flush.

- [ ] **Step 4: Run the tests to verify they pass**

Run the command from Step 2. Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add airbyte-integrations/connectors/destination-local-parquet/Parquet/ParquetRecordWriter.cs airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/RecordWriterTests.cs
git commit -m "feat(destination-local-parquet): write parquet files and flush on state"
```

---

### Task 5: Wire the writer into `LocalParquet`

**Files:**
- Modify: `airbyte-integrations/connectors/destination-local-parquet/LocalParquet.cs`
- Test: `airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/LocalParquetTests.cs`

**Interfaces:**
- Consumes: `ParquetRecordWriter` from Task 4.
- Produces: the final `LocalParquet.Write` override.

- [ ] **Step 1: Write the failing wiring test**

```csharp
[Fact]
public async Task Test_Write_RecordThenState_WritesParquetFile()
{
    using var dir = new TempDirectory();
    var connector = new LocalParquet();
    var config = Json($$"""{"output_path":"{{dir.Path}}"}""");
    var catalog = new ConfiguredAirbyteCatalog
    {
        Streams = new[]
        {
            new ConfiguredAirbyteStream
            {
                Stream = new AirbyteStream
                {
                    Name = "latest",
                    JsonSchema = JsonDocument.Parse(TestData.Schema).RootElement.Clone()
                },
                SyncMode = SyncMode.full_refresh,
                DestinationSyncMode = DestinationSyncMode.overwrite
            }
        }
    };
    var logger = new AirbyteLogger(_ => { });

    await connector.Write(logger, config, catalog,
        new AirbyteMessage
        {
            Type = Airbyte.Cdk.Models.Type.RECORD,
            Record = new AirbyteRecordMessage
            {
                Stream = "latest",
                EmittedAt = 0,
                Data = JsonDocument.Parse("""{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}""").RootElement.Clone()
            }
        });
    await connector.Write(logger, config, catalog,
        new AirbyteMessage { Type = Airbyte.Cdk.Models.Type.STATE, State = new AirbyteStateMessage() });

    Directory.GetFiles(Path.Combine(dir.Path, "latest"), "*.parquet").ShouldHaveSingleItem();
}
```

Use the `TempDirectory`, `Json`, and `TestData.Schema` helpers from Task 4. Add `using static Destination.LocalParquet.Tests.TestData;` to the file.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/Destination.LocalParquet.Tests.csproj --filter LocalParquetTests
```

Expected: fails with `NotImplementedException`.

- [ ] **Step 3: Implement `Write`**

Cache one `ParquetRecordWriter` per connector instance, created on the first message with `DestinationOptions.FromJson(config)`, the catalog, and the logger. Delegate every message to `WriteAsync`.

- [ ] **Step 4: Run the test to verify it passes**

Run the command from Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add airbyte-integrations/connectors/destination-local-parquet/LocalParquet.cs airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/LocalParquetTests.cs
git commit -m "feat(destination-local-parquet): wire writer into destination"
```

---

### Task 6: Harness connector type and source declaration

**Files:**
- Modify: `tests/Airbyte.Cdk.IntegrationTests/ConnectorSpec.cs`
- Modify: `tests/Airbyte.Cdk.IntegrationTests/ConnectorSpecLoader.cs`
- Test: `tests/Airbyte.Cdk.IntegrationTests.Tests/ConnectorSpecLoaderTests.cs`

**Interfaces:**
- Produces:
  - `public enum ConnectorType { Source, Destination }`
  - `public sealed class SourceSpec` with `string ConnectorDirectory` (absolute) and `JsonElement Config`.
  - `ConnectorSpec.Type` and `ConnectorSpec.Source`.
- `TestCaseSpec` stays unchanged.

- [ ] **Step 1: Write the failing loader tests**

Add to `ConnectorSpecLoaderTests`:

```csharp
private const string DestinationSpec = @"
connector:
  module: LocalParquet
  type: destination
source:
  connector: ../source-example
  config:
    base: USD
tests:
  - name: full refresh
    config: {}
    streams:
      - name: symbols
";

[Fact]
public void Test_Load_DestinationTypeAndSourceBlock()
{
    var repo = new TempRepository("destination-example", "source-example");
    var destination = Path.Combine(repo.Root, "airbyte-integrations", "connectors", "destination-example");
    var source = Path.Combine(repo.Root, "airbyte-integrations", "connectors", "source-example");
    File.WriteAllText(Path.Combine(destination, "integration_tests.yaml"), DestinationSpec);
    File.WriteAllText(Path.Combine(source, "Dockerfile"), "ENV AIRBYTE_IMPL_MODULE=\"ExchangeRates\"\n");
    File.WriteAllText(Path.Combine(source, "source_example.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

    var spec = ConnectorSpecLoader.Load(destination);

    spec.Type.ShouldBe(ConnectorType.Destination);
    spec.Source!.ConnectorDirectory.ShouldBe(Path.GetFullPath(source));
    spec.Source.Config.GetProperty("base").GetString().ShouldBe("USD");
    spec.Tests[0].Socket.ShouldBeTrue();
}

[Fact]
public void Test_Load_DestinationWithoutSource_Throws()
{
    using var connector = new TempConnector("destination-example")
        .WithDockerfile("LocalParquet")
        .WithProject("destination_example.csproj")
        .WithSpec(@"
connector:
  type: destination
tests:
  - name: t
    streams:
      - name: s
");

    var error = Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
    error.Message.ShouldContain("source");
}

[Fact]
public void Test_Load_DestinationWithLimit_Throws()
{
    using var connector = new TempConnector("destination-example")
        .WithDockerfile("LocalParquet")
        .WithProject("destination_example.csproj")
        .WithSpec(@"
connector:
  type: destination
source:
  connector: ../source-example
tests:
  - name: t
    limit: 10
    streams:
      - name: s
");

    var error = Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
    error.Message.ShouldContain("limit");
}

[Fact]
public void Test_Load_DestinationWithSocketFalse_Throws()
{
    using var connector = new TempConnector("destination-example")
        .WithDockerfile("LocalParquet")
        .WithProject("destination_example.csproj")
        .WithSpec(@"
connector:
  type: destination
source:
  connector: ../source-example
tests:
  - name: t
    socket: false
    streams:
      - name: s
");

    var error = Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
    error.Message.ShouldContain("socket");
}

[Fact]
public void Test_Load_UnknownConnectorType_Throws()
{
    using var connector = new TempConnector("destination-example")
        .WithDockerfile("LocalParquet")
        .WithProject()
        .WithSpec(@"
connector:
  type: worker
tests:
  - name: t
    streams:
      - name: s
");

    var error = Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
    error.Message.ShouldContain("type");
}

[Fact]
public void Test_Load_SourceTypeDefaults()
{
    using var connector = new TempConnector()
        .WithDockerfile("ExchangeRates")
        .WithProject()
        .WithSpec(MinimalSpec);

    ConnectorSpecLoader.Load(connector.Root).Type.ShouldBe(ConnectorType.Source);
}
```

Add `using System.IO;` and `using System;` to the test file if missing.

Note: the destination tests that point to `../source-example` do not need the source to exist for the `limit`/`socket`/`type` validation, as long as the loader validates those before resolving the source path. Order the checks so the declaration error wins.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Airbyte.Cdk.IntegrationTests.Tests --filter ConnectorSpecLoaderTests
```

Expected: build fails because `ConnectorType` and `SourceSpec` do not exist.

- [ ] **Step 3: Implement the model and loader changes**

Required behavior:
- Parse `connector.type` as `source` or `destination` (case-insensitive, default `source`). Unknown values are a declaration error.
- For destinations:
  - `source` is required, with `connector` (path) and optional `config` (object, default `{}`).
  - Resolve `connector` relative to the destination directory. The directory must exist and must not equal the destination directory.
  - `limit` is a declaration error.
  - `socket: false` is a declaration error. Absent `socket` becomes `true`.
- For sources, `source` is ignored.
- Keep all existing source validation unchanged.

- [ ] **Step 4: Run the tests to verify they pass**

Run the command from Step 2. Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add tests/Airbyte.Cdk.IntegrationTests/ConnectorSpec.cs tests/Airbyte.Cdk.IntegrationTests/ConnectorSpecLoader.cs tests/Airbyte.Cdk.IntegrationTests.Tests/ConnectorSpecLoaderTests.cs
git commit -m "feat(integration-tests): declare destination scenarios"
```

---

### Task 7: Harness process start, destination runner, and Parquet validation

**Files:**
- Modify: `tests/Airbyte.Cdk.IntegrationTests/ConnectorProcess.cs`
- Create: `tests/Airbyte.Cdk.IntegrationTests/RunningConnector.cs`
- Create: `tests/Airbyte.Cdk.IntegrationTests/DestinationRunner.cs`
- Create: `tests/Airbyte.Cdk.IntegrationTests/ParquetOutputValidator.cs`
- Modify: `tests/Airbyte.Cdk.IntegrationTests/Runner.cs`
- Modify: `tests/Airbyte.Cdk.IntegrationTests/Airbyte.Cdk.IntegrationTests.csproj`
- Test: `tests/Airbyte.Cdk.IntegrationTests.Tests/ParquetOutputValidatorTests.cs`

**Interfaces:**
- Consumes: `ConnectorSpec`, `SourceSpec`, `TestCaseSpec`, `StreamSpec` from Task 6; `ConnectorProcess`, `CatalogBuilder`, `MessageParser`, `Redaction`.
- Produces:
  - `ConnectorProcess.BuildAsync(CancellationToken, IReadOnlyDictionary<string,string>? properties = null)`.
  - `ConnectorProcess.RunAsync(..., string verb = "read")`.
  - `ConnectorProcess.Start(string verb, string? configPath, string? catalogPath, string? statePath, IReadOnlyList<string> socketPaths, bool closeStandardInput)` returning `RunningConnector`.
  - `public sealed class RunningConnector : IDisposable` with `int ExitCode`, `bool TimedOut`, `IReadOnlyList<JsonElement> Messages`, `string Output`, `Task<int> WaitAsync(TimeSpan timeout)`, `void Kill()`.
  - `public static class ParquetOutputValidator` with `ParquetStreamResult Validate(string parquetRoot, StreamSpec spec, JsonElement discoveredSchema)`.
  - `public sealed record ParquetStreamResult(string Name, Status Status, int FileCount, long RowCount, IReadOnlyList<string> Errors)`.

- [ ] **Step 1: Add Parquet.Net to the harness project**

In `Airbyte.Cdk.IntegrationTests.csproj`:

```xml
<PackageReference Include="Parquet.Net" />
```

- [ ] **Step 2: Write the failing validator tests**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using Parquet.Serialization;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    public class ParquetOutputValidatorTests
    {
        private static JsonElement Discovered(string schema) =>
            JsonDocument.Parse($$"""{"streams":[{"name":"latest","json_schema":{{schema}}}]}""")
                .RootElement.Clone();

        private static async Task WriteFile(string directory, string stream, int rows)
        {
            Directory.CreateDirectory(Path.Combine(directory, stream));
            var schema = new ParquetSchema(
                new DataField("amount", typeof(double), isNullable: false),
                new DataField("base", typeof(string), isNullable: false));
            var data = Enumerable.Range(0, rows)
                .Select(_ => (IDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["amount"] = 1.0d,
                    ["base"] = "USD"
                })
                .ToList();
            await using var file = File.Create(Path.Combine(directory, stream, "part.parquet"));
            await ParquetSerializer.SerializeUntypedAsync(data, schema, file);
        }

        [Fact]
        public async Task Test_Validate_CountsRows()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-validate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            await WriteFile(root, "latest", 3);

            var result = ParquetOutputValidator.Validate(root,
                new StreamSpec("latest", MinRecords: 1),
                Discovered("""{"type":"object","properties":{"amount":{"type":"number"},"base":{"type":"string"}},"required":["amount","base"]}"""));

            result.Status.ShouldBe(Status.Passed);
            result.RowCount.ShouldBe(3);
            result.FileCount.ShouldBe(1);
        }

        [Fact]
        public async Task Test_Validate_RowCountBelowMinFails()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-validate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            await WriteFile(root, "latest", 1);

            var result = ParquetOutputValidator.Validate(root,
                new StreamSpec("latest", MinRecords: 2),
                Discovered("""{"type":"object","properties":{"amount":{"type":"number"},"base":{"type":"string"}},"required":["amount","base"]}"""));

            result.Status.ShouldBe(Status.Failed);
            result.Errors.ShouldNotBeEmpty();
        }

        [Fact]
        public void Test_Validate_MissingDirectoryFails()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-validate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var result = ParquetOutputValidator.Validate(root,
                new StreamSpec("latest"),
                Discovered("""{"type":"object","properties":{}}"""));

            result.Status.ShouldBe(Status.Failed);
        }

        [Fact]
        public async Task Test_Validate_SeededStaleFileIsIgnored()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-validate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "latest"));
            File.WriteAllText(Path.Combine(root, "latest", "stale.parquet"), "old");
            await WriteFile(root, "latest", 1);

            var result = ParquetOutputValidator.Validate(root,
                new StreamSpec("latest", MinRecords: 1),
                Discovered("""{"type":"object","properties":{"amount":{"type":"number"},"base":{"type":"string"}},"required":["amount","base"]}"""));

            result.Status.ShouldBe(Status.Passed);
            result.FileCount.ShouldBe(1);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test tests/Airbyte.Cdk.IntegrationTests.Tests --filter ParquetOutputValidatorTests
```

Expected: build fails because `ParquetOutputValidator` does not exist.

- [ ] **Step 4: Implement `ParquetOutputValidator`**

Required behavior:
- Stream directory: `<parquetRoot>/<stream>`. Files: `*.parquet` directly in that directory, excluding `stale.parquet`.
- A missing directory or zero files is a failure.
- Open each file with `ParquetReader.CreateAsync`, sum `reader.Metadata.RowGroups.Sum(x => (long)x.NumRows)`.
- Fail when the total is below `spec.MinRecords`.
- Schema check against the discovered schema:
  - root `properties`: each property name must exist in `reader.Schema.Fields`.
  - root `additionalProperties`: one field named `value` with `SchemaType.Map`.
  - otherwise: one field named `value`.
- Read up to 100 rows with `ParquetSerializer.DeserializeUntypedAsync` from the first file. For each required root property, at least one row must have a non-null value. For a root map, the `value` entry must be non-null in at least one row.
- Return `Status.Passed` with no errors, or `Status.Failed` with messages.

- [ ] **Step 5: Run the validator tests to verify they pass**

Run the command from Step 3. Expected: all pass.

- [ ] **Step 6: Add `RunningConnector` and the `ConnectorProcess` start/verb/property support**

Required behavior:
- `RunningConnector` wraps a started `Process`. It drains stdout and stderr on background tasks, parses JSON messages with `MessageParser`, and exposes the collected messages and raw output. `WaitAsync` applies a timeout, kills the process on expiry, and sets `TimedOut`. `Kill` is idempotent.
- `ConnectorProcess.BuildAsync` accepts optional MSBuild properties and passes each as `-p:Name=Value`.
- `ConnectorProcess.RunAsync` accepts a `verb` parameter and uses it instead of `read`.
- `ConnectorProcess.Start` builds the same environment as `ExecuteAsync`, uses `SOCKET`/`PROTOBUF` when `socketPaths` is non-empty, starts the process, optionally closes stdin, and returns a `RunningConnector`.

- [ ] **Step 7: Implement `DestinationRunner` and the `Runner` dispatch**

Required behavior:
- `Runner.RunAsync` loads the spec, creates the artifacts directory, and dispatches to the existing source flow or `DestinationRunner`. Move the existing body into a private `RunSourceAsync` without behavior changes.
- `DestinationRunner` runs these steps:
  1. Build the destination.
  2. Run destination `spec` with `verb: "write"` and require a `SPEC` message.
  3. Write the destination config to `<scenario>/config.json`, then set `output_path` to `<scenario>/parquet`.
  4. Run destination `check` with `verb: "write"` and require `CONNECTION_STATUS` `SUCCEEDED`.
  5. Load the source spec from `spec.Source!.ConnectorDirectory`. Reject a source whose `Type` is not `Source`.
  6. Build the source with `properties: { ["UseLocalCdk"] = "true" }`.
  7. Write the source config to `<scenario>/source_config.json`.
  8. Run source `check` and `discover`; build the catalog with `CatalogBuilder.Build`.
  9. Write the state file from the scenario streams, if any.
  10. Create two socket paths under `<scenario>/sockets`.
  11. Seed `stale.parquet` in each stream directory under `<scenario>/parquet`.
  12. Start the source, then the destination, with the same socket paths. Close the destination's stdin.
  13. Wait for the source. If it fails or times out, kill the destination. Then wait for the destination.
  14. Require both exit codes to be zero. Check both message lists for error traces. Require at least one `STATE` from the destination.
  15. Validate each stream with `ParquetOutputValidator`.
  16. For `overwrite`, require `stale.parquet` to be gone. For `append`, require it to remain.
  17. Return a `ConnectorResult` with one `TestResult` per scenario and `StreamResult` entries built from the Parquet results.
- Reuse `Redaction` for error text. Destination secrets are not supported in this change; the destination `config` is literal.

- [ ] **Step 8: Build the harness and run the existing tests**

```bash
dotnet build tests/Airbyte.Cdk.IntegrationTests
dotnet test tests/Airbyte.Cdk.IntegrationTests.Tests
```

Expected: build succeeds and the existing tests plus the new validator tests pass.

- [ ] **Step 9: Commit**

```bash
git add tests/Airbyte.Cdk.IntegrationTests tests/Airbyte.Cdk.IntegrationTests.Tests
git commit -m "feat(integration-tests): run destination scenarios over protobuf sockets"
```

---

### Task 8: Source build switch and connector integration declaration

**Files:**
- Modify: `airbyte-integrations/connectors/source-exchange-rates-free/source_exchange_rates.csproj`
- Create: `airbyte-integrations/connectors/destination-local-parquet/integration_tests.yaml`

**Interfaces:**
- Consumes: `DestinationRunner` from Task 7.
- Produces: the scenario declaration that the harness runs.

- [ ] **Step 1: Add the conditional CDK reference to the source project**

Replace the `Airbyte.Cdk` package reference with:

```xml
<ItemGroup Condition="'$(UseLocalCdk)' == 'true'">
  <ProjectReference Include="..\..\..\Airbyte.Cdk\Airbyte.Cdk.csproj" />
</ItemGroup>
<ItemGroup Condition="'$(UseLocalCdk)' != 'true'">
  <PackageReference Include="Airbyte.Cdk" />
</ItemGroup>
```

- [ ] **Step 2: Create `integration_tests.yaml`**

```yaml
connector:
  module: LocalParquet
  type: destination

source:
  connector: ../source-exchange-rates-free
  config:
    start_date: "2026-10-28"
    base: USD

tests:
  - name: full refresh overwrite over protobuf
    timeout_seconds: 600
    config: {}
    streams:
      - name: symbols
      - name: latest

  - name: incremental append over protobuf
    timeout_seconds: 600
    config: {}
    streams:
      - name: historical
        sync_mode: incremental
        state:
          historical:
            USD: "2026-10-28"
```

- [ ] **Step 3: Build the source with the switch**

```bash
dotnet build airbyte-integrations/connectors/source-exchange-rates-free/source_exchange_rates.csproj -p:UseLocalCdk=true
```

Expected: build succeeds. The default build without the property still succeeds and uses the package.

- [ ] **Step 4: Commit**

```bash
git add airbyte-integrations/connectors/source-exchange-rates-free/source_exchange_rates.csproj airbyte-integrations/connectors/destination-local-parquet/integration_tests.yaml
git commit -m "test(destination-local-parquet): declare protobuf e2e scenarios"
```

---

### Task 9: Run the end-to-end scenarios and fix failures

**Files:**
- Modify: any file from Tasks 1-8 as needed.

- [ ] **Step 1: Run the harness for the destination**

```bash
dotnet run --project tests/Airbyte.Cdk.IntegrationTests -- \
  run --connector airbyte-integrations/connectors/destination-local-parquet --verbose
```

Expected: both scenarios PASS, with per-stream file and row counts.

- [ ] **Step 2: Run the source's own scenarios to prove no regression**

```bash
dotnet run --project tests/Airbyte.Cdk.IntegrationTests -- \
  run --connector airbyte-integrations/connectors/source-exchange-rates-free
```

Expected: all source scenarios PASS.

- [ ] **Step 3: Fix any failure at its root cause**

Common failures and the owning area:
- Socket file not created: the source was built without `UseLocalCdk`. Check the source build properties.
- Destination exits before the source: stdin close ordering or the composite reader. Check `RunningConnector` and `DestinationRunner`.
- Parquet conversion error: the mapper or converter. Add a unit test that reproduces the record, then fix.
- State never echoed: the destination dispatcher waits for per-partition record counts. Check that the source's terminal state matches the partitions.
- Overwrite seed remains: the stream directory name differs. Check `DestinationOptions.StreamDirectory`.

- [ ] **Step 4: Commit the fixes**

```bash
git add -A
git commit -m "fix(destination-local-parquet): make protobuf e2e pass"
```

---

### Task 10: Documentation, final verification, and pull request

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `airbyte-integrations/connectors/destination-local-parquet/README.md`
- Modify: `airbyte-integrations/connectors/destination-local-parquet/CHANGELOG.md` (only if version changes)

- [ ] **Step 1: Update the root `README.md`**

- Add a row to the Components table:

```markdown
Local Parquet | Destination | [destination-local-parquet](airbyte-integrations/connectors/destination-local-parquet) | `docker pull airbytedotnet/destination-local-parquet` | ![Docker Image Version (latest semver)](https://img.shields.io/docker/v/airbytedotnet/destination-local-parquet?sort=semver&style=flat)
```

- In the integration test section, add a short destination paragraph: `connector.type: destination`, the `source` block, Protobuf-only scenarios, no `limit`, and the `output_path` injection.

- [ ] **Step 2: Update `AGENTS.md`**

Add to the repository map that `airbyte-integrations/connectors/` now contains a destination, and add a short note under "Adding or changing a connector": destination scenarios use `connector.type: destination` and a `source` block, and the harness builds the source with `-p:UseLocalCdk=true` until the next CDK release.

- [ ] **Step 3: Run the full verification**

```bash
dotnet build Airbyte.Cdk.sln
dotnet test Airbyte.Cdk.sln
dotnet test airbyte-integrations/connectors/destination-local-parquet/tests/Destination.LocalParquet.Tests/Destination.LocalParquet.Tests.csproj
dotnet run --project tests/Airbyte.Cdk.IntegrationTests -- \
  run --connector airbyte-integrations/connectors/destination-local-parquet
dotnet run --project tests/Airbyte.Cdk.IntegrationTests -- \
  run --connector airbyte-integrations/connectors/source-exchange-rates-free
```

Expected: all pass. The e2e runs need network access to `frankfurter.dev`.

- [ ] **Step 4: Build the connector image**

```bash
docker build -f airbyte-integrations/connectors/destination-local-parquet/Dockerfile -t destination-local-parquet:test .
```

Expected: image builds and the nested unit tests run inside the build stage.

- [ ] **Step 5: Commit and push**

```bash
git add README.md AGENTS.md airbyte-integrations/connectors/destination-local-parquet
git commit -m "docs(destination-local-parquet): document the destination"
git push -u origin local-parquet-destination-connector
```

- [ ] **Step 6: Create the pull request**

```bash
/home/jean/.local/share/com.jean.desktop/gh-cli/gh pr create \
  --title "feat: add local Parquet destination connector with Protobuf e2e" \
  --body "Adds destination-local-parquet, a typed Parquet writer for local files, plus a black-box destination mode in the integration harness. The e2e runs source-exchange-rates-free into the destination over Protobuf Unix domain sockets.

- Typed columns from the stream JSON schema (structs, lists, maps)
- Flush on STATE, append and overwrite sync modes, overwrite deletes old files
- Destination scenarios in tests/Airbyte.Cdk.IntegrationTests
- Uses a temporary ProjectReference to Airbyte.Cdk until the socket transport is released to NuGet"
```

Expected: a pull request URL.

---

## Self-Review

- Spec coverage: connector config/spec/check (Task 1), schema mapping (Task 2), value conversion (Task 3), writer, file layout, sync modes, atomic move (Task 4), write wiring (Task 5), harness declaration (Task 6), runner and validation (Task 7), source build switch and scenarios (Task 8), e2e proof (Task 9), docs and PR (Task 10).
- Type consistency: `DestinationOptions`, `StreamSchema`, `FieldMapping`, `StreamSchema.Fallback`, `ParquetRecordWriter`, `RunningConnector`, `ParquetStreamResult`, and `SourceSpec` are defined once and reused with the same names.
- Review Focus coverage: required field (Task 3), root map (Tasks 2, 3, and 7), path traversal (Task 4), type mismatch (Task 3), state flush and overwrite (Task 4).
