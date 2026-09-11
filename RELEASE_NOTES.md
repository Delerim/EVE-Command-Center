# EVE Command Center v3.1.3

- Added Factory Summary alongside Extraction in the adjustable Overview summary panel: per-planet factory counts, collection/refill counts, status and stored output snapshots.
- Correctly routed factory runs with exhausted inputs and evidence of prior production now show blue Collect / refill, rather than Needs attention. Factory cycles still finishing are not flagged as complete.
- Missing recipes/routes and empty factories without evidence of a started run retain attention status. Healthy upstream production remains a normal waiting state.
- Pilot and planet summaries expose collection readiness separately from attention. Readiness is estimated from ESI snapshots; stored intermediate products may still be required by downstream factories.

Validation: 132 checks passed, including completed-run collection status and per-planet factory summaries.
