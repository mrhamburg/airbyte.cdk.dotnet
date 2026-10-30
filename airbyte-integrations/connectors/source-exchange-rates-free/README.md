# Exchange Rates Free Source (Frankfurter)

This connector reads currency reference data and exchange rates from the free
[Frankfurter API](https://www.frankfurter.app/docs).

Streams:

- `symbols` — supported currency codes and names (full refresh).
- `latest` — latest rates for the configured base currency (full refresh).
- `historical` — daily rates from `start_date` (incremental on `date`).

## Local development

Build the connector from this directory:

```bash
dotnet build
```

Run the connector against the sample files from this directory (the entrypoint
needs the built implementation assembly and connector class name):

```bash
export AIRBYTE_IMPL_PATH="$PWD/bin/Debug/net10.0/source_exchange_rates.dll"
export AIRBYTE_IMPL_MODULE="ExchangeRates"

dotnet run --no-build -- read --command spec
dotnet run --no-build -- read --command check --config sample_files/config.json
dotnet run --no-build -- read --command discover --config sample_files/config.json
dotnet run --no-build -- read --command read --config sample_files/config.json \
  --catalog sample_files/configured_catalog.json --state sample_files/state.json
```
