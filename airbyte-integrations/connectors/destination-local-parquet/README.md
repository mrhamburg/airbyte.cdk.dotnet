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
