# Industry, Market & Wallet testing guide - v3.6.76

## New in v3.6.76: dashboard production materials

1. Open the main Dashboard. Under Production, Market & Wallet, the Production materials panel lists shared demand for active projects. Green means stock covers demand; amber means a confirmed shortage; grey means inventory needs checking. These are material counts, not counts of ready projects.
2. Compare Available, Reserved here, Missing stock and Required. Free stock excludes all reservations, including paused projects. Two builds sharing Tritanium must be compared against the same pool once.
3. Click column headings to sort. Resize/reorder columns, leave and reopen the page: those preferences should persist.
4. Choose Open production materials. Industry opens its existing consolidated materials tab. No new project, reservation or EVE request should be created. With no projects, it opens the gallery.
5. Refresh Industry through its existing action and return to Dashboard. Stock updates appear within 30 seconds; saved project changes appear on the next dashboard refresh. Missing authorization, stale snapshots or moved reservations must not claim zero stock or confirmed shortages. Known sufficient stock can still show coverage when another inventory is unavailable.
6. Pause a project: its demand disappears, but its stock reservations remain held. Archive/delete using the existing project controls and check that totals change accordingly.

This patch advances Phase E; it does not complete the remaining Phase B/C/D reconciliation or profit work. The existing four phases with outstanding requirements still need further increments.

## New in v3.6.55: assign confirmed charges

1. Open Market & Wallet, choose a pilot, then Wallet activity. Refresh if needed using the existing wallet permission.
2. Select a broker fee, sales tax, manufacturing charge or industry job tax with a negative amount. The Project charge column identifies entries that can be assigned.
3. Choose a production project and enter its share in ISK. Save the charge. Use New split to share the remainder with another project. The total cannot exceed the original charge.
4. Open that project's Finances tab. Fees & charges shows the assigned amount, and Cash difference subtracts it alongside purchases. The Fees & charges detail tab shows each source date, pilot, charge and note. Pilot and period filters apply to all figures.
5. Return to Wallet activity, select a saved split, change it or remove it. Reload project finances to verify the change. Assignment history keeps before/after evidence; the wallet entry itself remains unchanged.

Only the four listed debit categories are supported in this increment. Transfers, escrow, trade payments, incoming credits and entries with missing amounts cannot be assigned as expenses. The source amount is used once; the optional tax metadata is not added on top. Every project link is an explicit user attribution, not an inferred match to an order or production batch. Unassigned costs, consumed-stock valuation and finalized profit remain outstanding. Existing ledger data is backed up before schema 6 migration.

## Start here: sync and dashboard

Open Command Center Setup > Sync & dashboard. The Automatic sync table has separate switches for Wallet, Market, Industry, PI, Pilot info and Clones for each linked pilot. Wallet includes balance, journal, purchases and sales. Market includes open orders and confirmed order history. Wallet defaults on for linked pilots; market starts with the previously selected market pilot(s), with no hardcoded names. Enable Market for whichever pilots trade, then Save sync settings.

Select a row and Check sync status to see enabled/disabled feeds, permissions, saved timestamps and the latest financial-sync messages. Missing permission requires reconnecting that pilot; a checkbox does not grant EVE access. A currently running cycle may finish after saving. Financial sync checks every five minutes while the application runs and respects each endpoint's cache expiry; other services retain their existing cadence. Closing a page does not stop app-owned sync. Market & Wallet refresh buttons can still load a disabled feed on request. The other service refreshes honor their pilot switches. Public prices, mining logs and the existing corporation readers are separate.

Dashboard visibility has independent Market orders, Wallet & trades and Industry jobs switches. Hiding a pilot must not stop sync or delete history. Projects remain consolidated across all manufacturers. The dashboard now shows project counts/names, saved buy/sell commitments, balances, imported daily trade amounts and 7/30-day wallet cash flow. Incomplete/stale saved feeds are labelled. This is the first Phase E increment, not the complete dashboard/notification plan.

Market & Wallet itself stays single-pilot: click a portrait to switch views. Viewing a pilot must not change the sync list. Open orders, order history, purchases, sales, wallet activity, wallet overview and PLEX remain separate. Opening tabs reads saved data. Background results update visible tables; an open allocation inspector is left alone to protect unsaved edits.

## Phase A - foundations (delivered core)

Implemented: persistent projects, selectable default owner/buyer/seller, stable project/node identities, per-component executors, shared ledger, backups/migrations, reusable ESI and credentials.

Test: create a project, choose its owner, assign another pilot to a component, close/reopen. The same project must retain its tree, assignments and history. Change defaults: existing projects must stay unchanged. No local assignment submits an EVE job.

## Phase B - manufacturing planner (usable core; incomplete full scope)

Implemented: project gallery/create/delete, owned blueprint library with pilot/location/container selection, manufacturing dependency trees, material breakdown, Make/Buy/Use Stock choices, exact personal-stock reservations, shared material totals, blueprint/skill checks, required-skills pilot grid, local job assignments, explicit existing-job matching and transfer-needed notes.

