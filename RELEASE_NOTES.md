# EVE Command Center v3.6.60

- ESI-first moon discovery works without a CSV; private CSV is an optional local overlay for friendly names, moon composition, wastage and field-lifetime profiles.
- Moon Overview now separates recently mined fields from unverified historic field estimates, without inventing field presence from a later scheduled extraction.
- Despawn Audit includes an estimated loss column for each R4 ore and retains expandable ore cards.
- Station Fuel displays estimates of how many fuel blocks each station needs to align with the latest ESI fuel expiry, with explicit evidence/unknown labels.

## Moon discovery and privacy
- Uses authorised ESI corporation structures, extractions and public moon IDs/names to discover available moons. Unseen idle moon identity is labelled as unidentified until extraction is available.
- New users do not receive any bundled corporation moon map. Existing private CSV remains on the local machine; no moon data, mappings or passwords are embedded in source, test fixtures, or release artifacts.
- Re-import updates private overlays while preserving ESI-discovered moons, previous mining records and extraction history. Names and R4 composition supplied by CSV remain local.
- ESI refresh and inherited reader-scoped state distinguish confirmed ESI discovery from legacy persisted records.

## Accurate field reporting
- Future extraction schedules do not automatically generate fabricated prior field records.
- Recently mined ledger-associated fields appear as active; historical/estimated inferred fields are classified separately. ESI does not disclose live asteroid-rock presence, so this remains evidence-based and not a live scan.
- The overview shows a recent-mining count rather than counting every estimated field as currently active.

## Despawn and fuel planning
- Despawn Audit now has Zeolites, Sylvite, Bitumens and Coesite estimated-at-expiry columns. Expand (+) still opens the per-ore detailed cards. Unknown/no-profile outcomes are labelled instead of giving false precision.
- New fuel alignment panel targets the latest valid station fuel expiry using exact UTC timestamps. It estimates per-station block consumption only from fresh, internally consistent ESI fuel-bay quantities and expiry data, with explicit safeguards for unknown rates or missing fuel blocks. Top-ups are rounded up and must be confirmed in-game before adding fuel.

## Quality and compatibility
- Retains the normal combined/separate overview control, desktop notifications, Discord alerts, updater integration and safe local persistence.
- Regression coverage added for ESI-first drills, false-positive active fields, fuel top-up calculations, varied rates, missing data and fractional rounding.
