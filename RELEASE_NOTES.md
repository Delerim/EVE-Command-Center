# EVE Command Center v2.9.2

- All ESI services now share a paced request queue, including fits, skills, moons, contracts, market data and permission checks.
- Startup builds data gradually. Waiting for a queue slot no longer consumes the network timeout.
- ESI cache expiry is respected and duplicate GET requests reuse cached responses, with authentication isolation and pagination headers preserved.
- Advertised rate-limit groups are paced at 70% of their published successful-request budget, with a conservative global request floor and extra slowdown near exhaustion.
- Retry-After and low error-budget responses pause ESI traffic. Repeated denied or throttled reads are briefly cached to prevent retry storms.
- Existing cached pilot data remains available while fresh data loads. Contract monitoring retains its 30-minute minimum interval.
- Includes v2.9.1 live release prompts: checks every five minutes while running, with changelog, Update & Restart, and Skip & Continue.

Validation: 98 automated checks passed, including cache isolation, duplicate requests, shared cooldown, cancellation and group pacing.
