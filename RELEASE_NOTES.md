# EVE Command Center v3.6.56

- Add optional Discord webhook delivery under Settings > Alerts > Discord webhooks.
- Support multiple named webhook destinations, each with its own channel label, enable switch and selected alert types. Discord determines the actual destination channel from the webhook URL.
- Alert choices: moon drill with no extraction scheduled, low structure fuel, new and accepted corporation contracts, industry ready, Omega renewal and glistening ore.
- Moon fracture, moon-ready and field-active milestones are deliberately NOT sent to Discord.
- Delivery is asynchronous and independent of desktop alert preferences. Duplicate deliveries are suppressed across restarts, and moon/fuel alerts re-arm when the condition clears.
- Webhook secrets are encrypted using Windows DPAPI for the current user and stored in local application data; no webhook URLs are added to source, logs or ordinary settings files.
- Add webhook URL validation and local persistence regression checks.
