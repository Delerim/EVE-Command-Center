# EVE Command Center v3.2.0

- Added Industry Command Center with pilot cards, manufacturing/research/invention dashboards, live job finish times, blueprint/run availability and recipe/material planning.
- Bundled 15,731 published CCP blueprint activities. Scan owned or all recipes without extra API calls; inspect required, owned and missing materials, skills, BPO/BPC efficiency and remaining copy runs.
- Selected-recipe Jita quotes show missing-material purchase estimates, all-input replacement value and output sell-listing value. Prices use Jita sell orders; installation, taxes, hauling, market depth and facility effects are excluded. Manufacturing time applies blueprint TE and core Industry skills; other activity times and invention probabilities are explicitly base values. BPC output is not assigned an ordinary market price.
- Industry jobs refresh in the background via the paced ESI queue. Ready-job desktop alerts are grouped per pilot and deduplicated across restarts. Link Industry Permissions for the required personal scopes.
- Added grouped PI desktop alerts for transitions to extractor restart/attention and collection/refill. The initial snapshot is a quiet baseline, with saved deduplication and a Desktop Alerts toggle.
- Added Omega before Mining: optional manually recorded expiry dates and account groups, plus home/jump-clone data after linking clone permission. ESI does not provide account subscription expiry or verified Alpha/Omega status; manual/unknown fields are clearly labelled.

Validation: 152 checks passed, covering industry material math, BPC runs, occupied blueprints, excluded fitted assets, job notifications, PI alert persistence and Omega unknown-state handling. Industry and Omega window renders inspected. Live industry/clone data requires user SSO authorization.
