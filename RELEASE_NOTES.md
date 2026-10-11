# EVE Command Center v3.6.55

- Added project assignments for confirmed broker fees, sales taxes, manufacturing charges and industry job taxes in Market & Wallet > Wallet activity.
- Split a charge between projects in ISK, edit or remove saved assignments, and review their history. The editor shows the remaining amount and rejects assignments above the original debit.
- Project Finances now separates fees and charges from purchases and sales. Cash difference subtracts assigned charges; pilot and period filters apply consistently to every figure.
- Added a sortable charge-detail table with aligned ISK values, readable project names and remembered column widths. Original wallet entries remain unchanged.
- Transfers, escrow, trade payments and incoming credits are excluded from expense assignment to avoid double counting. Cash difference is still not profit; unassigned costs and stock valuation remain separate.
- Added a backed-up ledger migration and regression coverage for decimal splits, stale edits, source eligibility, audit rollback, restart, migration, project removal and UI feedback.

- Validated with 719 regression checks and native WPF render fixtures for the charge editor and project finances.
