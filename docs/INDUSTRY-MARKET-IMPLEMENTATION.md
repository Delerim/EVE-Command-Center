# Industry / Market implementation map

Assessment: 10 October 2026, main 8411ec0 / v3.6.37 (matches origin/main).

## Reuse and current gaps

- `EveSsoService`, `EveCredentialStore`, `EveAuthorizationScopes`: linked character IDs, existing protected refresh-token storage and consolidated consent. No new credentials or ESI client.
- `EsiHttp`: process-wide request scheduler, token-isolated response cache, expiry headers and rate/error handling; `EsiDiagnostics` logs safe request diagnostics.
- `IndustryService`: background personal jobs, blueprint instances, assets and skill snapshots in `industry.json`. It currently requires its combined scope set and has a single snapshot timestamp/error, not per-feed provenance. Corporation blueprint/assets ingestion is not implemented here.
- `IndustryCatalog`: 9,014 type names and 15,731 activity recipes; JSON has only Types/Recipes and **no SDE version metadata**. Existing ME/TE calculation and profitability scanner are useful estimates, not a complete facility-aware recursive production engine. Existing recipe scans are independent alternatives, not reservations.
- `MiningMarketService`: existing Jita/station price lookups. `EveSsoService` reads personal wallet transactions/journal for pilot dashboards but does not maintain a durable, paginated industrial attribution ledger. No persistent market-order lifecycle module.
- `BackgroundOperations`: app-owned polling and cached industry data. `OperatingToast`, industry notifications and Command Center embedded module hosting are reused. Existing Industry tabs and per-pilot views stay available.
- `ContractsTheme.xaml`, virtualized DataGrids and existing Command Center navigation supply the UI. New planning content stays within Industry; no new Window.
- Existing tests use the executable `Tests/CommandCenter.Checks.csproj`; tagged workflow validates notes, builds Windows x64 single-file output and packages the updater download.

## API assessment

Checked official https://esi.evetech.net/meta/openapi.json and https://developers.eveonline.com/docs/services/esi/overview/ on 10 October 2026. Current canonical character routes:

| Route suffix | Scope | Pagination |
| --- | --- | --- |
| assets | esi-assets.read_assets.v1 | page |
| blueprints | esi-characters.read_blueprints.v1 | page |
| industry/jobs | esi-industry.read_character_jobs.v1 | include_completed; no page |
| orders | esi-markets.read_character_orders.v1 | no page |
| orders/history | esi-markets.read_character_orders.v1 | page |
| wallet/transactions | esi-wallet.read_character_wallet.v1 | from_id |
| wallet/journal | esi-wallet.read_character_wallet.v1 | page |

Schema exposes If-None-Match / If-Modified-Since and compatibility-date parameters. Existing services pin 2026-08-25 and use shared caching; Phase A introduces no API calls other than reading the existing linked-character list locally. Later ingestion must verify response expiry, paging termination and scope-specific failure independently before updating this compatibility date. Never convert missing permissions or failed reads to zero stock. Scope availability is not proof of skill/facility eligibility.

## Persistence decision

Phase A adds one **planning document**, `industry-workspace.json`, next to existing app data. It stores user intent rather than duplicating ESI snapshots or wallet records. Schema version, revision, stable GUID project/node/reservation IDs, 64-bit EVE IDs/quantities and UTC audit events. Mutations copy, validate and atomically replace the document with a last-good backup; failure leaves memory unchanged. Corrupt/unknown schemas fail closed and retain the original. No inferred job, transfer or financial facts.

JSON is appropriate for this bounded first increment and matches existing storage. Before Phase C/D history ingestion, migrate this single document transactionally into an indexed SQLite ledger, with a preserved JSON backup and stable IDs. Do not create a competing transaction ledger beside it. Future monetary fields use decimal; historical cost is nullable, never an implicit zero.

## Staged file/module map

