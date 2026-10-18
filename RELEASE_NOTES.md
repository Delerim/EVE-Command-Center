# EVE Command Center v3.6.62

- **Station fuel alignment:** Fix false extra fuel-block top-ups when reference fuel bays also contain non-fuel items. Only actual fuel blocks count toward the chosen reference total.
- **Aligned status:** Replace the confusing `0 / aligned` label with a simple **Aligned** status displayed in green.
- **Safer totals:** Keep fuel-block types separate, retain the fullest latest-expiring reference target, and show the add quantity and final total without stock-count drift.

## Station fuel accuracy
- Reference fuel inventory may coexist with other StructureFuel items. Those unrelated items no longer invalidate the reference and force a drifting time-based estimate.
- For comparable five-block-per-hour moon drills with a 10,290-block reference total, adding 7,940 to a 2,350-block refinery produces 10,290 total; adding 7,845 to a 2,445-block refinery also produces 10,290.
- The display for stations already at the target expiry simply reads **Aligned** in green. No unnecessary zero or slash.
- Mixed fuel-block types, missing data and stale ESI fuel snapshots still cannot claim a reliable exact refuel quantity. Different burn rates are expressly labelled estimated rather than copied from another station.

## Reliability and regression coverage
- Added checks for non-fuel stock alongside valid fuel blocks in the reference station, mixed fuel types, correctly aligned status, and both known Pelkia top-up scenarios.
- All existing moon discovery, CSV privacy, despawn audit, Discord alerts and Combined Overview behavior remain unchanged.
