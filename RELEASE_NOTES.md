# EVE Command Center v3.6.81

- **Orca mining hold added.** Orca cards now show their log-driven mining-hold estimate alongside the separately labelled cached fleet hangar. Capacity uses hull dogma and the pilot's Industrial Command Ships level. Personal cargo stays hidden.
- **Aligned mining tiles.** Shared row sizes keep the Orca and exhumer cards equal in height, align hold bars beneath the mining-stream area, and keep status/alarm controls together at the bottom. Existing compact and preview modes remain available.
- **Earlier capacity display.** Ship mining capacity is sent to the overview as soon as hull and skill data are ready, without waiting for the remaining fitting, implants and EHP calculations. ESI delays and unresolved volume data can still delay confirmed values.
- Mining amounts remain estimates: unlogged transfers and unloading need correction. Right-click a mining hold to mark it empty. The Orca fleet hangar remains cached inventory, not inferred live transfers.

# EVE Command Center v3.6.80

- **Independent mining-stream rings.** Compact teal rings learn separate pull streams from log timing and yield. A stopped stream turns red independently; ambiguous or insufficient observations stay grey. The rings are inferred streams, not physical module-slot identifiers. Critical pulls do not inflate the learned normal yield.
- **Live mining-hold estimates for Hulk, Skiff and Mackinaw.** Mining adds volume, compression reduces it and a full-hold notification anchors the estimate. Capacity comes from the detected hull's dogma and pilot skills, including Mackinaw's Mining Barge and Exhumers bonuses. Personal cargo is removed. Ship changes start a new estimate.
- **Hold directly beneath the rings; status and alarms at the bottom.** Horizontal and compact vertical layouts retain Command Center styling. Hover details explain confidence and freshness without adding visible switches. Right-click the mining hold to mark it empty after unloading.
- **Orca fleet hangar.** Shows separately labelled cached inventory with freshness and missing-data handling. It does not pretend unlogged fleet transfers are live measurements. Orca drone mining does not invent strip-miner rings.
- Estimates start without known opening contents and cannot detect every transfer or unload. Missing item volumes/capacity remain explicit. The new log parser recognises English compression/full notifications. Existing ship-level sound/watchdog behaviour is unchanged; independent rings are visual indicators. No live-client end-to-end validation is claimed.

# EVE Command Center v3.6.79

- **Permanent history for linked manufacturing batches.** Successful Industry job responses retain observed status and end-time changes in the existing workspace. Initial links are recorded, and repeated unchanged responses do not duplicate history or rewrite the workspace. No additional ESI requests.
- **Job history inside each project.** Review component icons/names, readable batch labels, pilots, output, runs, observed/end times in UTC and facilities. Columns sort and remember their widths/order. Evidence remains stored after unlinking or deleting a project.
- **Collected jobs stay traceable.** Confirmed delivered/cancelled/reverted outcomes survive EVE's retention window. Missing nonterminal jobs remain Unknown. Job collection never confirms a project receipt, material consumption or profit.
- New observations must match the linked batch identity and be newer and fresh. Terminal outcomes cannot regress, and a ready job cannot revert to manufacturing. Persistence failures are reported in Industry status without aborting the rest of its refresh.
- Job history upgrades the workspace payload to schema 4; existing schemas remain readable. Earlier versions refuse newer payloads rather than discard history. Material consumption, cost basis and realized-profit accounting remain subsequent increments.

# EVE Command Center v3.6.78

- **Multiple manufacturing batches per component.** Link several EVE jobs without replacing earlier associations. Each job remains exclusive to one component across all projects; relinking the same batch does not duplicate output.
- **Inspect and unlink individual batches.** Assign & track distinguishes linked batches from candidates, shows expected output units alongside runs, and retains saved links when they disappear from current snapshots. Unlink selected batch leaves the others intact.
- **Combined production quantities.** Production & delivery totals all linked batches by stage, including partial runs and surplus. A missing or stale batch keeps totals Unknown rather than claiming complete coverage. Manufacturing, collection, stock reservations and confirmed receipts remain separate.
- The dashboard counts linked jobs across every batch. Existing builder and selected-blueprint checks remain in force; this does not yet split one component between multiple builders or different selected blueprint instances.
- Adding another batch upgrades the workspace to schema 3 in the existing storage. Earlier single-job plans remain readable, while older app versions refuse the newer payload rather than discard batch links. Delivery confirmations preserve this version. Consumption, automatic historical job-transition capture and realized profit remain future work.

