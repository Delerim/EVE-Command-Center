# EVE Command Center v3.6.8

- Reverted the experimental v3.6.7 embedded-window lifecycle after it could black out a secondary monitor when opening Command Center pages on multi-monitor systems.
- Restored the proven v3.6.6 off-screen Show/Loaded/hide host path, preserving normal tool initialization and multi-monitor behavior.
- This intentionally accepts the small first-open flash on some legacy Window-based modules until those tools are converted to reusable embedded controls instead of top-level Windows.
