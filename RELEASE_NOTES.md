# EVE Command Center v3.6.13

- Added a saved horizontal/vertical toggle to Character Overview. Vertical mode keeps the existing cards, alarms and client-switching behavior in a compact resizable sidebar with scrolling and a compact tool menu.
- Vertical mode remembers its height and uses top/bottom positioning for drag-to-reorder. Horizontal mode retains its existing automatic or manual sizing.
- Replaced direct portrait URL bindings with cached image downloads, limited to two concurrent requests. Failed downloads retry after a delay without requiring pilots to reconnect, and pending requests are cancelled when the overview closes.
- Added regression coverage for orientation persistence, return to horizontal mode, sidebar scrolling and portrait download recovery.
