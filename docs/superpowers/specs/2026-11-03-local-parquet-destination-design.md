# Local Parquet Destination Connector - Design

Date: 2026-11-03

Status: awaiting approval

## Intent and why

Add a destination connector that writes Airbyte records to local Parquet files. The connector is a
development and debugging tool and a working example of the CDK destination path. It also proves the
Protobuf socket transport in both directions: the Exchange Rates source writes records, and the
destination reads them.

The repository has no destination connector and no destination integration test. This design adds
both.

## Goals

- A new connector at `airbyte-integrations/connectors/destination-local-parquet`.
- Typed Parquet columns derived from the stream JSON schema, including nested structs, lists, and maps.
- One Parquet file per stream per flush. Files go under a configured local directory.
- Correct destination protocol behavior: flush before a `STATE` returns, and support `append` and
  `overwrite` destination sync modes.
- A permanent, black-box end-to-end test in `tests/Airbyte.Cdk.IntegrationTests` that runs
  `source-exchange-rates-free` into the destination over Protobuf Unix domain sockets and checks the
  Parquet output.

## Non-goals

- Cloud storage, S3, or object-store layouts.
- Normalization, dbt, deduplication, or primary-key handling.
- A Parquet reader API in the CDK.
- Changing the CDK transport or destination base classes.
- A general destination connector template. The `init` command stays source-only in this change.

## Anti-goals

- Storing the whole record as one opaque JSON string column. That is valid Parquet, but it is not the
  typed output this design promises.
- Loading a whole stream into memory. Flushes bound the buffer.
- Treating a skipped end-to-end test as a pass.

## Constraints

- .NET 10, C# with block-scoped namespaces, 4-space indentation, and the existing CDK style.
- Central package management: add `Parquet.Net` 6.1.0 to `Directory.Packages.props`.
- Parquet.Net 6.1.0 targets `net10.0` and supports untyped serialization from
  `IDictionary<string, object?>` with a supplied `ParquetSchema`.
- The connector uses a `ProjectReference` to `Airbyte.Cdk` for this PR (see the CDK reference decision).
- The Airbyte protocol version is `0.19.0`.
- The integration harness stays black-box: it never references a connector assembly.

## CDK reference decision

Tag `0.0.8` is on `main` and does not contain the socket/Protobuf transport from commit `385c5ae`.
The destination must run in socket mode, so a connector built against the published `0.0.8` package
cannot pass the Protobuf scenario.

Chosen: the destination project uses a `ProjectReference` to
`Airbyte.Cdk/Airbyte.Cdk.csproj` for this PR. This works today, including the Docker build from the
repository root and the integration harness build. It departs from the repository convention that
connectors reference the published package.

Follow-up: after the next CDK release (`0.0.9` or later) that contains the socket transport, replace the
`ProjectReference` with `<PackageReference Include="Airbyte.Cdk" />` and let central package management
pin the version. Record this follow-up in the connector `CHANGELOG.md` and keep the project file free of
comments.

The Exchange Rates source also restores `Airbyte.Cdk` `0.0.8`, so it cannot write Protobuf sockets. The
harness builds the source with `-p:UseLocalCdk=true`, and the source project gets this switch:

```xml
<ItemGroup Condition="'$(UseLocalCdk)' == 'true'">
  <ProjectReference Include="..\..\..\Airbyte.Cdk\Airbyte.Cdk.csproj" />
</ItemGroup>
<ItemGroup Condition="'$(UseLocalCdk)' != 'true'">
  <PackageReference Include="Airbyte.Cdk" />
</ItemGroup>
```

The default source build is unchanged, so the source image keeps using the published package. Remove the
switch after the next CDK release, together with the destination `ProjectReference`.

Alternatives rejected:

- Publish CDK `0.0.9` first, then merge the destination with a package pin. Correct release order, but it
  needs two steps and the integration test cannot pass before `0.0.9` exists on nuget.org.
- A conditional reference: project inside the repo, package outside. It works in both places, but it is
  more complex and hides the difference during review.

## Approaches considered

### A. Typed columns from the stream schema (chosen)

Build a `ParquetSchema` from `ConfiguredAirbyteStream.Stream.JsonSchema` and pass record dictionaries to
`ParquetSerializer.SerializeUntypedAsync`.

Trade-off: the most code, because JSON schema must map to Parquet fields and JSON values must map to CLR
values. It gives real typed columns and handles `additionalProperties` maps such as `rates`.

