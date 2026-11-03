# OpenDAL Parquet Destination

Writes Airbyte records to typed Parquet objects through [Apache OpenDAL](https://opendal.apache.org/).
The connector uses the OpenDAL `fs` service by default, so it works as a local destination. Any OpenDAL
scheme can be used by setting the service and its options inline, in a settings file, or with environment
variables.

One object is written per stream and per flush under `output_path`.

## Config

| Property | Required | Default | Description |
|---|---|---|---|
| `output_path` | yes | — | Service-relative key prefix that receives the Parquet objects. For `fs` with root `/`, an absolute path is accepted. |
| `service` | no | `fs` | OpenDAL scheme, for example `fs`, `s3`, `gcs`, `azblob`, `sftp`, or `memory`. |
| `settings_file` | no | — | JSON file with `service`, `options`, and optional Parquet settings. Relative paths resolve from the process working directory. |
| `options` | no | `{}` | Service-specific OpenDAL options. Values are converted to strings. |
| `flush_row_limit` | no | `50000` | Maximum buffered rows per stream before a Parquet object is written. |
| `flush_on_state` | no | `true` | Flush buffered rows when a state message arrives. |
| `file_name_template` | no | `part-{timestamp}-{sequence}.parquet` | Object file name. Placeholders: `{timestamp}`, `{sequence}`, `{uuid}`, `{stream}`, `{namespace}`. Must end in `.parquet` and contain `{sequence}` or `{uuid}`. |
| `parquet.compression` | no | Parquet.Net default (`snappy`) | `none`, `snappy`, `gzip`, `lzo`, `brotli`, `lz4`, `lz4_raw`, or `zstd`. |
| `parquet.row_group_size` | no | Parquet.Net default | Maximum rows per Parquet row group. |

`flush_on_state=false` is available, but it is a correctness risk: the CDK echoes a state message after
`Write` returns, so a crash can lose records that the platform already acknowledged. Keep it enabled.

## OpenDAL configuration

### Settings file

```json
{
  "service": "fs",
  "options": { "root": "/tmp/airbyte-root" },
  "parquet": { "compression": "snappy", "row_group_size": 100000 },
  "flush_row_limit": 50000,
  "flush_on_state": true,
  "file_name_template": "part-{timestamp}-{sequence}.parquet"
}
```

Use `settings_file` in the Airbyte config or `OPENDAL_SETTINGS` in the environment. A relative path
resolves from the process working directory. Unknown properties are rejected.

### Environment variables

| Variable | Meaning |
|---|---|
| `OPENDAL_SETTINGS` | Settings-file path. An inline `settings_file` wins. |
| `OPENDAL_SERVICE` | OpenDAL scheme. `OPENDAL_SCHEME` is an alias. |
| `OPENDAL_OPTIONS` | JSON object of options. |
| `OPENDAL_OPTION_<KEY>` | One generic option, for example `OPENDAL_OPTION_ENDPOINT`. |
| `OPENDAL_<SERVICE>_<KEY>` | Native-style option, for example `OPENDAL_S3_BUCKET` or `OPENDAL_FS_ROOT`. |

### Precedence

1. Inline `service` and `options` in the Airbyte config.
2. Environment variables.
3. The settings file.
4. Built-in defaults: `service=fs` and `options.root=/`.

Options merge key by key. A `null` value removes an inherited option. `output_path` always comes from
the Airbyte config.

For `fs`, the connector sets `root=/` when no root is configured. This keeps an absolute `output_path`
working as before: `/tmp/out` becomes key `tmp/out`.

### Examples

Local filesystem through the default service:

```json
{ "output_path": "/tmp/airbyte-out" }
```

Local filesystem with a settings file:

```json
{ "output_path": "out", "settings_file": "/etc/airbyte/opendal.json" }
```

```json
{ "service": "fs", "options": { "root": "/tmp/airbyte-root" } }
```

S3 through environment variables:

```bash
OPENDAL_SERVICE=s3
OPENDAL_S3_BUCKET=my-bucket
OPENDAL_S3_REGION=us-east-1
OPENDAL_S3_ACCESS_KEY_ID=...
OPENDAL_S3_SECRET_ACCESS_KEY=...
```

## Layout

`<output_path>/<namespace?>/<stream>/<file>`

`output_path` is a key prefix, not a local filesystem path. Backslashes become `/`, leading and trailing
slashes are removed, and `.` or `..` segments are rejected. Namespace and stream names are escaped, so
they cannot escape the prefix.

`overwrite` writes the new objects first, then lists the stream prefix and deletes older `.parquet`
objects. A failed sync therefore keeps the previous data. `append` keeps older objects. `overwrite`
requires the service to support list and delete; `check` verifies write, read, list, and delete.

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
- No `_airbyte_emitted_at` or other `_airbyte_*` columns. Object metadata carries the stream, namespace,
  sync mode, destination name, and OpenDAL service.
- One object per stream per `STATE`. Use `flush_row_limit` to bound memory, but a chatty checkpoint can
  still produce many small objects.
- One Parquet object is buffered in memory per flush. `flush_row_limit` controls the size.
- `overwrite` needs list and delete support from the OpenDAL service.
- The `Apache.OpenDAL` native library requires glibc 2.39 or newer. The repository's
  `mcr.microsoft.com/dotnet/*:10.0` images are Ubuntu 24.04 and work. On older hosts, run the tests and
  the connector in Docker.

## Local run

From this directory, run the default `fs` service and write the sample record:

```bash
dotnet run -- write --command check --config sample_files/config.json
dotnet run -- write --command write \
  --config sample_files/config.json \
  --catalog sample_files/configured_catalog.json \
  < sample_files/input.jsonl
find /tmp/airbyte-local-parquet -name '*.parquet'
```

Run with a settings file:

```bash
dotnet run -- write --command write \
  --config sample_files/config-settings-file.json \
  --catalog sample_files/configured_catalog.json \
  < sample_files/input.jsonl
find /tmp/airbyte-opendal/out -name '*.parquet'
```

## Development

```bash
dotnet build
dotnet test tests/Destination.LocalParquet.Tests/Destination.LocalParquet.Tests.csproj
```

The OpenDAL `fs` tests are skipped automatically when the native library cannot load on the host. Run
the same tests in the Docker build to execute them:

```bash
docker build -f airbyte-integrations/connectors/destination-local-parquet/Dockerfile \
  -t destination-local-parquet .
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
