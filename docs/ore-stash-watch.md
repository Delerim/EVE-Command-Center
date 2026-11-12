# Main-character ore stash monitor

Open **Mining > My ore stash**, select the linked character that owns your ore, and choose **USE MAIN & SCAN**. The choice is saved. Only that character's personal assets are requested by this monitor; it does not read alt or corporation assets. Changing the main clears the previous stash and its alert state.

The main needs `esi-assets.read_assets.v1`, normally included when linking a pilot. If it is missing, reconnect that character in Setup. `esi-location.read_ship_type.v1` identifies the current ship for in-space cargo whose ship may not appear as a parent in the asset snapshot. Without that identity, hangars and owned parked-ship cargo still work, but in-space coverage may be incomplete. Structure names can remain numerical when access does not allow name resolution.

## Advice

The defaults use an estimated corp payout of 90% Jita 4-4 buy against full Jita 4-4 sell:

- **SELL:** gap up to 15%, representing reasonable relative buyback value.
- **ASSESS:** gap above 15% and below 20%, or insufficient evidence to decide.
- **DON'T SELL:** gap at least 20%, such as a 10b Jita sell reference paying only 8b.

Both gap thresholds and the corp percentage can be changed. SELL means reasonable value against current quotes, not a forecast that this is the highest future price. The full Jita sell figure is a gross listing reference; market fees, broker fees, hauling, waiting and execution risk can reduce what direct selling would realise. Use Janice simulation for route-cost comparisons.

Each ore is evaluated independently across its storage locations. A poor Brimful payout does not block selling a separate fair-value normal Bitumens stack. The Janice basket summary also uses the overall gap, while retaining individual warnings.

The scanner follows personal hangars, nested containers and owned ship cargo / ore / ice / fleet holds. It excludes fitted slots, corporation hangar flags, unresolvable cargo and cyclic container references. Only Asteroid-category resources are priced. Raw ore is paired only with an exact modern Compressed-name match in the authoritative ore category. Modern ore and ice compression preserves unit count, so raw and already-compressed quantities share one selling assessment and one order-depth check. The row, storage description and alerts explicitly say to compress the raw portion first. No compression action is performed. Unmapped resources and legacy Batch Compressed variants require ASSESS instead of assuming a conversion.

## Inventory and prices

Monitoring runs in the app background roughly every ten minutes while ECC is open, including while the tab is closed. It does not run after ECC exits. All inventory comes from ESI asset snapshots, not live cargo telemetry; recent movement or mining may take time to appear. Asset dates and provider expiry are displayed. An unchanged older last-modified timestamp does not itself invalidate a snapshot whose provider cache is still valid.

Market lookup uses all pages of the item's regional order book, restricted to Jita 4-4 station. The monitor shows the corp quote and full sell quote as indicative values. It also consumes eligible bid levels in descending price order to estimate the immediate Jita buy value for the actual stash quantity, respecting order volume and minimum quantities. Insufficient bid depth, missing quotes, expired data or incomplete pages prevent SELL and selling-point alerts. Price failures on one ore do not manufacture a value of zero or suppress valid evidence for another ore.

Saved inventory is unverified after restart until a scan succeeds. A failed scan keeps previous quantities for inspection but displays ASSESS and disables new opportunity notifications from that evidence.

## Alerts

Good-selling-point notifications can be disabled independently of monitoring. By default, an ore must qualify for SELL with at least 100m ISK of estimated corp payout. Multiple newly qualifying ores are grouped into one desktop popup and appear in the notification centre. Clicking opens My ore stash.

An opportunity is remembered across restarts. Merely remaining at SELL, changing its quoted price slightly or moving between storage locations does not create a new popup every scan. To rearm, fresh evidence must move more than two percentage points above the SELL threshold and later return, with at least 24 hours since the previous alert for that ore. Removing and re-adding a stack cannot bypass the cooldown. The first qualifying scan may notify once. Stale or unavailable prices do not rearm an alert.

## Validation

Run the checks executable with `--audit-ore-stash` and optionally a PNG output path for the integrated fixture. `--audit-ore-market-live` checks a public Jita ore book without accessing private assets. The offline checks include main-character isolation, scoped storage traversal, real held volume, stale evidence, pagination, missing scopes, character-switch races and notification persistence. The main character's live private inventory must be checked separately after selection in the app.

Modern unit-count handling follows [CCP Masterplan's compression specification](https://forums.eveonline.com/t/details-on-forthcoming-improvements-to-compression-mechanics/349870) and the [20.03 release notes](https://www.eveonline.com/news/view/patch-notes-version-20-03).

ESI schema reviewed against [the official ESI OpenAPI document](https://esi.evetech.net/meta/openapi.json). No additional write scopes, market transactions, asset movement or Discord messages are introduced.
