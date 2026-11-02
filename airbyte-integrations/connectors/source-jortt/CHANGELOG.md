# Changelog

## 0.0.3
Added the substreams that only exist below a parent object, using the CDK parent/child support:
`project_line_items` (per project), `bank_transactions` (per bank account), and `invoice_payments` /
`expense_payments` (per invoice and per expense).

## 0.0.2
Added the v3 streams that the published swagger omits but the developer documentation describes: `projects`,
`labels`, `loonjournaalposten` (payroll), `organization` (`/v3/organizations/me`), the monthly `report_balance`
and `report_profit_and_loss`, and the five `report_summary_*` snapshots. `payroll:read` is now requested.

## 0.0.1
Initial release. Reads organizations, customers, invoices, estimates, expenses, receipts, bank accounts and the
three ledger account lists through the Jortt v3 API. `expenses` supports incremental sync on `vat_date`.
