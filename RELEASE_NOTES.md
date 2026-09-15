# EVE Command Center v3.5.20

- Added PvE and PvP cards to the Character Overview mode menu, retaining the existing card dimensions and client switching. PvE shows logged DPS, bounty totals and last observed weapon; PvP shows incoming/outgoing damage and remote repairs, peak incoming hits and recent scramble observations.
- Combat DPS uses a rolling 30-second window. Delayed old logs cannot create fresh damage spikes. Recent tackle observations expire visually without claiming the effect ended. Ammo quantities, loaded scripts and complete active debuffs are not exposed as live data. Display modes do not change alert settings.
- Fixed preview show/hide and focus issues: text/stat overlays become non-activating before first display; hidden previews no longer briefly show during creation or z-order maintenance. Disposing a preview releases its text overlay and DWM registration.
- Fixed log-reader lifecycle tracking so stop/restart waits for the actual reader. Process-stat polls cannot overlap when GPU counter queries are slow; CPU readings are clamped to valid percentages.
- Removed NPC damage from bounty-rate calculations. Combat-stat pruning no longer discards concurrent additions, and pilot stat keys are case-insensitive.
- PI countdown updates preserve read notifications. Superseded industry scans are cancelled and use a stable material/skill snapshot. Background pilot caches now use atomic replacement.
- See docs/code-audit-3.5.20.md for review coverage, findings and remaining validation limits.
