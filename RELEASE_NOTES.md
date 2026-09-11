# EVE Command Center v2.8.0

## Expanded moon overview

- Select an active field or upcoming extraction to expand its ore breakdown, with small EVE icons, remaining volume and percentage bars, estimated value and ISK per hour.
- Gold highlights the most valuable remaining ore per cubic metre. Adjust the assumed mining rate in the overview to compare income estimates.
- Values use cached ESI average market prices, not guaranteed Jita sale prices. Remaining ore is estimated from saved composition, pull duration, mining ledger and waste; it is not a live asteroid scan.
- Small miner portraits accompany the field ledger. Changing systems clears unrelated field details.
- Summary tiles distinguish active fields, ready chunks and historical despawn audits instead of presenting historical estimates as fields currently in space.

## Preview settings

- Added a clearly labelled Preview Settings button to the overview bar and settings hub.
- Restyled the full settings window with Command Center's dark teal colours, rounded panels and matching buttons while retaining its existing controls and handlers.
- Profile toolbar buttons wrap when space is limited.

## Buyback history chart

- Added a Buyback Report tab with week, month and year views, calendar selection, previous/next and today navigation.
- Date input and the day/month/year calendar use the matching dark teal theme, with selected-day and today highlights.
- Bars show accepted contract prices and counts, grouped by UTC acceptance date. Weeks start Monday.
- Includes completed incoming item exchanges with Janice links for the selected corporation. Coverage depends on available ESI history and the local archive; missing history does not prove there was no activity.

## Provider request limits

- Contract refreshes now wait at least 30 minutes and respect longer ESI cache expiry or provider Retry-After delays. Manual refresh and restarts preserve the cooldown.
- Batch and persist issuer/acceptor name lookups instead of repeatedly probing character and corporation endpoints for every historical contract.
- Saved contract data remains visible when a refresh fails. Notifications arrive when the next successful poll observes the change; they are no longer checked for fresh contract data every minute.
