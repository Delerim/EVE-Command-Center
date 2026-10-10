# EVE Command Center v3.6.42

- Production Queue now opens on project cards with item icons. Open a build to work with its component tree, or choose New project to browse owned blueprints by pilot and container. The blueprint library is no longer a permanent component tab.
- Moved activity filters inside Jobs. Manufacturing, research and other job filters no longer affect Production Queue or the blueprint planner.
- Rebuilt the component inspector around full-height input and material tables. Stock and reservations have a draggable divider; blueprint and job tables have height handles. Pane widths, table heights, column sizes and column order are remembered.
- Added material icons, grouped quantities and subtle stock colors. Consolidated materials show owned, free and all reserved stock first, separately from reservations for active builds. Unknown inventory is labelled unverified rather than presented as zero.
- Added component blueprint and skill indicators. Available fleet copies are identified before assignment; assigned-pilot checks distinguish trained skills, missing requirements, insufficient blueprint runs and stale or missing data. Facility access and hauling remain separate checks.
- Replaced exposed identifiers in project history, job evidence, blueprint tables, market locations, clone details and settings labels with readable names or an explicit unavailable state. Internal identities and original audit evidence are preserved.
- Omega clone cards now show station names and named implant icons. Market and planetary containers also resolve location names through the existing ESI infrastructure; inaccessible names retain the last known value where available.
- Verified project creation, selection, deletion, job linking, layout persistence and compact layouts. This release refines the current planning workflow; later reconciliation and financial phases remain pending.