### B. One JSON string column per record

Table schema: `data string`, `_airbyte_stream string`, `_airbyte_emitted_at long`.

Trade-off: trivial and lossless, but it defeats Parquet columnar reads and does not show a usable
destination. Rejected by the user.

### C. Typed top-level columns, nested values as JSON strings

Map each top-level property to a column, but write objects and arrays as JSON text.

Trade-off: easier than A, but `rates` becomes a string, so common analytical queries need JSON
parsing. Rejected because A is feasible.

### Harness approach

Three options were considered for the end-to-end test:

1. Extend `tests/Airbyte.Cdk.IntegrationTests` with destination scenarios (chosen).
2. A new xUnit end-to-end project.
3. A one-off script plus unit tests.

Option 1 keeps one black-box harness, one `integration_tests.yaml` convention, and one CI path. The
cost is the largest change in the test project.

## Connector design

### Project layout

```
airbyte-integrations/connectors/destination-local-parquet/
  CHANGELOG.md
  Dockerfile
  LocalParquet.cs
  NuGet.Config
  README.md
  destination_local_parquet.csproj
  integration_tests.yaml
  spec.json
  Parquet/                 # internal implementation
    ParquetRecordWriter.cs
    ParquetSchemaMapper.cs
    RecordValueConverter.cs
    Options.cs
```

`LocalParquet : AbstractDestination` implements `Check` and `Write`. It also has a static
`Main(string[] args)` that calls `AirbyteEntrypoint.Main(args)`, like `ExchangeRates`.

`ParquetRecordWriter`, `ParquetSchemaMapper`, and `RecordValueConverter` are internal classes. They hold
the logic that is hard to test through the process boundary.

### Configuration and spec

`spec.json` properties:

| Property | Type | Required | Description |
|---|---|---|---|
| `output_path` | string | yes | Directory that receives the Parquet files. Created when missing. |

`ConnectorSpecification` fields:

- `protocol_version`: `0.19.0`
- `supportsIncremental`: `true`
- `supported_destination_sync_modes`: `["append", "overwrite"]`
- `supportsNormalization`: `false`
- `supportsDBT`: `false`

Compression stays at the Parquet.Net default (Snappy). It is not configurable in this change.

### Check

`Check` returns `SUCCEEDED` when:

- `output_path` is present and is not an existing file.
- The directory can be created.
- A probe file can be written and deleted.

Otherwise it returns `FAILED` with a short message. The check does not write Parquet data.

### Write flow

`Write` receives one `AirbyteMessage` at a time.

- `RECORD`: buffer the record under its stream key. Buffer entries hold the parsed JSON data and the
  configured stream. If the stream uses `overwrite` and this is its first record in the process,
  delete the existing `.parquet` files for that stream first.
- `STATE`: flush every non-empty buffer, then return. The dispatcher echoes the state after `Write`
  returns.
- Any other type: ignore it.

The buffer also flushes when it reaches 50,000 rows, so a long run without checkpoints keeps memory
bounded. A flush writes one file with one row group.

The destination does not implement `IParallelDestination`. Sequential writes are safe and enough.

The destination has no end-of-input hook, and this change does not add one. A normal source run ends
with a terminal `STATE`, which flushes the buffer. This design therefore does not support stopping a
destination scenario early: a killed source would leave the last buffer unwritten.

### File layout and naming

- Stream directory: `<output_path>/<namespace>/<stream>` when the stream has a namespace, otherwise
  `<output_path>/<stream>`.
- File name: `part-<utc timestamp with milliseconds>-<sequence>.parquet`.
- Sequence increments per process, so two flushes in the same millisecond cannot collide.
- Namespace, stream, and file names are sanitized. Path separators, `..`, and invalid file-name
  characters become `_`. An empty result becomes `stream`.
- `overwrite` deletes only `.parquet` files in that stream directory. It does not delete directories
  outside it.
- Each file is written to `<name>.parquet.tmp` and then moved to `<name>.parquet`, so a crash never
  leaves a readable partial file. The test asserts that no `.tmp` file remains.

File metadata: `airbyte.stream`, `airbyte.namespace`, `airbyte.sync_mode`, and `airbyte.destination`.

### Schema mapping

The schema comes from the configured catalog for the stream. When the catalog schema is missing or
`{}`, the destination infers a schema from the first buffered record. When inference also fails, the
stream falls back to a single `data` string column that holds the record JSON.

