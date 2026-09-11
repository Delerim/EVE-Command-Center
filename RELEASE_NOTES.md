# EVE Command Center v2.9.3

- Fixed Moon and Contracts tabs disappearing when permission verification encounters temporary ESI throttling, timeouts or server errors.
- Successful verification is remembered for the same reader for up to 24 hours, including across restarts. Temporary failures show a delayed-verification status and retry automatically.
- Explicit authentication or permission denial, missing scopes, unlinking or changing readers still removes access. Unverified readers do not gain access during an outage.
- Moon access updates immediately after its check instead of waiting for the Contracts check.
- Added Verify Access beside the Moon reader in Settings; reauthorization is no longer needed just to retry verification.

Validation: 104 automated checks passed. Live ESI verification confirmed the configured Moon and Contracts readers have access.
