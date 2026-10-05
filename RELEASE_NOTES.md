# EVE Command Center v3.6.31

- Changed the bulk alarm control to update each detected non-Orca client's alarm switch directly, instead of layering a global mute over them.
- All on enables every targeted client, including previously muted clients. Individual switches remain usable after a bulk action.
- Removed disabled-button styling that turned alarm buttons and Orca drone indicators white.
- Client alert delivery now respects the same per-character mining alarm mute state. Orca drone suppression remains unchanged.
