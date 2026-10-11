# EVE Command Center v3.6.52

- Market is now Market & Wallet. Clicking a pilot portrait opens only that pilot's orders, trades and wallet; the selection is remembered.
- Moved wallet balance, journal, cash flow and PLEX history out of Pilots into Market & Wallet. Pilot refreshes no longer fetch wallet data independently. Views reuse the shared imports and respect refresh caching.
- Added Wallet overview with balance and today/seven-day cash flow, plus a dedicated PLEX view. Journal entries include balance after the entry and their description or reason. Failed refreshes retain saved data and explain the problem.
- Continued Phase D with explicit sales attribution. Split confirmed sales between eligible projects, general stock or unrelated activity, with quantity limits, previews and editable history. Project attribution requires confirmation that the sold units came from that project.
- Projects now have a Sales tab showing attributed units and gross receipts. These receipts do not imply completed production, delivery or profit; costs and fees remain separate.
- Ledger schema 5 creates a backup before adding sale allocations and their audit trail. Existing purchases, projects and imported trades are retained.
- Validated with 663 regression checks, including single-pilot isolation, wallet cache reuse and failure recovery, sale quantity limits, stale edits, transactional rollback, restart and populated-ledger migration.