# EVE Command Center v3.6.77

- **Moon drill refresh no longer shows false unscheduled drills.** Apply the complete extraction list before asynchronous moon-name lookups. Previously the drill timer could redraw midway through refresh, showing 64 running and 63 not set even though all 127 schedules were present. Genuine missing schedules remain distinguishable; no schedules are invented.
- **Production & delivery in each project.** See required, reserved, in-production, awaiting-collection, delivered and still-to-deliver quantities separately. Product icons/names, aligned sortable quantities, restrained receipt colours and remembered column widths/order use the existing workspace style.
- **Confirm partial deliveries and handoffs.** In a component's Stock & transfers tab, select a reservation and observed destination stock, enter the quantity and handoff note, then confirm delivery. Moving between pilots or locations moves the reservation atomically. Confirmed stock stays held, so another project cannot claim it.
- **Correct receipts without losing stock allocations.** Undo a delivery confirmation while retaining its reservation. Delivered reservations require this explicit correction before release. Archived projects can retain fully delivered stock; outstanding reservations must be resolved first.
- **Evidence-based progress.** Project cards reflect gathering, manufacturing, awaiting collection, partial receipts or delivered output. A linked manufacturing job uses its actual runs and recipe output. Stale or absent job observations remain Unknown; elapsed timers and EVE collection never auto-confirm a project receipt.
- Workspace delivery evidence is versioned and retained in the existing JSON/SQLite workspace and audit history. No additional ESI queries or separate ledger. One linked EVE job per component remains the current limit; multi-batch reconciliation, consumption and realized-profit accounting remain future work.

# EVE Command Center v3.6.76

- **Production materials on the main dashboard.** See active-project demand, shared available stock, reserved quantities and missing stock together, with item names/icons and understated coverage colours. Paused projects keep their reservations but do not add demand; archived projects do not add demand.
- **Open the materials workspace directly.** The dashboard action opens Industry > Production Queue > All project materials, including when the embedded workspace has not finished loading. An empty workspace opens the project gallery.
- Sort by material, quantities, status or projects. Numeric headings and values share alignment, and column widths/order are remembered independently of production plans.
- **Unknown inventory is not a shortage.** Dashboard and consolidated materials now use the same conservative rule: incomplete inventories cannot prove a deficit. Known verified stock may still prove coverage. Stale, unauthorized or moved reservation sources require review. Coverage does not claim delivery, skills or manufacturing readiness.
- Reuse existing saved assets, project state and reservations without new ESI requests or a second ledger. Dashboard materials refresh at most every 30 seconds unless the saved project revision changes; a workspace failure clears previous quantities.
- This is another Phase E increment. Confirmed job/delivery/consumption reconciliation, full batch costing and realized profit remain future work. Backup behaviour from v3.6.75 is unchanged.

# EVE Command Center v3.6.75

- **Full backups from the existing Back up now button.** Includes saved Command Center history, consistent SQLite snapshots, linked EVE logins and external Documents/EVE/logs. Encryption and Google sign-in are unchanged. No new test button.
- **Choose your own backup schedule.** Enable scheduling and enter the interval in minutes (5 to 43,200). New installations default to manual backups; existing saved settings are retained. Scheduled backups run while the app is open; separate backup-on-exit and daily/weekly/monthly tiers are removed.
- **Keep two cloud copies.** Create a new encrypted snapshot, validate its contents and read it back from Google Drive before deleting older restore points. Failed upload or verification preserves earlier copies and does not record success. A retention failure reports that extra copies remain.
- **Validate before recovery writes.** Check checksums, SQLite integrity, supported destinations and encrypted EVE login consistency before restoring a v2 archive. Restore captured historical data and logs; missing observations are not invented. Pilots without a saved login are reported as requiring reconnection.
- Backups and recovery share an operation lock. Failed archive creation cleans up its temporary ZIP. Restore-point selection defaults to the newest copy.
- Normal Windows Release builds and synthetic regressions validate this patch. The earlier native .NET compiler crash was not reproduced, so no unsupported root-cause claim or analyzer-disable workaround is made. See docs/backup-v3.6.75.md for scope, validation and limitations.

