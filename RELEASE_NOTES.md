# EVE Command Center v3.6.19

- Active-client highlighting now follows foreground-window events and updates immediately after a successful client switch, without waiting for the mining refresh.
- Replaced the inset green frame with a single outer highlight, keeping mining text clear and preserving alarm colors.
- Added explicit Live and Snapshot preview choices. Existing configurations switch to Live once on upgrade; subsequent choices are remembered.
- Live mode uses continuous Windows DWM thumbnails, the same rendering technology as separate previews. Minimized or unresponsive clients still require a fallback.
- Verified continuous preview pixels with the placement timer stopped; 383 regression checks and 21 native preview checks passed.
