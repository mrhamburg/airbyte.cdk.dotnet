# Source Jortt

Airbyte source for [Jortt](https://www.jortt.nl), a Dutch accounting package. Reads the bookkeeping objects of
one administration through the [v3 REST API](https://developer.jortt.nl/).

## Authentication

The source uses the OAuth 2.0 **client credentials** grant, which requires a Jortt application registered under
**Settings > API** in your administration. The application must grant the read scopes the source requests:

```
customers:read invoices:read estimates:read expenses:read organizations:read reconciling:read
```

Use `client_id` and `client_secret` from the registration. When the credentials may act on more than one
administration the API answers `organization.header_required`; set `organization_id` (list the available ones with
`GET /v3/organizations`).

The Jortt API requires an administration on the MKB or Plus plan. Without one, every request fails with
`organization.requires_mkb_plan`.

## Streams

| Stream | Endpoint | Sync modes |
|---|---|---|
| `organizations` | `GET /v3/organizations` | full refresh |
| `customers` | `GET /v3/customers` | full refresh |
| `invoices` | `GET /v3/invoices` | full refresh |
| `estimates` | `GET /v3/estimates` | full refresh |
| `expenses` | `GET /v3/expenses` | full refresh, incremental (`vat_date` / `vat_date_from`) |
| `receipts` | `GET /v3/receipts?scope=all` | full refresh |
| `bank_accounts` | `GET /v3/bank-accounts` | full refresh |
| `ledger_accounts_cost` | `GET /v3/ledger_accounts/expenses/cost` | full refresh |
| `ledger_accounts_income` | `GET /v3/ledger_accounts/expenses/income` | full refresh |
| `ledger_accounts_balance` | `GET /v3/ledger_accounts/expenses/balance` | full refresh |

Only `expenses` has a server-side date filter, which is why it is the only incremental stream: the cursor is
`vat_date` and each slice requests `vat_date_from=<cursor>`. Everything else is full refresh.

Not implemented: reports (aggregates rather than records), `payments/{sink_id}` and `bank-accounts/{id}/bank-transactions`
(both require a parent object, which needs a fan-out the CDK does not provide), and labels, tradenames and payroll,
which only exist on the deprecated v1 paths.

## Configuration

| Property | Required | Description |
|---|---|---|
| `client_id` | yes | OAuth client ID |
| `client_secret` | yes | OAuth client secret |
| `organization_id` | no | Administration to read, when the credentials span several |
| `start_date` | no | Initial `vat_date_from` for `expenses`, defaults to `2020-01-01` |
| `scopes` | no | Override the requested scopes |

## Local development

```bash
dotnet run -- read --command spec
dotnet run -- read --command discover --config sample_files/config.json
dotnet run -- read --command check --config sample_files/config.json
dotnet run -- read --command read --config sample_files/config.json \
  --catalog sample_files/configured_catalog.json --state sample_files/state.json
```

`discover` needs no credentials: the OAuth token is requested on the first HTTP call, not when streams are built.

## Integration tests

`integration_tests.yaml` declares two scenarios. They are skipped until the `JORTT_CLIENT_ID`,
`JORTT_CLIENT_SECRET` and (optionally) `JORTT_ORGANIZATION_ID` secrets exist:

```bash
dotnet run --project ../../../tests/Airbyte.Cdk.IntegrationTests -- \
  run --connector airbyte-integrations/connectors/source-jortt
```
