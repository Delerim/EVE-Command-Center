# EVE Command Center v3.6.54

- Added Command Center Setup > Sync & dashboard, with saved per-pilot switches for automatic wallet, market, industry, PI, pilot-info and clone sync. Wallet defaults to linked pilots; market selection is independent.
- Added per-pilot financial sync status, permission guidance and saved timestamps. Turning sync off retains history. Existing endpoint caches and request scheduling are reused.
- Market & Wallet pilot selection is now independent of background refresh targets. Background cache updates preserve a pilot switch and do not interrupt an open allocation editor.
- Started Phase E dashboard integration: project counts and names, saved buy/sell commitments, wallet balances, daily trades and 7/30-day journal cash flow. Incomplete or stale data is labelled; cash flow is not profit.
- Added independent dashboard visibility for market, wallet/trades and industry jobs. Existing moon/contract reader setup and local mining monitoring remain separate.
- Added a phase-by-phase testing guide in docs/INDUSTRY-MARKET-TESTING.md with delivered behavior and remaining full-brief work.
- Validated with 695 regression checks, including independent feed routing, cache reuse, restart, disabled sync, corrupt settings, pilot switches during refresh, dashboard coverage and cash-flow deduplication.
