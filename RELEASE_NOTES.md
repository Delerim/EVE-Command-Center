# EVE Command Center v3.6.50

- Continued Phase D with purchase attribution. In Market > Purchases, select a row to open Why did I buy this? Split quantities between matching projects, general stock and unrelated activity, with optional notes.
- Edit or remove saved splits without losing their history. Project suggestions require an explicit choice. Allocations cannot exceed the confirmed purchase quantity, and stale editors cannot overwrite another saved change.
- Purchase tables show allocated/unallocated quantities and Bought for, with filters for purchases needing allocation and search by project name. The resizable allocation pane and market table widths are remembered.
- Added a Purchases tab inside each production project, showing the buyer, item, attributed quantity, confirmed unit price, acquisition cost, purchase station and note. Quantities and money remain separate from physical stock reservations and delivery readiness.
- Purchase allocations and their before/after audit records commit atomically in the existing ledger. Schema upgrade makes a pre-upgrade backup. Deleted projects retain historical purchase explanations; original transactions are never removed by allocation changes.
- Validated with 619 checks covering split costs, limits, stale edits, reversal, write-failure rollback, removed projects, restart, populated-ledger migration, UI actions and remembered widths.
- Acquisition costs shown here exclude fees, manufacturing charges and existing stock valuation. Sale/batch attribution and project profit calculations remain the next financial steps.
