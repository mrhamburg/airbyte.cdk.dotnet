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
administration the API answers `organization.header_required`; set `organization_id` to one of the IDs returned by
`GET /v3/organizations`. `check` fails with the list of available IDs when the header would be required and no
`organization_id` was given, so a green check never precedes a failing read.

The Jortt API requires an administration on the MKB or Plus plan. Without one, every request fails with
`organization.requires_mkb_plan`.

Jortt documents no specific scope for receipts, bank accounts or ledger accounts; if the registered application
does not implicitly cover them, those three groups return `403` while check still succeeds. Confirm on the first
credentialed run.

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
`vat_date` and each request sends `vat_date_from`.

### Incremental limitations

Jortt exposes no modification timestamp, so the cursor is the business date of the expense, not the moment it was
changed. Two consequences:

- **Late-booked expenses.** A bookkeeper can enter an expense today with last month's VAT date. `lookback_days`
  (default `7`) widens every incremental request, so those entries are still picked up.
- **Edits to old expenses.** Changing an expense that is older than the lookback window does not move its VAT date,
  so the edit is never seen. Run a periodic **full refresh** (for example weekly) to catch them. A full refresh
  sends no date filter and reads everything.

The cursor is the highest VAT date the stream has seen, tracked independently of the API's response order, because
Jortt does not document a sort order. That also makes the terminal state safe to resume from.

`start_date` is optional. When set it is a lower bound for `expenses` in **both** sync modes, so a full refresh with
`start_date` set does not read earlier history; leave it empty to read everything.

The state file drives the incremental filter, so do not pass a `--state` file when running a manual full refresh —
Airbyte itself never sends state for a full-refresh stream.

Not implemented: reports (aggregates rather than records), `payments/{sink_id}` and
`bank-accounts/{id}/bank-transactions` (both require a parent object, which needs a fan-out the CDK does not
provide), and labels, tradenames and payroll, which only exist on the deprecated v1 paths.

## Configuration

| Property | Required | Description |
|---|---|---|
| `client_id` | yes | OAuth client ID |
| `client_secret` | yes | OAuth client secret |
| `organization_id` | no | Administration to read, when the credentials span several |
| `start_date` | no | Lower bound for `expenses`, in both sync modes. No default: unset reads the full history |
| `lookback_days` | no | Days to reach back behind the incremental cursor, default `7` |
| `scopes` | no | Override the requested scopes |

## Local development

```bash
dotnet run -- read --command spec
dotnet run -- read --command discover --config sample_files/config.json
dotnet run -- read --command check --config sample_files/config.json
dotnet run -- read --command read --config sample_files/config.json \
  --catalog sample_files/configured_catalog.json --state sample_files/state.json

dotnet test source_jortt.sln   # unit tests, no network
```

`discover` makes no HTTP request — the token is fetched on the first API call — but `client_id` and
`client_secret` must still be present in the config, so placeholder values are enough.

## Integration tests

`integration_tests.yaml` declares a full-refresh scenario, an `expenses` full-refresh scenario and an incremental
`expenses` scenario. They are skipped until `JORTT_CLIENT_ID` and `JORTT_CLIENT_SECRET` exist:

```bash
dotnet run --project ../../../tests/Airbyte.Cdk.IntegrationTests -- \
  run --connector airbyte-integrations/connectors/source-jortt
```