1. **A (this increment):** `Models/IndustryWorkspaceModels.cs`, `Services/IndustryWorkspaceStore.cs`, `Services/IndustryWorkspaceInventory.cs`, embedded `Views/IndustryWorkspaceView`, and `Tests/IndustryWorkspaceChecks.cs`. Unassigned defaults; linked-character role flags; persistent parent/component tree; owner vs executor; explicit source stack reservation/release with global shared-stock protection; audit trail and source provenance. Inventory view resolves nested parent IDs and rejects cycles/unverified ancestry, fitted/cargo stock and stale data. Existing reservations remain visible for review after moved/decreased stock. Plans never claim to be executable EVE jobs.
2. **B:** version/hash/provenance for catalog; blueprint-instance library and unavailable/runs checks; facility-aware validated recursive math; mixed Make/Buy/Stock demand; deterministic priority allocation; component transfers and explicit existing-job links. Expand asset browser to corporation divisions using access service and reliable scope-specific timestamps. Full aggregated procurement UI.
3. **C:** embedded Market module using existing SSO/EsiHttp; selected-character scoped imports, durable checkpoints, order history and idempotent transactions. Preserve failed/stale observations; no game mutations.
4. **D:** explicit/suggested purchase splits, reversals and audit trail, journals/fees deduplication, known-cost vs opportunity-cost valuations and defensible realized P&L.
5. **E:** independently selected compact dashboard widgets, activity feed, stable notification deduplication and full reconciliation tests.

## Phase A acceptance / limitations

### Try the first increment

1. Open **Industry > Production Queue**. Expand Character Defaults and Roles, choose any linked lead/buyer/seller, and save. Leaving them unassigned is supported.
2. In the existing **Blueprints & Material Planner**, select a single-product manufacturing recipe and click **Add to Queue**. Alternatively search a type by name in Production Queue, select Use for New Project, enter a name/output quantity and create.
3. Select the project. Search a component by name, choose Use for Child Component, select the parent in the tree, and add a child with an explicitly planned quantity.
4. Select that child, choose an executing toon independently of the project lead, and save. The entire parent stays in the consolidated queue.
5. Refresh Industry snapshots using the existing Jobs/Planner header if needed; return to the queue and reload cached data. Select a matching observed stack, review its owner and containment path, then reserve an exact quantity. Conflicting, unverified and stale sources cannot create reservations. Review or release existing uncertain reservations before allocating replacement stacks.
6. Restart and reopen the queue. Projects, children, defaults, assignments, reservations and audit history remain. Recovery of an unreadable file is explicit and preserves the original plus last-good backup; future schema files cannot be downgraded.

Create a draft parent project from a manufacturing recipe or a manually specified product type; expand/add a component; independently assign a linked executor; select an observed asset stack with its containment path; reserve an exact quantity; reopen the same consolidated project with assignments intact. Defaults apply only to new projects. Character switching elsewhere does not filter or mutate projects. Roles are planning/visibility metadata until the relevant later module consumes them.

Node quantities are explicitly planned quantities, not a claimed recursively calculated bill of materials. No automatic readiness, purchases, spending, delivery, consumption, transfer completion or job associations. Selecting a source does not deliver stock to a manufacturing facility. Unknown locations remain labelled by IDs; accessible personal snapshots only, corporation inventory unverified. Reservations require a recent successful snapshot and are rechecked on every allocation. Explicit release retains audit history. A stale/moved/insufficient stack needs review, not silent reallocation.

## Risks / validation

Priorities: never double-reserve physical units; never silently discard plans on corrupt files; never equate intent with in-game readiness; never hide children on lead reassignment. Regression tests cover restart, defaults, ID stability, executor delegation, parent cycles, stock overlaps, source moves, stale/error data, backup recovery, schema rejection and failed writes. UI render checks cover the embedded workspace, empty state and populated tree. Existing preview/alarm/mining tests remain mandatory. Release increment follows patch version, notes validator, regression suite, diff check, Windows x64 publish and normal tag workflow.
