# EVE Command Center v3.6.10

- Retired fully consumed EVE session logs after a newer session for the same character and log type is established, so repeated relogs no longer expand the live polling set. Raw EVE logs, current-day mining persistence, and mining-history rebuilds are unchanged.
- Added dead-HWND admission checks around queued, batched, and deferred preview creation so a client that exits during discovery cannot leave orphan primary, PiP, or stat windows.
- Coalesced foreground and minimize border refreshes so rapid focus changes cannot queue redundant full preview sweeps behind a busy UI dispatcher.
- Added regression coverage for safe session-log retirement and dead-window preview admission.
