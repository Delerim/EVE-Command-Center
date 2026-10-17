# EVE Command Center v3.6.59

- Removed the bundled corporation moon catalogue from the application and replaced automatic loading with a private CSV-only setup.
- Moon Management, moon schedule and calendar now remain empty until the user imports a moon list.
- Added DOWNLOAD CSV TEMPLATE, clear import guidance and name-based matching for CSVs without ESI moon IDs.
- Show uploaded friendly moon/drill nicknames in moon cards and the schedule/calendar instead of generic moon numbers.

## Private moon management
- No preconfigured corporation moons ship with the Windows executable or the new source tree. Existing local moon-report state is not treated as consent to expose a list; an explicit import is required.
- IMPORT MOON SETUP accepts the documented CSV format, including rows with MoonId=0. A recognized MoonName is matched to the live ESI moon as soon as the extraction becomes identifiable.
- An optional StructureId column can link an idle drill without waiting for a visible extraction. The app does not automatically add unrelated corporation structures returned by ESI.
- Imported StructureName is your private label shown on cards and calendar entries; the EVE moon name remains the stable matching key.
- Re-import replaces the managed list and prevents unrelated legacy entries from reappearing. Private settings continue to be saved locally per user.
- Downloaded example template uses fictional sample data only; actual private moon CSVs are never embedded in this release, source or GitHub artifacts.

## Privacy and compatibility
- Removed the old automatic LSE audit loader and legacy bundled-audit tests. No new ESI scopes are required.
- Existing saved raw local moon-report history is not published or uploaded. The first CSV import makes a private local backup of the prior moon-report state before applying the whitelist. The app displays no Moon Operations inventory before that import.
- Previously committed source and historical releases may still contain the old catalogue; removing it in this release does not erase Git history. Clean up history before making the repository public.
- Includes regression coverage for empty-by-default moon management and name-only private imports. Windows build and regression tests remain mandatory before publishing.
