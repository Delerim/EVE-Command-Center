# EVE Command Center v3.6.11

- Added Discord-style live ISK/hr telemetry to every active miner on the Command Center landing dashboard, including the pilot portrait, current ore, observed m3/s, value rate and pull age.
- Added the same live rolling ISK/hr figure to every mining card in Character Overview without removing the existing session PROFIT value.
- Reused the existing Best ISK/hr estimator and enabled-market quotes, so this patch changes presentation only; mining ledger, JSONL persistence, history rebuilding and rate calculations are unchanged.
- Added regression coverage for the compact live ISK/hr presentation.