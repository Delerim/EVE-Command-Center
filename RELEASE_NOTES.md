# EVE Command Center v3.6.61

- Corrected station-fuel alignment to use the latest-expiring reference station's actual fuel-block total when refinery consumption rates match.
- Show both **fuel blocks to add** and the **resulting total**, aligned to five-block fuel increments rather than drifting with fractional ESI implied-rate estimates.
- Keep differing burn rates, missing ESI data, and stale snapshots explicitly labelled as estimates or unavailable.

## Station fuel alignment correction
- For a group of similarly consuming refineries, the fullest near-latest-expiring station establishes the stock target (for example, 10,290 fuel blocks rather than 10,285). A station with 2,350 blocks then shows **+7,940 blocks | 10,290 total**, while one with 2,445 shows **+7,845 blocks | 10,290 total**.
- Reference stations already at the latest expiry remain aligned. Those one five-block tick behind show a five-block top-up where expiry confirms the difference.
- Stations with materially different implied hourly consumption use their own time-based estimate, rounded up to a five-block step, rather than copying a potentially wrong fuel stock target.
- Fuel quantities are based on recent, consistent ESI fuel-bay snapshots and are still **estimates**, not authoritative service burn-rate telemetry. Verify in-game before moving fuel.

## Quality and compatibility
- Regression tests now cover matching stock totals, one fuel-tick differences, different burn rates, missing or stale inventory, and the specific 2,350 / 2,445 versus 10,290 scenario.
- No moon catalogue or private CSV values are included. This update preserves v3.6.60 ESI-first moon discovery, despawn auditing, the privacy-first local CSV overlay, Discord alerts, and existing operating data.
