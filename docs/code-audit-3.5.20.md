# Code audit and combat overview — v3.5.20

Review date: 15 September 2026.

## Scope and method

Repository-wide searches covered the tracked C# and XAML source (169 production files / approximately 67,451 lines before this release), looking for synchronous waits, timer overlap, untracked tasks, window creation/activation, native-resource disposal, stale async results, cache writes and notification state changes. Manual inspection focused on the critical paths below. The regression suite exercises the feature areas as well as the new combat views.

This is a risk-focused review with concrete fixes and tests, not a guarantee that every defect has been found. No long-running, multi-client EVE/DirectX soak was performed. Native rendering and switching-lifetime checks use temporary test windows and do not interact with EVE clients.

## Findings fixed

| Area | Finding | Change |
|---|---|---|
| Preview focus | Text/stat overlays acquired their non-activation styles in Loaded, after initial display. Repeated Loaded events could also add the stat-window hook repeatedly. | Set ShowActivated=false and install native styles/hooks in SourceInitialized, once per HWND. |
| Hidden previews | Creation called Show before applying hidden/combined-mode rules. | Create the DWM handle without showing the preview when suppressed; text-overlay visibility follows the preview. |
| Z-order | Hidden previews still received foreground/z-order maintenance. | Hidden/disposed previews and invisible stat overlays return without being raised. |
| Resource lifetime | Direct Form.Dispose could bypass the thumbnail's close-only cleanup. | Dispose calls the idempotent cleanup, releasing the DWM registration, timers and text overlay. |
| Log reader | A dedicated thread was represented by Task.CompletedTask; Stop did not wait for the running loop, and startup captured the mutable CTS field. | Track the actual asynchronous reader task and capture its token. Stop/restart checks verify completion. |
| Window discovery | Startup captured a mutable CTS field inside a queued task. | Capture the token before queueing. |
| Process monitoring | A three-second timer could overlap slow Windows GPU-counter queries; repeat Start calls could create more timers. | Single-flight poll guard, idempotent Start, shutdown guard and non-negative CPU readings. |
| Combat accounting | NPC damage was added to the bounty window as if it were ISK. | Only bounty events contribute to bounty calculations. |
| Stat retention | Pruning took one snapshot, drained another set of events and discarded concurrent additions. | Retain the events actually taken from the bag; bound each drain pass. |
| Pilot identity | Stat keys were case-sensitive. | Use ordinal case-insensitive keys. |
| PI notifications | Countdown text changes made already-read issues unread every refresh. | Use a stable issue/stage key; preserve read status for countdown-only changes. Refresh the title as issue counts change. |
| Industry scanning | Superseded scans still consumed CPU and read a mutable pilot snapshot. | Cancel superseded scans and plan against copied blueprint, material and skill collections; retain the existing generation check. |
| Pilot cache | Successful pilot snapshots were written directly to the destination files. | Write a temporary file and replace the previous cache atomically. |
| Combat parser | Oversized damage integers could throw. | Use TryParse and ignore invalid numeric data. |

The focus and show/hide fixes address plausible flash paths. The user's intermittent white flash was not reproduced in a running EVE client, so it is not claimed conclusively eliminated. A DirectX client can still expose a blank frame while Windows restores it; the app does not control that surface.

## Feature review coverage

| Feature | Paths reviewed / exercised |
|---|---|
| Character previews, client switching, hotkeys | Native activation path, independent input queues, hidden/combined visibility, DWM registration lifetime, snapshot capture worker bound, move/resize, mode changes, overlay focus styles. |
| Mining | Per-toon watchdog, independent rates and rock tracking, history indexing task lifetime, profit-data tests, rolling stat retention, capture stability. |
| Pilots and skills | Background snapshot refresh, selection-generation/cancellation guards, skill-planner readiness and prerequisite tests, fitting-cache fallback, shared authorization. |
| PI | Refresh scheduling and snapshots, production/refill projection tests, haul thresholds, grouped alert stages, notification read state, tax/profit calculations and divider persistence. |
| Industry | Job refresh and ready alerts, recipe scans, async selection races, material/skill snapshots, costs and profitability regressions. |
| Omega | Cached pricing/offer refresh, expiry-stage grouping, renewal budget and manually recorded status tests. |
| Moons and contracts | Cached access checks, reader authorization and transient-error handling, ledger recovery, approved destinations, contract history and announcement tests. |
| Shared infrastructure | ESI pacing/cache bounds and cancellation tests; credential-store lifetime; settings and cache writes; window placement; update/release-note matching; cloud backup path validation and exit behavior. |

## Combat display contract

- Modes are presentation choices; switching to PvE/PvP does not silently change alert settings or start game actions.
- Damage is actual logged damage divided by a fixed 30-second window, including NPC and player events. It is not fitted/paper DPS.
- PvE adds logged session bounty totals and the last observed weapon string. Bounties are not net profit.
- PvP adds incoming/outgoing remote repair HP/s, the largest incoming hit in the window and cached fitting EHP where available. Capacitor transfers are excluded from repair HP/s.
- Scramble/warp-block messages are timestamped observations. A 30-second highlight does not indicate the effect's true duration or prove the ship can now warp. Complete active EWAR/debuff status is not inferred.
- Ammo quantities and loaded scripts are unavailable in this view. Weapon text describes a prior log event, not proof of current ammunition or module state.
- Old log backfill cannot turn into fresh DPS or a new threat highlight. Per-pilot queues are bounded and isolated.
- Combat cards bypass the mining analysis path and perform no extra ESI requests.

## Remaining limits / follow-up

- Run an extended EVE session with the user's normal minimize/maximize, preview and multi-monitor settings to verify whether the flash recurs. The native test windows cannot reproduce every graphics-driver/client condition.
- Existing snapshot capture still depends on PrintWindow. Worker count is bounded, but Windows cannot safely cancel a hung PrintWindow call; live DWM mode avoids periodic overview capture.
- External ESI freshness limits, unavailable permissions and manually recorded Omega/POCO/cost inputs remain visible data limitations rather than quantities to guess.
- Cloud exit backup intentionally retains its existing bounded wait; this was not changed as part of switching fixes.
- Cached fit EHP is not live shield/armor/hull health. No current health, capacitor, ammo inventory or persistent debuff claims were added.

## Validation

- Release build: zero warnings, zero errors.
- Full feature regression and render run: 339 passing checks, including PvE/PvP card sizing and stale combat-event handling.
- Native desktop smoke run: 18 passing checks, including actual DWM source pixels, focus preservation, hidden-window behavior, cleanup and thirty live/snapshot toggles with no growth in native form count.
- Rendered character, mining, PvE/PvP and existing panel fixtures were inspected; release-note version validation and whitespace checks passed.
