# EVE Command Center v3.6.70

- Keep Google Drive backup actions accessible at normal window sizes and Windows display scaling. The enlarged encrypted-backup setup panel can scroll independently without hiding **Back Up Now**, **Restore & Restart**, **Refresh Status**, **Export Encrypted Copy**, or **Restore File**.
- Put restore-point selection and backup actions into a persistent bottom panel, with buttons that wrap at narrower widths. Backup operation status stays visible below the controls.
- Align the **☁ Backup** icon and label with the other App Settings sidebar entries by removing surplus spacing.
- Preserve the v3.6.69 encrypted backup format, EVE credentials, optional private moon CSV handling, restore-point retention, Google OAuth configuration, and existing cloud files without changing any backup or restore logic.
- Add regression checks for the scrolling setup, persistent restore selector, action buttons, status footer and sidebar icon alignment.

# EVE Command Center v3.6.69

- Add the missing cloud icon beside Backup in App Settings, matching the existing sidebar icon spacing.
- Introduce encrypted portable backups with recovery-password setup. Entire archives use AES-256-GCM and PBKDF2-SHA256, while locally remembered recovery passwords are protected with Windows DPAPI and never uploaded. Configure a recovery password before starting the new automatic cloud backup.
- Include EVE SSO refresh tokens from Windows Credential Manager in the encrypted archive so linked pilots can be recovered on a replacement PC (subject to token validity and revocation). Never include the Google OAuth refresh token, OAuth JSON, or cloud recovery password.
- Expand snapshot coverage to JSON/JSONL, moon records, historical reports, logs, CSV, SQLite database snapshots and supported local data. Explicitly choose any private moon CSV stored outside Command Center's folders.
- Keep the existing v1 ZIP untouched for backwards compatibility; upload encrypted v2 snapshots under different names instead.
- Retain the latest encrypted snapshot, 7 daily, 4 weekly and 12 monthly restore points in Google's hidden Drive appDataFolder, pruning only old v2 checkpoints.
- Add a restore-point picker, encrypted local export and local encrypted-file restore option to the Google Drive Backup window.
- Add encryption integrity and incorrect-password regression checks. A wrong recovery password cannot modify local data.

**Important:** EVE SSO refresh tokens can be rotated or revoked; an older snapshot cannot guarantee an active login. Recovery passwords must be retained externally because Google sign-in alone cannot decrypt the portable archive. Backup contents are not visible in My Drive because the Drive API stores them in the private appDataFolder.

# EVE Command Center v3.6.68

- Fix Google Drive cloud-backup OAuth redirect failures after consent: the local loopback listener now remains open until the browser receives the authorization result page. This resolves the observed `127.0.0.1 refused to connect` and disposed `ThreadPoolBoundHandle` callback errors.
- Separate the local OAuth callback from token exchange for a direct, offline regression test using a synthetic authorization code and state. No real Google access tokens or credentials are used in tests or included in the release.
- Preserve PKCE, one-time state verification, the four-minute sign-in timeout and cancellation, token exchange, Windows Credential Manager storage, existing automatic backup settings, and backup/restore safeguards.
- Show a clear timeout message if the Google consent flow is abandoned rather than leaving the user waiting on the Backup window.
- Keep the release process build-first and allow `--publish` to resume safely if a network outage occurs after the release commit but before pushing the branch or tag.

# EVE Command Center v3.6.67

- Restore a clearly labelled **BACKUP** section in App Settings, beside Alerts, making the existing Google Drive backup and recovery tool easy to find.
- One click opens the original Google Drive Backup window for browser-based Google sign-in, connection status, manual backup, automatic backup interval, and restore/restart.
- Preserve the established Google OAuth and Windows Credential Manager integration. No additional authentication flow, token exports, or duplicate backup storage is introduced.
- Clarify that the toolbar **Backup** command handles local layout/profile configuration, not the Google Drive backup.
- All backup and restore operations remain opt-in and handled by the existing CloudBackupService. Restoration still shows its safety and restart prompts.

# EVE Command Center v3.6.66

- New Moon Operations Intelligence dashboard: recorded weekly/monthly fractures, observed vs inferred fields, structure/unscheduled drill counts, estimated remaining R4 ore by type, despawn loss estimates and fleet fuel health.
- Compact Discord operations reports with a manual preview and send action, plus optional daily (yesterday), Monday weekly (previous week) and monthly (previous month) schedules while the application is running.
- Every alert and each report frequency can be routed independently to multiple encrypted Discord webhooks. Existing destinations retain their previous alert subscriptions and never opt in to new reports automatically.
- Recorded and estimated values are distinguished, stale ESI prevents report sending, unknown fuel/ore coverage is disclosed, and completed scheduled deliveries are deduplicated across restarts per destination and reporting period.

## Moon Operations Intelligence
- Open Moon Operations > Operations Report for structure counts, confirmed fractures, active/inferred fields, drills without an extraction, fuel average/median, low-fuel counts, and estimated ore remaining/lost for each R4 family.
- Fracture chart uses recorded fracture timestamps in UTC. Historical estimates use only ESI-visible or opted-in local moon mappings; missing private ore profiles are not interpreted as zero loss.
- Discord preview uses the same summary logic as the dashboard, without sending hundreds of per-moon events or any webhook secret. Manual sends require user confirmation and an enabled manual-report destination.

## Discord routing and schedules
- Configure as many destinations/channels as you want under App Settings > Alerts > Discord webhooks. Choose manual, daily, weekly or monthly Moon Operations reports independently per destination alongside existing alert rules.
- Scheduling is disabled by default. After enabling a daily/weekly/monthly cadence and choosing a UTC hour, reports run only when Command Center is open, the corporation ESI snapshot is fresh, and matching subscribed destinations exist.
- Delivery uses existing opt-in encrypted webhook service and per-destination successful-delivery ledger. A different destination may receive a different combination of alerts and reports.

## Data fidelity and safety
- Daily reports cover yesterday; Monday weekly reports cover the preceding Monday-Sunday; monthly reports cover the previous calendar month. No automatic report is claimed to have been delivered before Discord confirms it.
- Profiles and moon names are not embedded in any release assets. Local CSV and protected webhook URLs stay outside Git, and existing per-type alerts/notifications remain unchanged.

---


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
