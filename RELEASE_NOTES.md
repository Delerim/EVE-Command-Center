# EVE Command Center v3.5.7

- Link a toon once for every current feature. All character-link and reconnect flows now request the complete permission set for Pilots, PI, Industry, Omega, Moons and Contracts.
- Added a central upgrade/reconnect selector in Settings. It shows which existing toons still need a one-time permission upgrade and which already have the full set saved.
- Moon and contract setup now select an existing reader without opening a separate feature authorization. EVE corporation roles still determine access.
- Successful linking schedules the other features to refresh through the existing paced background queue.

Existing limited connections need one reconnect per toon to approve the additional permissions. EVE consent cannot be added silently; after that, the same connection is shared across the current features.
