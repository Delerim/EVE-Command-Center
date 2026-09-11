# EVE Command Center v3.0.0

## Planetary Industry
- New PI button in the overview, with all-character colony status, extractor program and next-cycle timers, factory supply estimates, storage contents and projected nominal extraction totals.
- Link PI Permission for each character using EVE SSO. Existing selected-character scopes are requested alongside the PI scope.
- Select any stockpile character and then its container; no hardcoded character or container. Nested contents are included, unrelated station cargo excluded.
- Factory refill plans use bundled CCP schematics, actual launchpad capacity, existing cargo and directly routed inputs. Shared stock is allocated only once across the plan.
- Background refresh is paced through the shared ESI queue every 30 minutes, with saved snapshots retained. Timer displays advance locally.
- Colony amounts depend on EVE's last colony recalculation. Production and input-runway estimates are clearly distinguished from observed contents; future routed production is not assumed.

## Mining rates
- Fixed critical bonus log entries contributing normal yield to BASE. EVE logs normal yield and additional critical yield separately: bonus lines now add only to REAL.
- Completed-interval averaging replaces the guessed endpoint interval that could inflate rates for nearby multi-miner pulls. The window targets the latest 90 seconds, using up to two minutes of log samples. The tooltip displays the measured span.
- Mining rates include logged drone mining as well as strips. Two strips at 540 m3 / 24.1s contribute 44.81 m3/s before drones or critical bonuses.

## Pilot loading
- Saved pilot summary cards remain visible while refreshing. Pilot data requests have bounded waits and report timeout/cancellation instead of staying indefinitely in Loading.

Validation: 118 checks passed, including PI recipe capacity, nested stock isolation, shared-stock allocation, expired extractor programs and mining critical/endpoint regressions. PI overview visually rendered; live colonies require each user's PI authorization.