Root node rules:

- Root `object` with `properties`: its properties become the top-level Parquet columns. The root is not
  wrapped in a struct.
- Root `object` with `additionalProperties` and no `properties` (the `symbols` stream): the record
  becomes one `MapField<string, V>` column named `value`.
- Root schema that is not an object: one column named `value`.
- Root object with both `properties` and `additionalProperties`: `properties` win and extra keys are
  ignored with a warning.

Mapping rules for a JSON schema node:

| JSON schema | Parquet field |
|---|---|
| `"type": "string"` | `DataField<string>` |
| `"type": "integer"` | `DataField<long>` |
| `"type": "number"` | `DataField<double>` |
| `"type": "boolean"` | `DataField<bool>` |
| `"type": "object"` with `properties` | `StructField` with one child per property |
| `"type": "object"` with `additionalProperties` (object or `true`) | `MapField<string, V>` where `V` is the mapped `additionalProperties` schema |
| `"type": "array"` with `items` | `ListField` whose item is named `element` |
| `"type": ["X", "null"]` or `"null"` union | the field for `X`, nullable |
| `enum` with strings | `DataField<string>` |
| `$ref`, `allOf`, `oneOf`, `anyOf`, unknown or absent type | `DataField<string>` holding JSON text |

- `additionalProperties: true` maps to `MapField<string, string>`.
- Nullability comes from the type union. A `["string", "null"]` property is nullable even when listed in
  `required`.
- `required` controls presence only. A missing required key fails the flush with a clear message that
  names the stream and the field, even when the field is nullable.
- A field that is not listed in `required` is nullable. Struct, list, and map containers are always
  optional in Parquet.Net 6.1.0 because its public constructors do not expose container nullability;
  `required` still controls presence.
- Map keys are always non-nullable strings.
- If the schema changes between records of one buffer (a new property appears), the new property is
  ignored for that flush and a warning is logged. The next flush uses the schema of the first record in
  that buffer. This keeps Parquet schemas stable per file.

Inference rules, used only when the catalog schema is missing:

- Use the first record in the buffer.
- A JSON object becomes a `StructField` with one child per key. Dynamic-key inference is not attempted.
- A JSON array becomes a `ListField`, typed from the first non-null item.
- `null` infers a nullable `DataField<string>`.
- A record whose root is not an object uses the `value` column.
- Inference fails when the record is not a JSON object and is not an object map. The `data` fallback
  then applies.

### Value conversion

Conversion is driven by the declared Parquet field type. `JsonElement` to CLR values:

- string: `string`
- true/false: `bool`
- number: `double`
- integer: `long`; a value outside the `long` range becomes `double`
- null: `null`
- object: `Dictionary<string, object?>`; for a `MapField`, the same dictionary
- array: `List<object?>` with converted items
- a numeric token for a string field uses its invariant text
- any other mismatch throws a flush error that names the stream, the field, and the token kind; it does
  not silently write a different type

### Errors

A write failure throws. `AirbyteEntrypoint` converts the exception to an error trace and exit code 1. The
buffer is not cleared after a failed flush, so a retry of the process re-reads the same records from the
source.

## Integration harness design

### Declaration

Destinations use the same file name, `integration_tests.yaml`, with a new optional `type` and a new
`source` block:

