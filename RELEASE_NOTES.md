# EVE Command Center v3.6.12

- Character Overview and tray tools now open reusable Command Center tabs, including Mining, Pilots, PI, Industry, Moons, Contracts, notifications, settings, setup, cloud, skill plans and fit details.
- Prevented saved window placement from moving hidden embedded backing windows on-screen during navigation. Cancelled tab closes now preserve their content, and restoring Command Center respects its previous normal/maximized state.
- Reduced hidden dashboard, PI, Industry, Omega and overview rendering; prevented reentrant overview refreshes and duplicate detail-window initialization.
- Simplified client focus handoff, delayed taskbar-cover promotion until focus is accepted, and suspended live thumbnail registrations for unresponsive clients with recovery when they respond again.
- Added today's fleet mining value beside PLEX, including offline pilots, with missing-price indication and a tooltip explaining the pre-tax estimate.
- Added embedded-window lifecycle, cancelled-close and valuation regression checks; extended native live/snapshot cycling to 200 switches. Real EVE multi-client soak testing is still needed to assess long-session game/driver hangs.
