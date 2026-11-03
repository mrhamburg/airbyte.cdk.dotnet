# Local Parquet Destination

Writes Airbyte records to typed Parquet files on the local filesystem. One file is written per stream
and per flush, under `output_path`.

## Config

| Property | Required | Default | Description |
|---|---|---|---|
| `output_path` | yes | — | Directory that receives the Parquet files. Mount it as a volume when you run the Docker image. |
| `flush_row_limit` | no | `50000` | Maximum buffered rows per stream before a file is written. |

## Layout

`<output_path>/<namespace?>/<stream>/part-<timestamp>-<sequence>.parquet`

`overwrite` writes the new files first and deletes older generations after the sync completes. A failed
sync therefore keeps the previous data. `append` adds new files and keeps older ones.

## Type mapping

| JSON schema | Parquet |
|---|---|
| `string`, `enum`, unknown or absent type | string (`UTF8`) |
| `integer` | `INT64` (`long`) |
| `number` | `DOUBLE` |
| `boolean` | `BOOLEAN` |
| `object` with `properties` | struct |
| `object` with `additionalProperties` | map with string keys |
| `array` | list |

`required` fields must be present. Structs, lists, and maps are nullable. A value that contradicts its
declared type fails the sync with the field name.

## Limitations

- No `append_dedup`, `update`, or `soft_delete` support; the connector rejects them.
- No normalization or primary-key deduplication.
- No `_airbyte_emitted_at` or other `_airbyte_*` columns. File metadata carries the stream, namespace,
  sync mode, and destination name.
- One file per stream per `STATE`. Use `flush_row_limit` to bound memory, but a chatty checkpoint can
  still produce many small files.
- `overwrite` cleanup is scoped to the stream directory. Two streams that sanitize to the same name do
  not collide because the name is escaped, but a manual rename can break the layout.

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

The destination declares two scenarios in `integration_tests.yaml`: a full refresh with `overwrite` and an
incremental read with `append`, both fed by `source-exchange-rates-free` over Protobuf Unix domain sockets.
The project temporarily references `Airbyte.Cdk` as a `ProjectReference` until the next CDK release ships the
socket transport; the harness builds the source with `-p:UseLocalCdk=true` for the same reason. Track the switch
back to the NuGet package in [#31](https://github.com/mrhamburg/airbyte.cdk.dotnet/issues/31).
