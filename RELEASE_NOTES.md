# EVE Command Center v3.5.23

- Fixed false implant re-link prompts when data is pending or an implant lookup fails. Permission status is stored separately from data availability; old snapshots show pending verification instead of claiming missing authorization.
- Pilot refresh reloads the saved character profile so permissions granted through another window are used without reopening Pilots.
- Missing attributes display pending refresh instead of 0 SP/min and 100% alignment; no off-map warning is inferred without attribute data.
