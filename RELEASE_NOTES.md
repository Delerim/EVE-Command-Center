# EVE Command Center v2.6.0

## Setup and corporation access

- New setup screen links personal pilots and optional holding-corporation moon and operating-corporation contract readers. Existing installations see it once.
- Live ESI extraction and structure reads validate moon access; a live corporation-contract read validates contracts. Unverified views stay hidden. Checks repeat every ten minutes.
- Temporary ESI failures hide the affected view until verification succeeds. Rank labels and scopes alone never grant access.
- Personal linking no longer requests corporation scopes. Use Settings to authorize corporation readers.
- A denied mining ledger read no longer blocks permitted structure and extraction data; ore estimates display a warning.

## Overview navigation

- Mining, Pilots, Moons and Contracts are grouped together, followed by Settings, Cloud and Order Clients.
- Header measurement prevents controls and the close button being clipped with a single pilot.

## Moon operations

- New opening overview with system selection, active fields, upcoming extractions, the next system in the schedule, estimated R4 ore remaining and a selected-field ledger.
- Recent extraction restarts recover inferred previous fields without waiting for mining activity. Their old pull duration is estimated from the new extraction and labelled accordingly.
- Natural fracture is no longer treated as asteroid-field despawn. Fields remain active for their configured estimated lifetime after fracture.
- Upcoming counts include refineries with a previous field still active. Freshness shows the last successful ESI refresh.
- Switching readers preserves separate archived moon histories. Monitoring continues while windows are closed or minimized and Command Center is running.

Ore remaining and field lifetimes are estimates, not live asteroid scans.