# EVE Command Center v3.6.74

- **Fix Combined preview bleed when Settings or Backup gets focus.** The saved Combined/Separate preference now reasserts its native-preview visibility policy when the embedded Settings workspace activates or deactivates; no automatic preference change.
- Keep the standalone WinForms preview body and its separate WPF text overlay synchronized. A hidden native preview can no longer leave an orphaned pilot label, process text or FPS window visible after Settings interactions or on z-order repair.
- Reapply the Combined-mode visibility gate after Settings updates thumbnail sizing/styles and per-character stat windows. The focus tracker also checks both HWND layers before early exits.
- Hidden previews do not recreate their independent text overlays when overlay z-order is corrected. Separate mode still restores the preview/stat windows when deliberately selected; independent PiPs and user visibility preferences are not changed.
- Add synthetic native/WPF preview regression checks for Separate → Combined, orphan text overlay reproduction, Settings opening, Settings reapplication, Backup focus transition and mode restoration. No EVE clients, user credentials or private moon data are used in these tests.
- Scope is limited to preview lifecycle, Settings activation, regression coverage and release notes. Google Drive backup, permanent mining history, contract journals and encrypted restore format are unchanged.


- **Preserve mining history beyond one year.** Remove the destructive 365-day trimming of archived per-character/per-ore totals. Keep historical daily `MiningData/*.jsonl` event journals instead of deleting completed days after indexing. Backups already include these files. Existing aggregates are never replaced when a log scan returns no observations for their day.
- Continue the bounded one-year **initial** EVE game-log backfill to avoid exhausting RAM on installations with gigabytes of logs. Earlier dates already stored in the Command Center archive remain safe; a future streaming all-time import is required to recover older game logs not yet indexed. Mining totals remain explicitly observed, not inferred when monitoring was offline.
- **Preserve moon history.** Stop the 365-day pruning of extraction pulls, ESI mining ledger rows and daily mined volume/totals, including when a refresh runs. Remove the one-year filters from the moon calendar and ledger views. Historical observations can now remain for years, subject to storage capacity; snapshots are not proof of periods when ESI was unavailable.
- **Record contract status transitions.** Add an append-only `Contracts/contract-status-history.jsonl` recording known baseline statuses on first upgrade and new/changed contract statuses at subsequent successful ESI polls. This records observed transitions that today's `contracts.json` (latest per contract) otherwise overwrites. Duplicate unchanged polls are ignored. Does not synthesize history for unseen contracts.
- **Preserve daily PI colony layouts.** Add an append-only `PiHistory/YYYY-MM-DD.jsonl`, capturing the first real ESI colony layout observed each UTC day for each pilot/planet. Unlike the existing mutable PI state, prior days are retained. These are snapshots, not verified daily production or hauling records.
- Both new journal types are inside the existing `%LOCALAPPDATA%/EVE Command Center` backup scope and use the `.jsonl` extension already supported by encrypted v2 backups. No EVE or Google OAuth secrets are written to these journals; private moon CSVs remain unbundled.
- **Improve recovery audit dates.** Inspect timestamped JSON/JSONL sources up to 64 MiB (previously 2 MiB), recognize the new PI history category, and filter scheduled/expiry and future dates so 2027 expiries do not masquerade as recorded 2026 activity. The audit is still partial and does not equate file size to full annual coverage.
- Add synthetic regression checks for contract baseline and transition journaling, unchanged refreshes, multi-year daily PI snapshots, category recognition and safeguards against reintroducing one-year mining/moon deletion.
- Existing cloud encryption/restore formats, Google scopes, SSO tokens and local industry SQLite databases are unchanged. Backup archive verification still has a 512 MiB in-memory limit; larger archives require future streaming support. A complete new-PC restore remains a separate end-to-end acceptance test.

