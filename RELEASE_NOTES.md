# EVE Command Center v3.1.0

- PI overview groups colonies into collapsible pilot summaries, including factory-world counts and overall attention. Planet cards show command-centre level, extractors, heads, products and expandable facility details; the separate colony-details tab is consolidated into Overview.
- Expansion choices survive timer refreshes. The extraction summary retains its adjustable splitter.
- Stockpile items are ordered by tier and name, with sale stock and factory feed colour-coded. Jita 4-4 best-buy unit and total estimates include quote timestamps and refresh hourly through the shared ESI queue; fees and market depth are excluded.
- Factory refills group T1 hauling targets by pilot and planet, showing combined requirements, stock allocation and shortfalls. Higher-tier inputs are hidden from this hauling view without changing capacity calculations.
- Correctly routed, active extractor supply keeps waiting basic factories healthy, even when extraction cannot keep all factories continuously running. Missing recipes or routes and exhausted inputs without active extraction remain flagged.

Validation: 125 checks passed, including grouped factory-world classification and active extractor supply through storage. Overview render inspected.
