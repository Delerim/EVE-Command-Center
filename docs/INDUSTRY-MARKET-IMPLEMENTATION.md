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

## Phase B first increment (v3.6.39)

Production Queue retains the shared Industry header and tab layout. Its workspace now separates blueprint library, project/components, consolidated materials and setup. `IndustryBlueprintLibrary` projects existing personal blueprint/asset/job snapshots into an instance table filtered by toon and full station/container path. It does not introduce new ESI routes. Custom container and station names are not fetched; stable IDs and available container type names are displayed.

`IndustryDependencyPlanner` expands single-product manufacturing recipes, resolves blueprint type to manufactured product, computes ceiling runs and batch material requirements, and retains surplus output separately. The selected root blueprint's ME and instance/owner/location are retained; insufficient/in-use/stale BPCs are rejected and planned runs cannot exceed the same copy across projects. Child recipes use explicit ME 0 with no blueprint assigned. Facility/rig bonuses, reactions and invention are excluded; materials with no unique manufacturing recipe become purchase leaves. Guards reject cycles, arithmetic overflow and excessive trees. Catalog SHA-256 identifies calculation provenance; the bundled catalog still has no authoritative upstream version date.

Material rounding follows the existing batch formula with decimal arithmetic, including the one-per-run minimum; checked against [CCP material-efficiency guidance](https://support.eveonline.com/hc/en-us/articles/203210542-Material-Efficiency-Research). No claim of facility-aware readiness or manufacturing eligibility is made. Calculated quantities cannot be manually edited into an inconsistent tree; create a new batch plan. Existing unreserved one-node drafts can be expanded, retaining their root/project identity. Existing trees are never silently regenerated over manual assignments.

`IndustryShoppingList` is a read-only projection of saved planned quantities. Active projects contribute demand; paused/archived projects do not. Existing reservations across all projects reduce observed free stock. Buy / Use Stock nodes terminate traversal; Make parents contribute children. Make leaves without input plans and reserved Make parents are marked for review. Parent stock does not automatically scale child demand. Unreserved demand is not a confirmed purchase shortage: explicit stock reservation and hauling review are still required. No orders, purchases or consumption are inferred.

Delete Project requires inline confirmation and no remaining stock reservations; its audit history survives. New planning metadata is additive within schema 1; existing manual projects retain their original behavior.

Still pending in Phase B: child blueprint selection/recalculation, facility-aware calculations, reactions, corporation inventory, job matching and transfer tracking. Market/financial imports remain later phases. Tests cover real Obelisk expansion, batch rounding/overproduction, BPC constraints and double planning, 2,100 blueprint instances and container filtering, actual UI creation/deletion, persistence and old-draft conversion. Render audits cover library, component tree, stock and consolidated materials.

## Split workspace, local job plans and Phase C start (v3.6.40)

The project/component tree is now persistent on the left of a resizable split. The right contains editors, blueprint library, materials and setup. Clicking a component selects its editor. Calculated Make nodes support explicit local manufacturing job assignments with stable GUID, executor and optional facility ID. Assignments remain planning metadata: no ESI job submission or actual-job association, eligibility or delivery inference. Conflicting sourcing changes require removing the local job assignment first.

Phase C begins with `MarketOrdersService`, `MarketOrderModels` and embedded `MarketOrdersWindow`. Uses existing SSO and `EsiHttp`, independently selected linked character IDs and a latest-observation cache (`market-orders.json`), not a financial/history ledger. Verified the current official `https://esi.evetech.net/meta/openapi.json` on 10 October 2026: GET `/characters/{character_id}/orders`, scope `esi-markets.read_character_orders.v1`, no pagination, cache TTL 1200 seconds, compatibility-date header supported. The app retains its existing 2026-08-25 compatibility date. Scope is included in consolidated reconnect consent; missing permission does not trigger automatic authorization.

User-initiated refresh honors cache headers/default 20 minutes and preserves snapshots on partial failures. Character selections and valid snapshots use atomic file replacement and last-good backup; unreadable cache blocks overwrites. Open-order prices/remaining notional values use decimal ISK and quantities use 64-bit integers. Missing/failed/stale status is visible. Corporate orders from the character feed are excluded. Hiding a toon retains its cached observations. No disappearance-to-fill inference or order mutation occurs.

Pending Phase C: durable order lifecycle/history and paginated transactions, with the previously proposed transactional ledger migration before historical attribution. Phase D accounting and related dashboard work remain unimplemented. This cache is explicitly replaceable observation data rather than a second transaction ledger.


## Manufacturing workflow and UI refinement (v3.6.41)

This increment follows brief sections 2.1-2.3, 6, 16 and 18 before resuming Phase C. It is not a claim that Phase B or the full brief is complete.

- `IndustryWorkspaceTheme.xaml`: reusable Industry-only buttons, fields, tabs, expanders, selected/hover rows and full-width tree templates. The common Industry header remains above every tab; its pilot/activity filters still concern observed jobs/recipe planning, not consolidated project ownership. The workspace keeps the project/component tree on the left and a component inspector on the right, with separate inputs, blueprint/jobs, stock/transfers and notes/assumptions sections. Reload preserves expanded branches and project/node identity.
- `IndustryWorkspaceStore.SelectBlueprint`: applies an owned component blueprint and regenerates its inputs at that instance's ME. Maintains project/node IDs. Rejects replacement of reserved, job-linked or configured child work, and enforces shared BPC-run limits. No facility/rig eligibility or game submission is claimed.
- `IndustryJobMatching` and persisted `IndustryJobLink`: explicit manual association using authorized manufacturing snapshots, product, blueprint type/instance, installer, facility, runs, start/end, job ID and observation timestamp. Duplicate EVE job links across nodes are rejected. Partial job runs remain visible as evidence; they do not count as delivered or reserved quantities. A disappeared/stale job keeps its link and becomes unverified, never silently completed. Matching evidence is included in the audit history and survives unlink/deletion.
- `IndustryDisplayNames`: separate display-name metadata from canonical reservation paths. Existing shared SSO type-name lookup is reused for catalog gaps. `IndustryService` resolves custom parent-container names in batches (maximum 1,000), stations and accessible structures with the existing EsiHttp client and token. Optional `esi-universe.read_structures.v1` is added to consolidated reconnect consent; its absence does not block core Industry refresh. Authenticated structure resolution can validate an item-type root missing from the personal asset list; unknown missing containers remain unverified. Names refresh at most hourly; metadata failure retains core snapshots and previous display names.
- Endpoint verification: official `https://esi.evetech.net/meta/openapi.json`, 10 October 2026. POST `/characters/{character_id}/assets/names` uses asset-read permission, body 1-1,000 unique IDs; no pagination. GET `/universe/stations/{station_id}` is public and expires daily at 11:05; GET `/universe/structures/{structure_id}` requires read-structures scope and ACL, TTL 3,600 seconds. Existing shared EsiHttp caches GET responses and honors rate/error limits; asset-name POST is gated by persisted hourly metadata cadence. Existing compatibility date remains 2026-08-25.
- Project deletion now has a discoverable button and inline confirmation including reservation count. One atomic store change records the complete deleted project/reservation snapshot, releases only that project's reservations, and removes the local plan. Other projects and actual EVE state are untouched.
- Transfer-needed flags and instructions persist as planning intent. Clearing that requirement is not recorded as confirmed delivery.

### Remaining brief requirements

The full brief still requires corporation/division inventory with scope-specific freshness, manual stock assertions, richer hierarchical browsing, bulk assignment, batch splitting and quantity editing with non-destructive recalculation, facility/rig/skill-aware math, authoritative catalog versioning, reactions/invention, and full separate per-node stock/ordered/purchased/manufactured/delivered/consumed/missing reconciliation. Multiple partial actual jobs per node and verified component handoffs are not implemented by the single explicit job link. Parent component stock does not yet automatically scale child demand. Market history, transactions, expenses, attribution, financials and dashboard work remain in the previously mapped phases. UI controls must not pretend those capabilities exist.

### Validation

Existing suite plus component-flow regressions cover blueprint ME recalculation, stable IDs, actual-job matching and restart, wrong/stale/duplicate evidence, preservation of linked work, transfer intent, structure vs missing-container ancestry, rename-safe reservations and atomic deletion with audit retention. Render fixtures cover 1,920x1,020 and 1,280x850 window sizes, inputs, job association, library and stock. The WPF selected-row template is checked at actual arranged width. Live desktop mouse automation was unavailable in this session; screenshots and WPF control-level checks are not presented as an end-user desktop test.


## v3.6.42: project navigation, readable inventory and layout preferences

Production Queue starts at a project gallery. New project opens a dedicated owned-blueprint creation page; component work remains embedded in Industry. Jobs owns its activity filters, and the recipe planner no longer inherits those filters. The component tree remains on the left. Inputs and consolidated materials use available height; stock and reservations share a resizable vertical region. Blueprint/job/role table heights, navigation width, column widths and order persist separately in `industry-layout.json`. Corrupt preferences fall back to defaults without affecting plans.

Material projections retain separate totals: verified observed owned stock, free stock after reservations across all projects, all reservations, and reservations contributing to current active-build demand. Unverified or blocked sources are labelled, not represented as confirmed zero availability. Icons and resolved names are display-only. Canonical reservation paths and IDs remain unchanged.

Readiness badges distinguish a usable owned BPC/BPO, insufficient runs or busy copies, missing recipe skills, and unknown/stale/unauthorized snapshots. Unassigned nodes can show an available fleet copy but cannot claim trained skills. These are recipe checks, not facility readiness or permission to submit an EVE job.

Clone metadata now resolves station/structure and implant names using the existing SSO client. Market location/type names and planetary container locations use the same resolver. A failed optional lookup does not invalidate a successful inventory/order/clone observation. Raw source and historical audit data are retained; UI projections omit keys and technical evidence dumps. Structure names still require scope and ACL access. No new SSO flow or independent ESI transport was added.

WPF render fixtures cover the gallery, creation library, component inputs, jobs, stock and materials at wide and compact sizes. Checks exercise persisted layout dimensions/order, corrupt preference recovery, new-project navigation, missing versus unknown readiness, clone station names, and readable history. Live desktop mouse automation is unavailable; control-level checks and renders do not claim a live end-user mouse test. The remaining brief requirements listed above are unchanged.


## v3.6.43 - Owner readiness and project skill comparison

Blueprint badges are scoped to LeadId; skills use ExecutorId when explicitly assigned, otherwise LeadId. The Required skills tab aggregates the maximum direct recipe skill levels across the active Make tree, names each contributing product, and compares linked pilots without changing plans. Missing recipes, missing authorization and stale snapshots are distinct from missing training. Facility eligibility and prerequisite training plans remain separate.

Stock absence is only N/A when every linked personal inventory has a current successful authorized snapshot and no matching source exists. Unknown never means zero. Allocation and canonical source identities are unchanged.

Omega preserves supplied clone names and offers explicit refresh. ESI's optional name field was absent in the reported cached character response; the UI cannot infer custom names from implants or clone location.


## v3.6.44 - Stock coverage and assignment clarity

Missing stock is max(0, required - reservations for these builds - free stock). Unknown inventory and reservation review remain nonnumeric. Coverage is an availability comparison, not an allocation or a hauling/delivery claim. Separate component rows may share free stock; consolidated demand remains the combined comparison.

Gallery status projects evidence: Planning, explicit planned-job assignment, or explicit EVE job link. There is still no delivery/consumption reconciliation or automatic Completed transition. Global defaults moved to the gallery; unused role preferences remain persisted but are not exposed as functioning dashboard controls. Assignment terminology changed without altering store invariants or starting in-game jobs.


## v3.6.45 - Sorting, hover motion and Phase C order observations

TableSorting handles template and bound columns, numeric nullable quantities with unknowns last in both directions, and reapplication after visible source replacement. HoverMotion scales card rendering by 1.015 for 130ms with an eased return; layout bounds do not change and Windows client-area animation preferences are respected.

The Market open-order observation cache now retains one last observation for orders absent from a successful full response, with last-seen and first-missing timestamps. Same-character reappearance removes that absent observation; failures and missing scopes never create absence. Corporate rows are excluded from absence tracking. UI filters open/missing/all independently of buy/sell, while totals count only currently observed open rows. No new ESI endpoint, history pagination, inferred terminal state, fill or transaction was added. This stays in the existing replaceable observation cache, not a second transaction ledger. The shared transactional ledger migration is still required before confirmed historical ingestion and attribution, as specified above.


## v3.6.46 - Aligned quantities and durable confirmed order history

QuantityHeader and QuantityText share right alignment and matching nine-pixel insets with numeric cells; existing sorts, resizing and preferences remain intact.

Phase C now imports the official character orders/history feed, verified against https://esi.evetech.net/meta/openapi.json on 10 October 2026: page / X-Pages pagination, market-order scope, 3,600-second cache, cancelled and expired states only, past 90 days. No completed/fill inference. Existing EsiHttp transport and SSO tokens are reused with compatibility 2026-08-25. Every page must succeed with a consistent page count; duplicate IDs, malformed records and unsupported states fail the whole character import. Cache age is respected across all pages. Selected characters remain independent, personal records only. Existing history is retained on empty responses and failure. Wallet transactions and attribution remain pending.

The first explicitly requested history refresh migrates the planning document under its existing exclusive lock into industry-ledger.db (Microsoft.Data.Sqlite 8.0.31). A temporary database is created and committed before atomic rename; the source JSON and exact pre-ledger backup remain unchanged. IDs, full workspace content and audit events are preserved. Components, reservation sources and historical orders have indexes. A workspace revision compare-and-update plus its indexes commit together. History is keyed by owner/order with source endpoint, compatibility date and observation time; the per-owner refresh checkpoint commits with all records. Monetary values round-trip through JSON decimal text, never SQLite REAL. Existing store instances switch to ledger writes after migration. Unknown database versions or externally changed legacy JSON fail closed. Legacy JSON backup recovery cannot overwrite an existing ledger.

Microsoft.Data.Sqlite transaction and connection behavior checked against official docs: https://learn.microsoft.com/dotnet/standard/data/sqlite/transactions and https://learn.microsoft.com/dotnet/standard/data/sqlite/connection-strings. Connections disable pooling to permit migration rename and deterministic cleanup. Native SQLite is bundled into the single-file Windows release. This increment does not add wallet/financial tables or a competing attribution store.

Validation covers migration identity/backup preservation, already-open store writes, stale writers, restart, malformed source refusal, partial-page rollback, exact decimals, cache cadence, repeated imports, unknown terminal states, corporate exclusion and retained history. Render fixtures cover aligned and sorted material headings and the confirmed-history view. Full desktop mouse automation remains unavailable.


## v3.6.47 - Reconnect repair and confirmed personal transactions

The SSO access-token cache now compares decoded character/scope metadata against the current linked profile before reuse; a stale token missing newly granted permissions must refresh through existing credential storage. JWT decoding here is only a cache eligibility check, not a replacement for SSO authentication. Read-only live requests confirmed that the current permissions work. Live history also exposed zero-day immediate orders, which are now accepted (negative duration remains invalid). HTTP failures get bounded human-readable explanations, without response bodies, credentials or exception type names.

Verified official https://esi.evetech.net/meta/openapi.json on 10 October 2026: GET /characters/{character_id}/wallet/transactions, scope esi-wallet.read_character_wallet.v1, from_id pagination, 3,600-second cache, char-wallet rate group. Same EsiHttp transport and reviewed compatibility date are reused. The live service includes the cursor boundary record; exact immutable boundary evidence is deduplicated, a boundary-only page ends the scan, and records greater than the cursor or conflicting boundaries fail closed. No order identity is provided by transactions, so no automatic order/fill/project attribution is asserted.

Schema 2 adds indexed wallet_transactions and per-owner transaction checkpoints to industry-ledger.db. SQLite BackupDatabase preserves a consistent schema-1 backup before transactional additive migration. Keys are owner/transaction; integer IDs and quantities remain Int64, monetary fields round-trip as decimal JSON text. Evidence conflicts roll back the complete import and checkpoint. Empty responses and hidden pilots never delete historical rows. The existing pilot-dashboard latest transaction cache remains replaceable, not a second ledger. Personal-only filtering excludes corporation activity. Absolute cached-response expiry prevents accidental freshness extension.

UI: Refresh orders / Refresh confirmed history / Refresh transactions, Transactions view with Purchases and Sales tabs, searchable named item icons, station names and numeric sorting. Totals cover imported records for selected pilots and current search, explicitly gross and potentially incomplete. Fees, journal ingestion, suggested/manual allocations, transfer reconciliation and P&L remain Phase D work.

Validation includes scope-cache eligibility, explanatory errors, absolute expiry, schema backup and stable projects, later-page rollback, decimal precision, idempotency, both exclusive and inclusive cursor responses, conflicting evidence, missing scope, restart, hidden pilots and transaction view filtering. A temporary isolated read-only live import succeeded for open orders, 300 history rows and 105 personal transactions; temporary records were removed afterward. No production planning data was changed by that diagnostic. Render fixtures are separate from live desktop mouse testing.


## v3.6.48 - Visible, cancellable market refresh

The saved HttpRequestException can outlive an in-progress transaction refresh. RenderOrders previously overwrote progress when changing views, and optional metadata could delay the primary import through the shared ESI queue. All three actions now share a cancellable refresh lifecycle with a visible request/page stage and restored controls. View changes preserve progress. Old network errors are translated only for display, never treated as successful data or removed from freshness checks.

Optional name resolution receives a linked 15-second cancellation budget per pilot. User cancellation propagates; optional lookup failure or timeout lets validated primary records commit, preserving names already resolved. Existing shared ESI pacing and full-page atomic imports remain intact. Tests cover timeout, user cancellation, view switching, button recovery and preservation of cached orders.

Validation: 569 checks passed. An isolated read-only live import using the normal optional name-resolution path succeeded for open orders, confirmed history and personal transactions with the current pilot permissions. Temporary diagnostic storage was removed; production project and market records were not rewritten.


## v3.6.49 - Market navigation and Phase D journal foundation

Market now has dedicated open-buy, buy-history, open-sell, sell-history, purchase, sale, wallet-activity and unconfirmed-order tabs. Historical tabs show only confirmed cancellation/expiry evidence, never inferred fills. Portrait-card multi-selection persists immediately in the existing market preferences. A separate per-pilot view filters cached data without modifying the tracked cohort or any ledger rows. The selected tab supplies one refresh action; progress/cancellation survive view changes. Search, numeric sorting, readable names, hover feedback and explicit empty states reuse the workspace styling. Transaction and wallet views offer all-imported / today UTC / 7-day / 30-day filters.

Official ESI OpenAPI verified on 11 October 2026: https://esi.evetech.net/meta/openapi.json, GET /characters/{character_id}/wallet/journal, page/X-Pages pagination, esi-wallet.read_character_wallet.v1, 3,600-second cache, char-wallet rate group, 30-day source window. Reuses the existing EsiHttp transport, SSO, compatibility date and EveWalletJournalEntry model; the pilot dashboard remains a replaceable latest snapshot, not a second historical ledger. Added context, parties, description and tax metadata to the shared model without exposing raw identifiers in the Market UI.

Ledger schema 3 adds owner/entry-keyed journal evidence and per-owner checkpoints with a SQLite backup before upgrade. Full-page imports validate required fields, stable pagination and duplicates before committing entries and checkpoints atomically. Immutable conflicts roll back rather than rewrite financial history. Empty responses never delete previously imported entries. Decimal amounts stay in JSON rather than SQLite REAL. Optional amount means unknown, not zero, and the UI counts/explains entries excluded from totals.

Wallet cash flow sums each journal amount exactly once. It does not add transaction totals or the optional tax field, and includes transfers/other activity rather than claiming manufacturing profit. Related trades require the exact owner plus journal reference; cross-pilot identifiers cannot match. Fees are not assigned to products or projects based on timing alone. Manual/suggested allocation, acquisition valuation and project P&L are still pending; this release starts Phase D rather than claiming completion.

Validation: 593 full checks, including later-page rollback, cache cadence, missing permissions, changed/missing pagination, duplicate records, immutable conflict rollback, empty-source retention, stable project identity, schema backup, restart, per-pilot view isolation, persisted selection, date filters, null amounts and no double counting. A temporary read-only live journal import succeeded with 1,011 entries; temporary storage was removed without modifying production planning/market records. WPF render fixtures cover the changed tabs and wallet view; these are not live desktop mouse tests.


## v3.6.50 - Phase D purchase attribution

Market Purchases now exposes an embedded, resizable Why did I buy this? inspector. Confirmed personal purchases can be split across projects containing the same item, general stock, and unrelated activity. Matching projects are suggestions only; no destination is preselected or silently assigned. A saved split can be edited, reassigned or removed. The table shows allocated/unallocated quantities, destination labels, search by project and an unallocated filter. Layout widths persist alongside existing UI preferences, independently of planning data.

Industry projects have a Purchases tab showing attributed quantities and acquisition costs from the exact immutable source unit price. The project total is explicitly purchase cost, not profit; fees, manufacturing expenses, old-stock valuation and sales attribution are not yet incorporated. Financial intent does not reserve assets, mark transfers complete, reduce physical shortages or imply delivery. Existing reservations and project readiness remain independent evidence.

Schema 4 adds purchase_allocations keyed by owner/transaction with a foreign key to the existing wallet_transactions source, plus append-only allocation_audit before/after snapshots. A SQLite pre-upgrade backup preserves populated schema-3 records. Saves validate the personal purchase, positive whole quantities, aggregate capacity, unique split identities and current project/type eligibility inside one SQLite transaction. Per-purchase expected revisions reject stale editors. No-op saves are idempotent. Audit-write failures roll back allocations and revisions. Project names are snapshots for historical explanation, while current project identities remain stable; deleting/archiving/replanning a project retains unchanged historical links, but cannot receive new allocations until eligible. Removing those links retains the audit trail. No cascading project deletion can erase expenses or source transactions.

No new API endpoint or authorization flow is introduced. This increment uses confirmed transaction evidence already imported through the existing ESI client and ledger. Wallet cash-flow totals do not add allocations again. Suggested order/fill matching, reservation/consumption reconciliation, fee allocation, economic valuation and defensible batch/project P&L remain pending.

Validation: 619 full regression checks. New cases cover multi-project splits, exact decimal costs, unchanged physical reservations, duplicate saves, quantity bounds including overflow-sized requests, unrelated type/project rejection, owner isolation, sale rejection, duplicate split identities, reallocation, stale editors, transactional audit failure, project deletion, reversal, retained source records, restart and previous-schema upgrade. WPF fixtures exercise the editor, explicit project choice, save feedback, named audit history, project-name search, attributed project costs and remembered table/pane widths. Render inspection prompted compact editor placement and readable minimum financial column widths. These fixtures are not desktop mouse automation.


## v3.6.51 - Allocation clarity and acquisition cost analysis

The purchase inspector now explains its eligibility rule: only non-archived projects containing the purchased type are suggested. Paused projects are labelled; historical allocations remain visible. Unmatched purchases show general stock and unrelated destinations plus an explicit explanation. Destination choices include project icons, readable descriptions and a no-selection prompt. Assigned/remaining quantities, a progress indicator and quantity cost preview expose the effect before saving. Actions stay pinned below the scrollable inspector; empty saved allocations/history are explicit. Allocation validation and ledger revision checks remain authoritative.

Continued Phase D with ProjectAcquisitionCosts, a read-only aggregation over the existing project purchase allocations. Each type has exact decimal total cost, checked Int64 assigned quantity, quantity-weighted average price, min/max confirmed prices and last purchase time. Purchase counts use owner plus transaction identity, so separate splits of the same trade do not inflate counts and different pilots with equal transaction numbers remain distinct. The UI offers By item and Purchases with an item drill-down, whole-project summary cards, named icons and remembered sortable column widths. Filtering never writes financial or inventory state.

This is acquisition-price analysis, not a weighted-average inventory consumption ledger. Fees, prior stock valuation, manufacturing costs, sale/batch attribution and profit remain separate future work. No API routes, source imports, ledger schema or physical reservation semantics changed. Confirmed free purchases can have zero cost; missing historical stock cost is not inferred from them.

Validation: 632 checks, including mixed-price weighted means, exact decimal sums, repeated source splits, owner identity isolation, confirmed zero-price versus empty evidence, integer overflow, explicit UI project selection, unmatched-item explanation, save bounds, preview amounts, saved progress, project totals and source purchase drill-down. WPF render fixtures reviewed at compact and taller viewport sizes; no desktop mouse automation is claimed.

## v3.6.52 - Single-pilot Market & Wallet and sales attribution

Market & Wallet now selects exactly one pilot through portrait cards and remembers that selection. All order, transaction, journal, cash-flow and PLEX views use that pilot. Switching views reads saved data without a request. The old Pilots wallet UI and its independent balance/journal/transaction requests were removed. Wallet overview adds the canonical GET /characters/{character_id}/wallet balance endpoint through the existing token provider and EsiHttp scheduler; the official ESI OpenAPI schema was checked on 11 October 2026. It uses the existing wallet scope, response expiry and atomic market-state storage. Failed or malformed reads retain the last amount. Today and seven-day cash flow use journal entries only, with unknown amounts excluded explicitly; trade values are never added twice. PLEX prices are quantity-weighted imported trade prices, not current holdings or profit.

Phase D adds explicit sale splits and a project Sales view. Suggestions match non-archived project root products; matching a purchased input is not evidence that a project produced it. New or changed project splits require explicit user confirmation of origin. Gross receipts use immutable source unit prices and exact decimal arithmetic. Allocation does not change readiness, delivery, reservations or project lifecycle. The existing purchase engine is shared with separate sale tables, side validation and source-owner identity. Schema 5 backs up populated schema 4 before adding sale_allocations and sale_allocation_audit. Saves enforce quantity capacity, revisions and atomic audit writes. Project deletion retains historical links and source records, while preventing new allocations to deleted projects.

Validation: 663 checks, including purchase regressions, sale-side guards, owner isolation, quantity limits, stale editors, idempotence, audit rollback, schema-4 migration/backup, restart, reversal, deleted-project history, explicit confirmation, project receipts, wallet expiry reuse, independent balances, malformed/failed reads, missing scopes, journal-only cash flow, PLEX weighted prices and view switches without requests. WPF render fixtures cover the sale inspector, project Sales, Wallet overview and PLEX; these are not live desktop mouse tests. Fees, inventory consumption valuation, order/fill matching and defensible batch/project profit remain pending.

## v3.6.53 - Project financial evidence overview

Continued Phase D with a project Finances tab over existing purchase and sale allocations. IndustryLedger.ProjectFinancialEntries reads both allocation tables and their immutable transaction sources in one SQLite read snapshot. ProjectCashFlow calculates exact-decimal spending, receipts and cash difference; owner/transaction identity counts a source trade once even when it has several splits. Each split remains a distinct supporting row. Pilot and UTC period filters apply to all figures, and an empty result is not presented as confirmed zero activity. No new persisted state, schema migration, API route or authorization is introduced.

The view reuses native project navigation, item icons, table sorting and remembered column layout. It shows three summary cards, a clear accounting-coverage note, dated paid/received rows, and exact allocation drill-down into Purchases or Sales. Opening or filtering the view does not change project identity, stock, reservations, production or delivery. The cash difference is not realized profit: fee allocation, manufacturing costs and defensible consumed-stock/batch valuation remain pending, and purchases may be unused while sales are partial.

Validation: 678 checks, including exact decimal net amounts, split counts, different owners sharing transaction IDs, pilot isolation, inclusive date boundaries, empty evidence versus free purchases, invalid periods, overflow, changed allocations, combined-ledger restart, exact purchase/sale navigation and unchanged planning state. WPF render fixtures cover the new Finances tab; no live desktop mouse automation is claimed.

## v3.6.54 - Per-pilot sync policy and initial Phase E dashboard

PilotSyncSettings persists automatic feed preferences and separate dashboard visibility in pilot-sync.json; it is configuration, not a second ledger. Invalid configuration is preserved and automatic sync fails closed. Wallet defaults on for newly linked pilots, market migrates the prior selected set, and existing nonfinancial polling retains its cadence with per-pilot gates. Industry is one combined jobs/blueprints/assets/skills feed in this increment. Moon and contract readers retain their existing corporation setup. Public price feeds and local mining telemetry are outside these toggles.

FinancialSync reuses the existing MarketOrdersService methods with explicit character targets. The five-minute app-owned cycle respects each feed's existing cache expiry and keeps the viewed pilot independent of sync selection. Cache persistence preserves a selection changed while a request was in flight. Visible Market views observe saved updates without queries; automatic repaint is deferred while an allocation inspector is open. There are no new ESI routes, credentials or permissions. Manual refresh remains available independently of automatic policy.

Phase E begins with project counts/names, saved order commitments, balance coverage, imported daily purchases/sales and 7/30-day journal cash flow on the main dashboard. Market, wallet/trades and industry-job visibility are independent of sync. Saved/partial/older observations are labelled; no realized profit, inferred fills or invented readiness is claimed. Full readiness/gap widgets, activity/notification categories and richer presets remain future work. See INDUSTRY-MARKET-TESTING.md for the complete phase status and user testing checklist.

Validation: 695 checks, including saved independent policies, no unsaved mutation, exact per-feed request routing, cache expiry reuse, in-flight pilot selection preservation, disabled feed retention, separate visibility, confirmed empty versus unloaded snapshots, corrupt-file preservation and journal/transaction cash-flow deduplication. WPF fixtures cover settings and dashboard widgets; no live mouse automation is claimed.


## v3.6.55 - Phase D confirmed fee and manufacturing-charge attribution

Wallet activity now offers an amount-based project split editor for confirmed negative brokers_fee, transaction_tax, manufacturing and industry_job_tax entries. The allowlist was checked against the official ESI schema at https://esi.evetech.net/meta/openapi.json. Trade payments, transfers, escrow, credits, missing amounts and other categories are ineligible. No new API route or refresh is introduced. Tax metadata is not added to the debit amount.

Schema 6 adds owner/entry-keyed expense_allocations with a source foreign key and append-only expense_allocation_audit. Migration creates a populated schema-5 backup. Saves atomically validate source eligibility, positive decimal amounts, aggregate capacity, unique identities, active project destinations and optimistic revision. Unchanged historical assignments survive deleted or archived projects; they can be removed, but cannot receive changed amounts. Original wallet evidence is never edited. No-op saves are idempotent.

Project Finances reads trades and charges in one SQLite snapshot. Charges have their own total and sortable detail table; the selected pilot and period apply to all figures. Cash difference now subtracts explicitly assigned charges. This still is not profit: unassigned costs, consumed-stock valuation and full batch provenance remain outstanding. Allocation panes defer automatic repaint while open and reuse saved column/pane sizing. Native WPF fixtures and regression checks cover persistence, limits, rollback, migration, filters and UI actions.
