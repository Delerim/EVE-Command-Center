# EVE Command Center v3.5.4

- Fixed release publishing reusing the old v3.2.0 patch notes. Update prompts now receive the notes written for this release.
- Added a mandatory release check: patch notes must name the current project version, contain a change list, and match the release tag. Stale notes stop the build before publishing.

## Recently shipped in v3.5.3

- Preview switching no longer attaches its input queue to an EVE client. Unresponsive clients are skipped during cycling; client positioning and minimisation use asynchronous requests.
- Moved snapshot captures off the UI thread, limited outstanding captures, fixed bitmap ownership and failure cleanup, and prevented delayed minimise actions accumulating during rapid switches.
- Added PI Extractors Compact Mode and fixed mouse-wheel scrolling over planet tables.
- Preview debug logs rotate at approximately 6 MB per category. Enable Window Hooks and Desktop Window Manager in Preview Settings > Debug to investigate a recurring hang.

The underlying EVE client hang has not been reproduced or confirmed fixed; these changes address ways it could also stall Command Center.