Test: create two builds sharing a material; reserve a stack for one and verify that free stock drops for the other. Click a component to inspect inputs, owned blueprints, skills, jobs and stock. Select a BPC and confirm its runs/ME affect the plan. Reassign a component without losing the parent build. Delete a project and verify reservations release while financial history remains.

Remaining: complete corporation/division inventory, full invention/reaction and facility/rig mathematics, stronger production/consumption/delivery reconciliation, batch/priority workflows and location-price procurement comparisons. Transfer-needed and job links are not proof of delivery or consumption.

## Phase C - market module (core tracking delivered; incomplete full scope)

Implemented: personal open buy/sell orders, separate confirmed histories, purchases/sales, durable imports, per-pilot views, named items/locations, cache/failure states and independent automatic sync.

Test: choose each trading pilot; only their rows should appear. Refresh twice and verify no duplicate trades or spend. Open-order values are commitments. An order disappearing is unconfirmed until reliable history establishes its outcome; it does not automatically become a sale.

Remaining: explicit order commitments split across projects, defensible order/fill association, corporation orders, price/undercut monitoring and opt-in fill/expiry/large-trade alerts.

## Phase D - attribution and financials (usable core; still in progress)

Implemented: wallet balance/journal/trades and PLEX history; purchase splits among eligible projects, general stock and unrelated activity; audited edits/removals; item acquisition-cost analysis; explicitly confirmed sale-to-project splits; project Purchases, Sales and Finances tabs with pilot/date filters and supporting-record navigation; audited ISK splits for confirmed broker fees, sales taxes and manufacturing charges.

Test: allocate part of a purchase to a project and the rest elsewhere. Its project costs must include only its split. Reassign/remove it and check totals/history. Assign a sale to a project after confirming origin; receipts must use its actual unit price. In Finances, change pilot/period and open a supporting row. Repeat imports and restart: amounts must not double. Wallet journal cash flow must not add transaction values again.

Remaining: additional expense/refund categories and automated provenance, consumed-stock cost and opportunity-cost valuation, defensible batch costs, forecasts/margins and realized P&L. Cash difference is not profit. Purchased, reserved, delivered and consumed remain distinct.

## Phase E - dashboard intelligence (started in v3.6.54)

Implemented: compact project/market/wallet summaries, links to existing detail modules, independent dashboard visibility, automatic financial sync policy/status and regression coverage.

Test: sync Wallet for all pilots and Market for two. Switch the viewed pilot: sync selections must stay unchanged. Hide one pilot from dashboard Wallet totals; their Market & Wallet history must remain. Turn automatic Wallet sync off: saved data must remain visible. Restart and verify all choices persist. Missing permissions/failed refreshes must not masquerade as confirmed zero balances or empty activity.

Remaining: richer readiness/procurement-gap widgets, independent transaction/financial presets, active-only/hide-zero presets, deduplicated cross-module activity feeds and the new market/financial notification categories.

## How much remains?

The brief defines five phases, A through E. All five have now been started; no additional lettered phase follows E. A's core foundations are delivered. B, C and D have usable delivered increments but retain the gaps listed above; E has just begun. Four phases therefore still have outstanding full-brief requirements. Completion will take several further validated patches, especially financial reconciliation; the current app should not claim complete industrial profit accounting.


## v3.6.77 acceptance checks

- Refresh Moon Operations while viewing Drill Schedule: existing scheduled drills must not briefly turn NOT SET during name resolution. The original saved snapshot had 127 schedules. A genuinely absent extraction can still show NOT SET after a complete response.
- Open a production project > Production & delivery. Sort quantity headings and resize/reorder columns; reopen to check preferences. Quantities are distinct overlapping stages, not an additive total.
- Reserve an observed component stack. In Stock & transfers > Confirm delivery, select that reservation and the destination stack, quantity, note and acknowledgement. Partial confirmation increases Delivered while keeping the full reservation held.
- For a handoff, refresh Industry inventory after hauling, then select the destination stack. The confirmed quantity must move from the origin reservation to a new destination reservation without doubling total held stock. Insufficient, stale, mismatched or already allocated destination stock must fail without changing state.
- Undo selected confirmation: delivered quantity clears, stock stays reserved. Releasing a confirmed reservation must fail until undo. Reopen the app to verify persistence.
- A root receipt covering the required output changes the project card to Delivered; child-only receipts do not finish the parent. Paused/archived labels take precedence. Delivery does not assert consumption or profit.
- Link a partial manufacturing job: In production uses actual runs times product output. Only ESI ready status populates To collect. ESI delivered requires explicit project receipt. Stale/missing job observations show Unknown, never assumed completion.
- One EVE job per component is still the limit. Multiple batch reconciliation, consumption and realized profit are not complete phases in this patch.
