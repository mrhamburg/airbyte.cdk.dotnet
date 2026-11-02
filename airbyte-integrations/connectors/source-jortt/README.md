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
| `projects` | `GET /v3/projects` | full refresh |
| `labels` | `GET /v3/labels` | full refresh |
| `loonjournaalposten` | `GET /v3/loonjournaalposten?year=` | full refresh (one request per year) |
| `organization` | `GET /v3/organizations/me` | full refresh (single record) |
| `report_balance` | `GET /v3/reports/balance?year=&month=` | full refresh (one record per month) |
| `report_profit_and_loss` | `GET /v3/reports/profit-and-loss?year=&month=` | full refresh (one record per month) |
| `report_summary_balance` | `GET /v3/reports/summaries/balance` | full refresh (single record) |
| `report_summary_btw` | `GET /v3/reports/summaries/btw` | full refresh |
| `report_summary_cash_and_bank` | `GET /v3/reports/summaries/cash_and_bank` | full refresh |
| `report_summary_invoices` | `GET /v3/reports/summaries/invoices` | full refresh |
| `report_summary_profit_and_loss` | `GET /v3/reports/summaries/profit_and_loss` | full refresh |
| `project_line_items` | `GET /v3/projects/{id}/line_items` | full refresh, substream of `projects` |
| `bank_transactions` | `GET /v3/bank-accounts/{id}/bank-transactions` | full refresh, substream of `bank_accounts` |
| `invoice_payments` | `GET /v3/payments/{id}` | full refresh, substream of `invoices` |
| `expense_payments` | `GET /v3/payments/{id}` | full refresh, substream of `expenses` |

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

### Reports and payroll

`report_balance` and `report_profit_and_loss` emit one record per calendar month, walking from `start_date`
(or January of the current year when it is not set) through the current month, passing `year` and `month` to the
API. `loonjournaalposten` walks years the same way. The five `report_summary_*` streams are snapshots for the
current period and emit a single record per run.

### Substreams

`project_line_items`, `bank_transactions`, `invoice_payments` and `expense_payments` only exist below a parent
object, so they use the CDK's parent/child support (`SubstreamDependency`). Each is read once per parent record
with the parent key in the request path.

A substream always needs its parent for context: when the parent is not selected in the catalog it is read
internally and its records are **not** emitted; when it is selected, its records are emitted as well and the
parent is still only read once. Selecting only `project_line_items` therefore syncs line items for every project
without also syncing the projects.

Not implemented: `/v3/projects/{id}/line_items/summary` and `/v3/projects/{id}/invoice` (also substreams, but
report-like aggregates), and `tradenames`, which only exists on the deprecated v1 path.

Several of these endpoints are documented on the developer site but missing from the published
`/swagger_doc`; the streams follow the documentation. `projects`, `labels` and `loonjournaalposten` are marked
"Native v1 endpoint, also callable as v3" in the docs.

## Configuration

| Property | Required | Description |
|---|---|---|
| `client_id` | yes | OAuth client ID |
| `client_secret` | yes | OAuth client secret |
| `organization_id` | no | Administration to read, when the credentials span several |
| `start_date` | no | Lower bound for `expenses` (both sync modes) and the first month/year walked by the reports and payroll streams. Unset reads the full expenses history and starts reports in January of the current year |
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

The project references `Airbyte.Cdk` from this repository rather than the published package, because the
substream API is not in a released version yet. It goes back to `<PackageReference Include="Airbyte.Cdk" />`
once 0.0.9 is on nuget.org.

`discover` makes no HTTP request — the token is fetched on the first API call — but `client_id` and
`client_secret` must still be present in the config, so placeholder values are enough.

## Integration tests

`integration_tests.yaml` declares a full-refresh scenario, an `expenses` full-refresh scenario and an incremental
`expenses` scenario. They are skipped until `JORTT_CLIENT_ID` and `JORTT_CLIENT_SECRET` exist:

```bash
dotnet run --project ../../../tests/Airbyte.Cdk.IntegrationTests -- \
  run --connector airbyte-integrations/connectors/source-jortt
```
