# Runtime and navigation audit - 3.6.12

Baseline: v3.6.11, commit a9d5bce. This is a code review and regression-tested repair of navigation, preview switching, refresh scheduling and window ownership. It is not evidence that every feature is defect-free or that a multi-hour EVE client hang has been reproduced and eliminated.

## Findings and repairs

| Finding | Repair |
| --- | --- |
| Overview/tray launchers created separate operational windows alongside dashboard tabs. | Route launchers through one cached Command Center module host. Remove redundant standalone window fields and settings launch implementation. Detail views use lazy factories so reopening a tab does not allocate unused windows. |
| Global saved-placement restoration could reposition an off-screen embedded backing window during its Loaded event. | Mark embedded windows before initialization and exclude them from layout restoration/persistence. Keep the real Loaded lifecycle and transfer content/resources into the tab. This addresses a code-level cause of transient black/extra windows. |
| Closing a tab detached its view before Closing could cancel. | Wait for Closed before removing its visual content and tab. |
| Embedded Settings could save the backing window size as its standalone size. | Skip standalone size persistence for embedded settings. |
| Command Center was forced maximized when opened/restored. | Respect saved placement and remember the last normal/maximized state. |
| Hidden pages continued rebuilding controls. | Defer hidden dashboard/PI/Industry/Omega UI work, refresh on visibility, and pause hidden/minimized overview updates. Keep background monitoring intact. Guard overview refresh reentry. |
| Focus handoff used extra cross-process focus calls and promoted client z-order before knowing whether focus succeeded. | Use guarded foreground activation; promote taskbar-cover only after the target is actually foreground. Exclude invalid/hung character-selection targets. |
| Standalone live previews retained registrations for unresponsive sources. | Check source responsiveness once per second, sharing each result across that client's previews. Release DWM registration while unresponsive, display a cached frame if available, and restore registration after recovery. |
| No fleet daily value beside PLEX. | Sum the existing mining-day ledger using enabled-market pricing, including offline pilots. Mark missing quotes and describe the result as estimated gross value, before costs and taxes. |

## Consolidation boundaries

Operational tools and detail views use Command Center tabs. Character Overview, live preview overlays and crop selection remain separate by design. First-run setup, small modal editors, confirmations and native file pickers retain their dialog behavior. These are not duplicate operational dashboards.

The older WPF ThumbnailWindow files remain in the repository but are explicitly excluded from compilation; the active preview implementation is WinForms ThumbnailWindow.cs. Their presence is not two running preview engines.

## Verification

- Final regression suite: 351 passing checks, including embedded Loaded-once behavior, hidden backing windows, inherited context, placement exclusion, 100 cached tab switches, cancelled close, reopen and disposal.
- Native preview suite: 20 passing checks, including 200 live/snapshot switches, source suspend/recovery, geometry/visibility and native-window cleanup.
- Render-enabled regression run before the final cancelled-close addition: 391 passing checks. Inspected the Character Overview and the new full mining header screenshots.
- Fleet valuation tests cover aggregation, missing prices and an empty mining day.
- These are local synthetic/native fixtures, not a live fleet soak test. Existing domain regression tests run as part of the suite; mining ledger and PI/industry calculations were not rewritten.

## Remaining uncertainty and follow-up validation

Windows PrintWindow is a synchronous native operation whose completion depends on the target application. Existing snapshot capture is bounded to two background workers; the bound protects the UI from waiting on capture but does not cancel a blocked native capture or prove the game/graphics driver cannot hang. See [Microsoft PrintWindow documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow).

SetFocus operates on windows attached to the calling thread's message queue; redundant foreign-thread focus attempts were removed. Foreground activation can still be denied by Windows, in which case the app must not pretend the switch succeeded. See [Microsoft SetFocus documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setfocus).

A representative multi-hour EVE session is still needed: switch repeatedly with live and snapshot previews, minimize/restore clients, leave dashboards hidden, close/reopen tabs, and record whether any EVE process becomes unresponsive. The native checks establish lifecycle behavior under synthetic conditions, not a guarantee about real EVE or driver stability.
