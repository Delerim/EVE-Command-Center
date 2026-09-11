# EVE Command Center v3.1.2

- PI factory health now follows routed upstream production through factories and storage. T3 factories awaiting supplied T2 output show Waiting for upstream production instead of Check inputs.
- Missing routes, missing recipes and exhausted upstream supplies still require attention. Dependency loops cannot invent a healthy supply source; future output is not counted as stockpile inventory or an exact completion time.
- Planet cards now include small EVE planet-type icons and planet-type labels in both Overview and Factory Refills.
- Includes v3.1.1 compressed Jita pricing corrections for Moon ledgers, remaining-ore values and ISK/hour estimates.

Validation: 130 checks passed, including supplied T2-to-T3 routes through storage and exhausted-chain detection. Planet icon endpoint verified.
