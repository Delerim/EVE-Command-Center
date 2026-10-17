# EVE Command Center v3.6.57

- Added a dedicated Alerts & Discord section with configurable, grouped planetary-industry notifications for each Discord destination.
- Fixed Combined Character Overview overlays reappearing on focus changes and improved Command Deck navigation with distinctive, labelled controls.
- Preserved existing desktop alerts, saved encrypted webhooks and the two-run build/test-first publishing workflow.

## Alerts and Discord
- Added an **ALERTS & DISCORD** entry in the Command Center navigation. It opens the existing Alerts settings directly, combining desktop alert controls with Discord webhook configuration.
- Added three opt-in, independently selectable Discord planetary-industry alert types for every webhook destination: **T1 collection threshold**, **factory-world refill required**, and **extractor inactive for over five hours**.
- T1 collection messages use the existing, per-pilot volume stages: **40,500 m³** (collect soon) and **45,000 m³** (haul limit). Each threshold is summarized once per pilot rather than sending one message per planet.
- Factory refill messages group factories awaiting inputs by pilot. Extractor inactivity alerts require an expired or missing program for more than five hours, and a recent successful colony fetch. Planet names are included in the grouped message for follow-up.
- Discord alert types are selectable independently per saved destination; duplicate delivery suppression and re-arming when a condition clears remain persistent. Detailed per-planet items remain available in the in-app notification centre. Existing desktop PI alert preferences are unchanged.
- Moon drill alerts still notify when **no extraction is scheduled**. Moon fracture and field-ready events are **not** sent to Discord.

## Character overview and Command Deck
- Prevented legacy individual stat overlays from resurfacing during Combined Character Overview when client focus changes or the Home icon opens Command Center. Independent PiP thumbnails keep their existing behavior.
- Command Deck's overview-settings buttons now show meaningful **icons and visible labels**, not near-identical icon-only buttons. Command Center navigation also retains a left accent on the selected section independently of hover. Home continues to bring the Command Center dashboard forward without changing the combined miner cards.

## Safety and compatibility
- Existing Discord webhook URLs stay encrypted with Windows user-scope DPAPI; no credential migration or re-entry is needed.
- No additional ESI scope is needed. Alert messages use existing PI snapshots and are suppressed when the colony fetch is stale or failed.
- v3.6.57 uses the established **two-run, single-script** release procedure: initial run validates in an isolated checkout; `--publish` copies only validated files, commits, pushes and tags for the existing GitHub release workflow. Do not tag if validation fails.
