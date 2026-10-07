# EVE Command Center v3.6.34

- Added a daily WASTE counter to each detailed mining card, using residue units recorded in EVE gamelogs. Hover for the exact total; compact mining cards include the total in their value tooltip.
- Backfill today's residue from existing logs and preserve live totals across restarts, using the existing 04:00 local mining-day boundary.
- Keep residue separate from collected ore, market value, critical cycles and mining activity. No ore, volume or ISK value is guessed from residue lines.
- Kept card heights unchanged; latest critical-pull volume remains available in the critical-stat tooltip.
- Added regression checks for parsing, live events, backfill, per-pilot totals, restart persistence and day filtering.
