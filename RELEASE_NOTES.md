# EVE Command Center v2.9.0

## Fitting EHP correction

- Fixed the fleet resistance stacking formula. Capacity-only command bursts previously recalculated fitted resistance modules with overly strong penalties, lowering EHP.
- Fit and fleet calculations now share one stacking implementation. Damage Control remains outside the normal resistance penalty group and applies only when fitted.
- Use the Orca's calculated burst strength when available, retaining precision from its fit, skills and implants. Manual activation remains necessary because ESI cannot verify an active burst or its range; unresolved burst data uses an explicitly labelled 19.7% assumption.
- Shield resistances by damage type and the fitting snapshot time are now visible in the EHP tooltip.
- Regression fixture for Saberlash's supplied three-MLU Skiff reproduces approximately 122,833 EHP, including duplicate rigs, skills, MC-805 implant and the shield extension burst.

## Overview startup

- Linked character portraits no longer wait for a public name lookup or the entire fleet's fitting requests.
- Show each pilot as it loads; prioritize configured Orca boosters and display ship identity before finishing the fitting calculation so drone mode can appear earlier.
- Retain successful fitting data during refresh errors and cache recent results across restarts. Tooltips identify the saved snapshot time. A ship change clears the previous ship's fitting data.
- The first launch without saved fitting data still needs ESI to return it; syncing text replaces misleading reconnect prompts while requests are running.

## Moon cycles and layout

- Small structure icons use the actual ESI structure type in active fields, upcoming extractions and the fuel view.
- Added drag dividers between active fields, the upcoming schedule and the field ledger. Table columns remain resizable.
- Current-cycle counts start at the latest recorded fracture of the persistent first Raren anchor moon. The anchor is initially chosen from the earliest recorded Raren fracture, and the name/start date are displayed.
- Fractured pulls, mined ore, estimated losses, observed jackpots and expired fields with ore left now refer to that cycle. Live fields/ready chunks are labelled separately. All historical records remain available.
- Missing anchor history is shown explicitly rather than presenting all-time totals as a current cycle.
- Matched the Buyback Report tab's inactive colour to the other contract tabs.

## Station fuel

- New Station Fuel tab shows structures, systems, fuel expiry, days remaining, status, reserve bars, services and fuel-bay quantities when permitted.
- One combined low-fuel notification covers all structures below 80 days, with at most one reminder per day. Clicking it opens Station Fuel. Unknown expiry is not treated as an empty fuel bay.
- Fuel expiry uses the existing corporation structure permission. Exact quantities require reauthorizing the moon reader with corporation asset scope and sufficient ESI permissions; the view explains missing access.
- Fuel data is refreshed with the existing moon polling schedule. Contract polling remains at least 30 minutes with provider cooldowns respected.
