# EVE Command Center v3.6.20

- Fixed update/exit ordering that could restore separate previews after WPF had closed their text overlays, causing a closed-window exception during restart.
- Stop and dispose preview management before update shutdown; ignore queued foreground work and combined-preview restoration after disposal.
- Prevent preview display and z-order maintenance from reopening a closed text overlay.
- Added a native regression for closing an overlay before restoring its preview.
