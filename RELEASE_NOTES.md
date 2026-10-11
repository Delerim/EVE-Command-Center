# EVE Command Center v3.6.51

- Polished the purchase allocation inspector with item cards, assigned/remaining quantities, progress, a clear destination prompt and an exact cost preview. Assignment actions stay visible at the bottom of the pane on shorter windows.
- Project choices include icons and descriptions. When no active project contains the purchased item, the inspector explains why and offers general stock or unrelated activity. It never assigns a project automatically.
- Added explicit empty states for saved allocations and history, with clearer editing actions. Invalid or excessive quantities disable Save; the existing transactional checks still enforce allocation limits.
- Continued Phase D with per-project acquisition cost analysis. Project > Purchases > By item shows assigned quantity, total paid, quantity-weighted average unit price, lowest/highest purchase price and last purchase date.
- Select an item to inspect its supporting purchases. Numeric columns are aligned, sortable, resizable and remembered. Multiple splits of the same source purchase do not inflate the purchase count.
- Costs use confirmed purchase prices and saved allocations only. They do not value existing stock, add fees, imply delivery or claim project profit. No ledger migration or additional ESI permissions are required.
- Validated with 632 regression checks, including weighted prices, decimal precision, owner-scoped purchase counts, split aggregation, overflow protection, destination prompts, empty states, cost previews and purchase drill-down.
