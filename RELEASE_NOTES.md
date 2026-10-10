# EVE Command Center v3.6.45

- Production tables now sort by clicking headings, including item names and numeric shortfalls. Unknown stock sorts after known quantities in either direction, and the current sort is reapplied when the visible table reloads.
- Added a subtle 1.5% hover lift to project, pilot and component cards. It uses rendering transforms so neighboring rows do not move and honors Windows animation preferences.
- Continued Phase C Market work with Open, No longer observed and All observations filters. An order absent from a successful complete open-order refresh retains its last price, quantities and observation time; disappearance never implies a fill, sale or cancellation.
- Reappearing orders return to the open view without duplicates. Failed, malformed and unauthorized refreshes preserve prior observations. Selected pilots and retained observations survive restart in the existing cache.
- Market tables reuse the polished Industry controls, named item icons, subtle state colors, sortable quantities and dates, and a selected-order explanation. Open-order totals exclude absent orders.
- This is an order-observation increment. Confirmed order history, wallet transactions, attribution and financial accounting remain pending and will use the planned shared ledger rather than treating these observations as financial evidence.
