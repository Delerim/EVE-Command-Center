# EVE Command Center v3.6.58

- Restored a single, explicit **Combined / Separate** overview mode switch that persists across restarts and stays unchanged when opening the Home dashboard or choosing a card view.
- Removed the redundant **Alerts & Discord** item from the Command Center sidebar; Discord webhook configuration remains in **App Settings → Alerts → Configure Discord webhooks and channels**.
- Hardened legacy thumbnail/stat-overlay visibility so Combined mode hides the old per-client windows without suppressing independent Picture-in-Picture previews.

## Overview mode: one intentional switch
- Command Deck now has a prominently labelled **COMBINED / SEPARATE** toggle with its current state visible without hovering. Only that explicit action changes the overview mode; selection is saved to the existing mining dashboard preferences.
- Startup honours the saved preference rather than forcibly turning Combined on each launch.
- Clicking **Open Character Overview**, changing card display between Characters/Mining/PvE/PvP, or pressing **Home** no longer changes the Combined / Separate preference.
- Reapplying settings and changing EVE/Command Center focus cannot resurrect legacy per-client thumbnail/stat windows in Combined mode. Existing individual preview behavior returns when Separate is explicitly selected.
- The independent PiP system is unchanged and remains separately controlled.

## Settings and Discord
- Removed the duplicated Alerts & Discord sidebar shortcut. The existing Alerts settings, dedicated Discord webhook dialog, encryption, saved channels, opt-in PI groups, and desktop notification controls remain unchanged.
- Moon-not-set and PI alert delivery retain their existing deduplication. Discord does not post fracture notifications.

## Compatibility
- No new account permissions, credential migration or settings reset. The existing encrypted Discord webhooks and overlay positions remain in place.
- Automated regression checks cover primary-overlay visibility rules; native focus/return-to-app behavior still needs live Windows verification; validation remains mandatory before the official Windows release.
