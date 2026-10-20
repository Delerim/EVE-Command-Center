# Mining rings and holds - v3.6.80

Open Character Overview and select Mining. Each fitted laser gets a compact inferred-stream ring; only a stream whose learned pull deadline has passed turns red. Ring A/B ordering follows learned yield, not module slot order. The countdown and arc are estimated. Hover for timing/confidence. Ambiguous pulls, missing fit data, and a new ship do not assert a stopped module. Orca drones have no strip-miner rings. Existing ship-level alarm sounds remain separate.

The mining hold is directly beneath the rings; Stable/Idle and alarm controls stay at the bottom. Personal cargo is omitted. Vertical mode uses smaller stream indicators and the same hold bar, with scrolling when needed.

## Exhumer hold

Hulk, Skiff and Mackinaw use hull dogma mining capacity. Mackinaw additionally applies the active Mining Barge and Exhumers levels using the hull's bonus percentages (attributes 3187/3198). Current public ESI data inspected on 20 October 2026 has base mining capacity 11,500 / 18,500 / 31,500 m³ respectively. These amounts are not hardcoded in production. At both level V, the current Mackinaw formula gives 44,296.875 m³.

The amount prefixed with ~ is a session estimate updated from live local logs. Mined units include critical yield but exclude residue. Successful compression replaces raw volume with compressed volume using resolved type data; other pilots' compressor-service notifications are ignored. A full notification anchors used volume to capacity. Ship changes reset the estimate. Old log lines before the session or more than 30 seconds late are not replayed into it.

Opening contents are unknown; the initial baseline is zero until anchored. Unlogged transfers/unloading cause drift. Right-click the hold and choose Mark mining hold empty after unloading. Full and empty anchors discard earlier accumulated events. Unresolved volumes display Waiting for volumes, never a confident empty amount. An event bound also forces unknown if exceeded. These estimates are intentionally not persisted across app restarts, because off-session transfers are unobserved.

## Orca fleet hangar

Uses existing personal asset snapshots and direct FleetHangar contents only, excluding nested contents to avoid double counting. Singleton/packaged volumes are distinguished. Missing ship identity, unresolved types and old snapshots remain explicit; the bar says Cached or Old. Fleet-hangar transfers are not inferred from mining yields or compressor service messages. This feature adds no authentication scopes and never controls the game.

## Verification

Run `dotnet run --project Tests/CommandCenter.Checks.csproj -c Release -- --audit-mining-holds <image-path>` for deterministic log/capacity/stream checks and a WPF fixture render. Full regression checks are also required. Fixtures are not live-client proof. English compression/full messages are currently recognised; unsupported client languages need verified message patterns before claiming equivalent hold tracking.

The supplied reference was inspected statically to understand log-derived behaviour. Its GUI, controls, tokens and operational databases were not imported or executed.

## v3.6.81 follow-up

Orca now also has a log-driven mining-hold estimate. Industrial Command Ships adjusts the hull capacity using dogma attribute 3212 (currently 5% per level); with base 150,000 and level V this is 187,500 cubic metres. The fleet hangar retains its separately labelled cached source. Shared grid rows align cards and their bottom controls. Capacity is delivered before slower fitting/defence work finishes.
