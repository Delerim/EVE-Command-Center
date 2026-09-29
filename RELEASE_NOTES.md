# EVE Command Center v3.6.17

- Added a Drill Schedule tab to Moon Operations with a searchable table of drills, service state, extraction start and arrival in UTC, total duration, elapsed time and remaining time.
- Highlighted unset drills in amber and placed them first. Running, future-scheduled and ready-to-fracture extractions have distinct statuses; missing structures and unchecked data are not treated as confirmed unset drills.
- Timers refresh locally every 30 seconds while the tab is active. The view uses existing ESI snapshots and permissions; the last successful check remains visible.
- Added schedule regression tests covering idle drills, historical pulls, future starts, ready chunks, durations and missing data.
