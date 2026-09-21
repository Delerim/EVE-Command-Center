# EVE Command Center v3.5.30

- Updated the GitHub release pipeline to Node 24-compatible actions.
- Tag releases now publish directly to GitHub Releases instead of staging a second large Actions artifact, avoiding artifact-finalization failures.
- Main-branch diagnostic build artifacts are retained for seven days to reduce Actions storage usage.
