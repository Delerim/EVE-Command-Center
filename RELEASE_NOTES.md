# EVE Command Center v3.5.10

- PI now projects launchpad contents and routed factory production from saved snapshots, consuming T1 inputs and producing T2/T3 through actual recipe cycles. Original snapshot amounts remain visible; projections are estimates and cannot see unreported hauling or changes in game.
- Factory refills show estimated inputs left, full targets, original quantities, stock allocation and remaining stock. A shared T1 budget shows container reserves, total needed, leftovers and shortages across all planned refills. Targets assume collection of finished output before refilling.
- Added per-toon T1 collection warnings across extracting planets: amber at 54,000 m3 and red at 60,000 m3. Alerts appear in Notices and use the existing desktop alert setting.
- Strengthened the independent one-second mining watchdog using each character's actual last pull timestamp. One failed check or notification cannot block the remaining toons; fresh pulls rearm only that character's alarm and mutes remain respected.
- Mining watchdog alerts are retained in Notices so simultaneous warnings are not lost when tray balloons replace each other. No extra ESI requests are needed for watchdog checks or PI projections.