```yaml
connector:
  module: LocalParquet
  type: destination          # default: source

source:                      # required when type is destination
  connector: ../source-exchange-rates-free   # path relative to the destination directory
  config:
    start_date: "2026-10-28"
    base: USD

tests:
  - name: full refresh overwrite over protobuf
    timeout_seconds: 600
    config: {}               # destination config; the harness sets output_path itself
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

- `type` must be `source` or `destination`.
- `socket` must be `true` for destination scenarios, or absent (the default becomes `true` for
  destinations). The destination test exists to exercise Protobuf.
- `limit` is not supported for destination scenarios. A declaration with `limit` is a declaration
  error, because a killed source cannot flush its last buffer. Scenarios use small stream ranges
  instead.
- The harness writes the scenario config with `output_path` set to `<artifacts>/<scenario>/parquet`. A
  user-supplied `output_path` in the YAML is overwritten.
- The source connector path is resolved relative to the destination connector directory. The source
  module and project are resolved the same way as for a source scenario: from the source
  `integration_tests.yaml` or its Dockerfile.

### Execution

1. Build the destination.
2. Run destination `write --command spec` and check that `CONNECTION_STATUS` is `SUCCEEDED` for a check
   with `output_path` in the scenario directory.
3. Load the source descriptor, build the source, and run source `check` and `discover`.
4. Build the configured catalog with `CatalogBuilder.Build`. Use the scenario stream list, so full refresh
   streams get `overwrite`. For an incremental scenario, use `append`.
5. Create two socket paths in a temporary directory.
6. Seed the stream directories to prove the sync mode: write a stale `stale.parquet` file in each listed
   stream directory. An `overwrite` scenario must delete it; an `append` scenario must keep it.
7. Start the source with `read --command read`, the source config, the catalog, the optional state, and the
   socket environment.
8. Start the destination with `write --command write`, the destination config, the catalog, and the same
   socket environment. Close its standard input immediately; in socket mode records and states arrive on
   the sockets.
9. Consume both standard output and standard error streams to prevent blocking. Parse messages from the
   source and destination with `MessageParser`.
10. Wait for both processes with the scenario timeout. On timeout, kill both and fail the scenario.
11. Assert both exit codes are zero.
12. Validate the Parquet output (below).
13. Assert that the destination emitted no error trace, and that it emitted at least one `STATE`. A normal
    source run always ends with a terminal state, even for full refresh streams.

### Parquet validation

- Enumerate every `*.parquet` file under `<scenario>/parquet`.
- Fail when a `.tmp` file exists.
- For each configured stream, open every file under its directory with `ParquetReader` and sum the row
  counts. Ignore the seeded `stale.parquet` file, which is not valid Parquet.
- Fail when the total is below `min_records` (default 1).
- Check the file schema against the discovered JSON schema:
  - root `properties`: every top-level property has a field with the same name;
  - root `additionalProperties`: one map field named `value`;
  - other roots: one field named `value`.
  A map property produces one map field. A struct property produces one struct field.
- Read a sample of rows (up to 100) and assert that each required top-level property is non-null in at
  least one row.
- Report the file count and row count per stream, the same way source scenarios report record counts.

### Sync mode proof

- The overwrite scenario seeds `stale.parquet` in the `symbols` and `latest` directories and asserts it is
  gone.
- The incremental scenario seeds `stale.parquet` in the `historical` directory and asserts it is still
  present, so append did not clean the directory.

### Limits and stopping

- Destination scenarios run to completion. There is no cap and no early kill.
- The incremental scenario gets a recent state so `historical` reads only a few days.

### Report and exit codes

The report shows destination scenarios with the same PASS/SKIP/FAIL labels and per-stream row counts. Exit
codes do not change: 0 pass or skip, 1 failure, 2 declaration error.

## Tests

- End-to-end: two destination scenarios run Exchange Rates over Protobuf and validate Parquet and the sync
  modes. This is the main proof.
- Local command to run when developing:

  ```bash
  dotnet run --project tests/Airbyte.Cdk.IntegrationTests -- \
    run --connector airbyte-integrations/connectors/destination-local-parquet
  ```

- Unit tests for the schema mapper and value converter are optional. The connector project has no test
  project today. Add one only if the end-to-end test does not cover a mapping rule that matters.

## CI, docs, and release

- `integration-tests.yml` triggers on changes under `airbyte-integrations/connectors/**`. A destination
  change runs the destination scenarios. A source-only change runs only the source connector, because
  `ChangedConnectors` maps files to their own connector directory. This design accepts that gap: the
  destination scenario runs on any change to the destination, and on manual runs.
- `build-connector.yml` builds changed connector images on pull requests. The destination Dockerfile uses
  the `write --command` entrypoint and `AIRBYTE_IMPL_MODULE=LocalParquet`.
- Add the destination to the components table in `README.md`.
- Add the destination declaration rules to the integration test section of `README.md`.
- `CHANGELOG.md` starts at `0.0.1`.
- The project reference follows the CDK reference decision above. This change does not change the CDK
  source.

## What is left to the builder

- Exact public names of internal classes and methods.
- Whether to add a small unit test project for the mapper and converter.
- The exact wording of log messages.
- The Dockerfile base-image details, copied from the existing connector pattern.
- Any Parquet.Net option needed to keep the output readable by DuckDB and pyarrow. The default Snappy
  compression and one row group per file should be enough.
