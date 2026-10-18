# EVE Command Center v3.6.65

- Fixed Buyback Analytics index requests that could appear to freeze Command Center while ESI retrieves contract item details and type metadata.
- Replaced unbounded 15-contract indexing with five-contract batches, visible per-contract progress, a Cancel button, and cancellation time limits.
- Preserved already fetched item details across cancellation, suppressing expensive full-window redraws after each indexed contract.
- Added regression checks for bounded batch selection, already indexed contracts and timeout limits.

## Buyback reporting reliability
- Indexing is entirely optional and never accepts, modifies or creates an EVE contract. A batch is limited to five contract IDs with a 90-second maximum attempt per contract and four-minute overall limit. Because shared ESI rate limits can pause requests, these are cancellation budgets, not promises of successful completion.
- The status area shows the current contract number, completed count, failures and timeouts; Cancel stops work while retaining all contents already saved in the user's local contract cache.
- The dashboard and existing contract views continue to operate. The full account-ledger refresh is no longer triggered after each successful indexing result.
- Only records successfully retrieved from ESI contribute to ore/item volume. Failed or timed-out contracts remain unindexed and can be retried later.

# EVE Command Center v3.6.64

- New Buyback Analytics dashboard: accepted-contract ISK trends, previous-period comparisons, verified volume coverage, supplier rankings and item/ore mix visualisations.
- Weekly, monthly, yearly and custom date reporting use explicit UTC period boundaries, with searchable accepted-contract drilldown and CSV report export.
- On-demand indexing retrieves up to 15 eligible contract contents at a time using an authorised corporation reader and stores results locally; unindexed contents never inflate volume totals.
- Contracts reporting keeps existing outstanding checks, historical records, legacy buyback chart, EVE open actions and account ledger unchanged.

## Reporting dashboard
- Four KPI panels display accepted contract prices, accepted buyback counts, known contents volume and outstanding contract counts, with a previous-period comparison where data exists.
- ISK timeline, top supplier rankings and a ranked ore/item volume breakdown are based on the saved corporation ESI archive. Double-click an accepted contract to inspect its ESI contents.
- Period control supports ISO-style Monday weeks, months, years and custom inclusive date ranges. Export a dated CSV summary, accepted contracts and indexed item mix locally.
- Known-content coverage displays the number of indexed buybacks out of qualifying contracts. Empty ESI history is not assumed to mean zero actual historical activity.

## Safe data handling
- Reporting never accepts, cancels, edits or posts contracts and never pushes local contents or CSV files to GitHub.
- Contract price is an accepted contract amount, not a verified wallet transfer. Realised resale proceeds, margins and profitability are intentionally not fabricated.
- ESI items are loaded only on demand in small batches; unavailable old contract contents remain unindexed with clear coverage labels.
- UI and reporting regression tests cover accepted-only qualification, deduplication, UTC period boundaries, supplier totals, empty periods and volume evidence.

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
