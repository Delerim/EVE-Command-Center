# EVE Command Center v3.6.40

- Production Queue now keeps the selected blueprint and component tree in a persistent left pane, with a draggable divider and all editors/library/materials/settings on the right. Selecting a component opens its editor and highlights the selected row.
- Added explicit local manufacturing job plans for calculated Make components: choose the executing toon and optional facility ID, then create/update the assignment. Stable plan IDs, assignments and audit history survive restart; no job is submitted in EVE.
- Hide manual child-input controls for calculated trees and prevent conflicting sourcing changes while a local job plan is assigned.
- Started Phase C with an embedded MARKET navigation entry, separate open buy/sell views, item search, per-character selection, remaining quantities/values, order IDs, locations, expiry and snapshot freshness.
- Market selection and last successful personal order snapshots persist. Refresh uses existing SSO and shared ESI handling, respects the normal 20-minute order cache, and retains old observations on errors or missing scopes. Reconnect selected market toons in Settings if market-order permission is missing.
- Open-order values are not actual spending or profit. Corporation orders are excluded; order history, transactions, financial attribution, actual industry-job matching and transfer reconciliation remain future increments.
- Added regression and rendered UI checks for assignment persistence, component selection, market cache handling, permissions, buy/sell filtering and restart recovery.
