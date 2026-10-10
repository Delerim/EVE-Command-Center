# EVE Command Center v3.6.39

- Production Queue retains the shared Industry header, pilot cards, refresh controls and activity summary alongside Jobs. Reorganized the workspace into blueprint library, project/component breakdown, consolidated materials and setup pages, with themed tables, icons and sourcing colors.
- Added an owned blueprint-instance library with toon and station/container filters, search, ME/TE, remaining BPC runs, instance IDs and snapshot/job availability. Container and station IDs remain visible where custom names are unavailable.
- Create a production plan from a specific blueprint and automatically expand manufacturing components and their material inputs. Calculates whole runs, batch material rounding and surplus output; preserves blueprint identity and a catalog content hash.
- Selected root blueprint ME applies to the batch. Child recipes explicitly use ME 0 with no assigned instance; facility/rig bonuses and reactions/invention are excluded. These are planning estimates, not confirmed job readiness.
- Prevents insufficient or double-planned BPC runs, recipe cycles and edits that would leave calculated child quantities inconsistent. Existing one-node drafts can be expanded, including correcting a blueprint type into its manufactured product, without replacing existing component plans.
- Added consolidated required/reserved/unreserved materials and copyable output across active projects. Buy / Use Stock choices stop child manufacturing demand; paused projects retain stock reservations. Uncertain stock requires review.
- Added Delete Project with inline confirmation, reservation protection and retained audit history.
- Added regression coverage for Obelisk component expansion, blueprint/container filtering with 2,100 instances, shared materials, BPC limits, persistence and deletion. Job matching, transfers, corporation inventory, child blueprint selection and market/financial imports remain later work.
