# EVE Command Center v3.6.63

- **Fuel alignment:** Fix stations being matched against an unrelated, higher-consumption reference and receiving incorrect time-based top-up quantities.
- **Rate-grouped references:** Select the fullest latest-expiring station **within each comparable fuel-consumption group**, rather than selecting one global fuel reference for every station.
- **Correct Pelkia totals:** When the comparable reference holds 10,290 fuel blocks, Radolin at 2,350 needs **+7,940**, and Thorny Island at 2,445 needs **+7,845**, both reaching 10,290.
- **Clear status:** Stations already at the target expiry continue showing **Aligned** in green without a `0 /` prefix.

## Why the old calculation was wrong
- An outlier with a larger stock and a different inferred burn rate could be chosen as the only reference. Normal moon drills then fell back to fractional time-based estimates such as 10,315 or 10,295 blocks, although matching-rate reference stations were available.
- The alignment engine now matches each station against a reference in its own inferred burn-rate group. Reference quantities still come from fresh ESI fuel-bay snapshots; fuel type names do not prevent compatible drills from being compared.
- If no compatible reference has valid data, the fallback remains explicitly labelled as an *estimate*. This avoids presenting an unverified exact quantity.

## Regression coverage
- Add a failure reproduction where a higher-consumption station holds more fuel than the valid ~5-block/hour references but has the same expiry date. Verify that Pelkia still aligns at 10,290, not the unrelated reference total or a guessed rate.
- Preserve existing checks for five-block fuel quantities, mixed fuel types, missing/stale ESI data and correct **Aligned** statuses.
- No changes to moon discovery, private CSV content, despawn auditing, Discord settings or preview behavior.
