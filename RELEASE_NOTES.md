# EVE Command Center v3.6.38

- Added an embedded Production Queue to Industry with durable draft projects, expandable component trees, project notes, pause/archive controls and an audit trail.
- Added configurable linked-character lead, buyer and seller defaults plus planning role preferences. Component executors remain independent of the lead; reassignment never fragments the parent project.
- Add a draft from the existing manufacturing recipe planner, or search product/component names. This first increment uses explicitly planned quantities; it does not claim to calculate a complete recursive material bill.
- Select matching cached personal inventory by owner, stack ID and nested location/container path. Shared reservations prevent the same observed stock being allocated across projects twice.
- Stale, moved, missing or insufficient stock needs review. Reservations do not imply transfer, delivery, manufacturing readiness or consumption.
- Added versioned planning persistence with atomic replacement, last-good backups, explicit recovery and future-schema protection.
- Added regression checks for project restart, delegation, shared stock, stale snapshots, containment, source selection and persistence recovery. Market imports, purchase attribution, corporation inventory and full production accounting remain later phases.
