# EVE Command Center v3.6.47

- Fixed stale access-token reuse after adding permissions: a cached token must cover the reconnected pilot's granted scopes before it can be reused. Market errors now explain denied access, expired sessions, network failures and rate limits instead of showing exception names.
- Fixed confirmed history rejecting valid zero-day immediate orders. Tested the updated importer against live ESI responses.
- Continued Phase C with Market transactions. Use Refresh transactions to load confirmed personal purchases and sales, with named item icons, station names, search, pilot selection and sortable quantities, prices and gross totals.
- Transaction history persists in the shared industry ledger. Cursor pagination handles repeated boundary records without duplicate spending, and full imports commit with their refresh checkpoint. Failed pages, malformed responses and conflicting evidence preserve existing records.
- Added an atomic ledger schema upgrade with a pre-upgrade database backup. Existing projects and order history remain intact. Hiding a pilot does not remove imported transactions.
- Open orders, history and transactions honor their own API cache windows, including absolute expiry on shared cached responses. Gross purchases and sales are not profit: wallet journal fees, project allocations and financial accounting remain pending.
