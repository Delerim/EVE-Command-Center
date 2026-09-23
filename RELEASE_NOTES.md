# EVE Command Center v3.6.7

- Removed Window.Show() from Command Center embedded-module startup, eliminating the native top-level backing-window presentation that could flash black when a tool opened for the first time.
- Embedded tools now create only a hidden HWND for interop/SourceInitialized needs, move their visual content directly into Command Center, and run their existing Window.Loaded bootstrap once the embedded visual tree is naturally loaded.
- Preserves existing standalone tool behavior, timers, refresh logic, dialogs and Window-based code-behind while making Command Center page switching visually native to the main shell.
