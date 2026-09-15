# EVE Command Center v3.5.18

- Renamed the Miner Overview to Character Overview and added optional Combine Previews mode. Open EVE clients appear even without mining activity.
- Switch between compact character preview cards and the existing mining details. Click cards to switch clients through the existing activation path; embedded controls and drag reordering keep their own actions.
- Combined mode moves navigation into Tools and temporarily hides primary preview windows and their stat overlays. Separate mode, closing or hiding the overview restores the independent previews without changing their saved positions.
- Compact previews use bounded asynchronous snapshots, with small cached frames and a three-second scheduling cadence (large fleets may refresh less often). Minimized clients retain the last available frame. No capture runs on the UI thread; existing two-worker limits remain.
- Mode preferences persist. The original standalone preview mode remains the default.
