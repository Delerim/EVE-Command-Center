# EVE Command Center v2.9.4

- Despawn audit now uses compact totals, colored outcome badges and expandable ore cards with small EVE icons and remaining-volume bars. Unknown outcomes are not presented as reliable estimates.
- Settings includes optional ESI debug logging and an Open ESI Log Folder button. Logs include request paths, cache hits, queue delays, response codes, rate-limit headers and network timeouts, without authorization headers or response bodies.
- Debug logs rotate at 2 MB with two backups (roughly 6 MB maximum).
- Settings displays current ESI activity while working. Each permission probe allows 45 seconds before reporting delayed verification; recently verified access remains available.
- Reselecting the current Moon reader no longer waits behind a background Moon refresh.
- Fixed missing ORE Mining Director Mindlink recognition in Orca shield command boost estimates. This applies its shield bonus when the implant is reported by ESI.

Validation: 105 checks passed; expanded audit visually rendered and checked.
