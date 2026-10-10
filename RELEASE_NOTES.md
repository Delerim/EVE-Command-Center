# EVE Command Center v3.6.46

- Aligned quantity headings and values consistently in materials, inputs, stock, reservations and market tables. Available, missing, required and reserved quantities now share the same right edge within each column; sorting and saved widths remain available.
- Continued Phase C with a Confirmed history view in Market. Choose pilots and use Refresh confirmed history to import ESI-confirmed cancelled and expired personal orders. Imports respect the hourly history cache and retain records beyond the API's 90-day retrieval window.
- History imports commit only after every page succeeds. Failed pages, unsupported states and missing permissions preserve previous records. Repeated refreshes do not duplicate orders; corporation orders are excluded.
- The first history refresh migrates existing project planning into the shared transactional industry ledger, retaining stable project identities and an exact JSON backup. Projects continue saving in that ledger alongside history. Changes made to the old planning file by an older app are detected rather than silently discarded.
- Confirmed history takes precedence over stale open-order observations. Missing orders without history remain unresolved; disappearance never implies a fill. Wallet transactions, project attribution and financial accounting are still pending.
