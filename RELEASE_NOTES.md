# EVE Command Center v3.1.1

- Moon ledger valuations now use Jita 4-4 compressed-ore best-buy prices, replacing raw-ore ESI average/adjusted prices. Exact ledger ore variants retain their own quotes.
- Existing saved ledger estimates are recalculated from their original quantities when quotes refresh, so Moon ledger totals and field-ledger rows use the corrected basis without reimporting history.
- Remaining-ore value, ISK/hour and best-value highlighting use the same compressed-price basis per raw mined m3. Family-level remaining estimates use the most-mined observed variant, or base ore until observed; this assumption is labelled.
- Old raw-price caches are invalidated. Missing compressed quotes are reported as partial totals rather than silently falling back to raw prices. Quote timestamps are displayed; successful quotes refresh hourly through the paced ESI queue.
- Values are current market estimates before fees and order depth, not historical sale proceeds.

Validation: 128 checks passed, including exact-variant repricing, unchanged mined volumes and revaluation of saved records.
