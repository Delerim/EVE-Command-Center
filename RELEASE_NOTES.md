# EVE Command Center v3.5.6

- Fixed corporation views disappearing after reconnect: access checks now reload saved permissions instead of using stale scopes held by an open panel.
- Reconnecting a selected Contracts, Moons, PI, Industry or Omega character now requests its existing permissions alongside the new feature scopes.
- Added a guard against overwriting an existing character connection with fewer permissions or with a different character than selected.
- Pilot profile files are replaced atomically so concurrent permission checks cannot read a partially written file.

If Contracts was hidden by the stale check, open Settings and use Verify Access after updating. If EVE has actually removed a permission, authorize that reader again; the app does not bypass EVE permission checks.
