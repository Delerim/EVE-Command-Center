# EVE Command Center v2.7.0

## Contract history and acceptance alerts

- New History and Acceptors tabs show recorded contracts, statuses, acceptance dates, reported acceptors, counts and total prices. Records remain archived when they disappear from later ESI responses.
- Notify once when an observed contract is accepted, including contracts created and accepted between polls when ESI supplies a recent acceptance date. Initial historical imports do not flood alerts.
- Acceptor identities come from ESI. If it reports a corporation, the app cannot identify the pilot acting for it.
- Contract scheduling now checks each minute instead of every 30 minutes, respecting ESI cache expiry and rate-limit delays. This does not guarantee fresh data every minute.
- Slow moon refreshes no longer hold up contract scheduling.

## Moon and Glistening alerts

- Notifications when a moon is ready to fracture or an active field is newly detected.
- Gold Glistening notifications from recent live mining logs, followed by moon-specific confirmation when Glistening ore appears in the corporation ledger.
- Persistent milestone deduplication avoids repeated alerts after restart. Initial moon history is baselined quietly.
- Removed the test-alert button. Notifications continue while views are closed or minimized and Command Center is running.

## EHP and presentation fixes

- Shield Harmonizing now shares per-damage-type stacking penalties with fitted hardeners and amplifiers. It no longer applies an unpenalized resistance bonus after the fit calculation.
- Damage Control remains outside that penalty group and only applies when fitted. Fit diagnostics explicitly show absent Damage Control and the number of mining laser upgrades.
- EHP still uses cached ESI fit data and assumes active fitted hardeners and manually enabled fleet boosts are affecting the pilot.
- Fixed broken separators in setup and notifications; refreshed the gold alert layout and contract history styling.
