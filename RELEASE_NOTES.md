# EVE Command Center v3.0.2

- App-owned background refresh keeps pilot summaries and fitted ship data updating even when their windows are closed. Successful snapshots survive refresh failures and restarts.
- Contracts target five-minute checks and PI ten-minute checks. Moon refresh follows ESI cache expiry, including hourly corporation mining ledgers. The shared queue still respects server cooldowns, cache expiry and request budgets.
- Independent refresh jobs prevent a slow contract or permission request from blocking other monitoring.
- Each mining observer saves its successful ledger separately. Failed observers retain previous data and retry; Moon status reports ledger freshness and pending retries.
- Fixed cached responses without an Expires header repeatedly postponing their next refresh deadline.

Monitoring requires Command Center to remain running. ESI caching and rate limits can delay new data; local estimates do not force the provider to update.

Validation: 121 checks passed.
