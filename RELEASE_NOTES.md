# EVE Command Center v3.1.4

- Extraction and factory output now occupy separate, simultaneously visible panels with an adjustable divider, replacing the Factory Summary tab.
- Factory output aggregates products across planets into colour-coded T1/T2/T3 groups (and T4 when present), with product icons, factory counts, configured hourly capacity, stored quantities and collection quantities.
- Products available to collect sort first. Stored intermediates routed to consuming factories remain reserved and are not counted as collectable; final output is counted once per storage pin.
- Capacity is not guaranteed actual throughput. Stored/collection amounts use ESI colony snapshots, not projected future production.

Validation: 135 checks, including tier grouping, recipe capacity, reserved intermediates and final-product collection totals.
