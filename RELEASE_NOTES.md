# EVE Command Center v3.5.19

- Character Overview now offers a saved PREVIEW: LIVE / SNAPSHOT toggle. Live mode uses native DWM thumbnails, like the independent previews, without running capture or client waits on the UI thread.
- Live surfaces follow the overview and release their native windows and thumbnail registrations when disabled, hidden or closed. Minimized clients show the last available snapshot; restoring them resumes live preview. Partially scrolled cards use snapshots until fully visible.
- Character and mining modes share the same card width and minimum height, including space for optional rock tracking. Switching modes keeps the same frames and window sizing.
- Added hover glow, click feedback and an active-client indicator. Existing mining warning and alarm colours remain visible in character mode and take priority over selection colours. Muted alarms remain muted.
- Card updates now preserve their visual elements, allowing alarms and active-client feedback to update while hovering without destroying tooltips or repeatedly recreating live previews.