## Validation

Run the self-contained `Apply-v3.6.73.ps1` without arguments to stage, restore, build, run regressions and validate these release notes. Only if it prints **STAGED AND VERIFIED**, rerun the same script with `--publish`. The working repository remains unchanged during staging.

# EVE Command Center v3.6.72

- Add **Historical Data & Recovery Audit** to the Google Drive Backup window. Run a read-only local inventory that uses the **same actual file-selection rules as the encrypted backup**, grouped by Mining, Moon Operations, Contracts/Buyback, Industry, PI, Pilots and Other Data. Show included file counts and sizes, rather than inferring coverage from ZIP size.
- Run read-only SQLite `PRAGMA quick_check(1)` on discovered live SQLite databases and report any database whose integrity could not be verified; ordinary non-SQLite `.db` files are not misclassified.
- Report **earliest/latest recorded event dates only when real timestamp fields can be found** in JSON/JSONL. The date audit is deliberately partial (maximum 2 MiB per scanned source) and displays **Unknown** otherwise; neither file modification dates nor missing ESI history become fictional operational activity.
- Detect the separately stored EVE game-log folder beneath Documents as an **excluded source**; show its file count and approximate size. This release identifies missing collection/backup coverage but intentionally does not bulk-upload gigabytes of game logs or change storage retention rules.
- Add **Verify Local Encrypted Backup** to decrypt a manually selected `.ecc` backup and materialize it only into a temporary isolated directory. Check manifest schema, safe non-duplicated paths, all file sizes, SHA-256 checksums, separately encrypted EVE refresh-token entry, and SQLite `PRAGMA integrity_check` on included SQLite databases. Never write verified files into active Command Center data or Windows Credential Manager.
- Fail clearly when the audit cannot verify an archive (including wrong recovery password, corrupted ZIP, missing manifest or mismatched hashes). Verification is limited to 512 MiB per backup while the current encryption code uses in-memory buffers. A passing audit is **not** a substitute for full machine-to-machine disaster recovery.
- Preserve Google OAuth, encryption formats, existing backup scheduling, remote restore points, moon CSV contents, and every existing live restore action unchanged. No existing history is migrated, overwritten or deleted.
- Add synthetic regression coverage for year-spanning history date extraction, excluded EVE game logs, unknown timestamp handling, encrypted ZIP reconstruction into isolated storage, SQLite integrity, wrong-password rejection and no modification of live source data.

## How to use

1. Open App Settings > Backup > Google Drive Backup.
2. Choose **HISTORY / RECOVERY AUDIT**. Review the categories, file counts, sizes, timestamp coverage and missing EVE game logs.
3. Optionally choose **VERIFY LOCAL ENCRYPTED BACKUP**, and select a local `.ecc` file you have exported. This audits an encrypted copy without performing a live restore.
4. Do not assume year-long reports are complete if the underlying application has not captured every relevant event. A separate durable-collection audit/migration would be needed to fill uncovered operational categories.

# EVE Command Center v3.6.71

- Fix Google cloud backup failing with `sqlite-snapshot-*.db` **being used by another process** on Windows. Dispose both source and destination SQLite connections before reading the snapshot, and disable SQLite connection pooling for these temporary handles.
- Capture committed data from SQLite databases while Command Center keeps its own database connections open, including WAL-mode databases; keep temporary snapshots isolated and clean them up after all handles close.
- Differentiate genuine SQLite databases from other `.db` files using the SQLite file signature, and reject invalid `.sqlite` headers. An unreadable or corrupt SQLite database now fails the backup rather than silently falling back to an unsafe copy of a live SQLite file.
- Add live-database regression coverage with repeated snapshots of an open WAL database, restored-row and `PRAGMA integrity_check` validation, ZIP and encrypted recovery round-trip, and non-SQLite `.db` handling. No real credentials or private moon data enter tests.
- Preserve the v3.6.69 encrypted backup format, EVE credential inclusion, private moon CSV selection, retention schedule and the legacy v1 backup. No backup configuration or Google OAuth changes.

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
