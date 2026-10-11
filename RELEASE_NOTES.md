# EVE Command Center v3.6.53

- Added a Finances tab inside each production project, bringing assigned purchase spending and sales receipts into one view.
- Summary cards show spending, receipts and their cash difference. Every figure follows the selected pilot and period; all project pilots remain available regardless of who owns the project.
- Added a dated activity table with item icons, pilot names, assigned units and separate paid/received columns. Columns sort, resize and remember their layout.
- Select an activity and open its exact supporting purchase or sale allocation. Source prices, locations and notes remain visible without raw IDs.
- Figures read the existing ledger in one snapshot. Viewing or filtering finances does not create another ledger, make ESI requests, alter reservations or mark production complete.
- Cash difference is explicitly not profit. Fees, manufacturing costs and consumed-stock valuation are not yet included; missing linked history is shown separately from confirmed zero-priced trades.
- Validated with 678 regression checks covering decimal totals, split provenance, owner-scoped counts, pilot/date filters, empty evidence, overflow, restart and source navigation.
