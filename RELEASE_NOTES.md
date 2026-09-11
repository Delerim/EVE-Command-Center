# EVE Command Center v2.9.1

## Update prompts while Command Center is running

- Check GitHub Releases at startup and every five minutes while Command Center is open, including when views are closed or minimized.
- A newer downloadable release opens the existing themed changelog window with Update & Restart and Skip & Continue. The prompt appears above other windows without taking keyboard focus.
- Each release prompts once per running session. Skipping keeps the current client running; a newer version can prompt again. Another open update dialog defers the background prompt.
- Automatic checking and prerelease preferences remain respected. The About setting now explicitly says it covers startup and background checks.
- Stop the timer and cancel in-flight checks on shutdown. Prevent overlapping checks and avoid changing shared HTTP headers during active requests.
- Installation and restart only happen after choosing Update & Restart. ESI polling is unchanged.

Install this version once through the existing startup updater to enable prompts for future releases while running.
