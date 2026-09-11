# EVE Command Center v2.5.0

## Corporation Contracts

- New **CONTRACTS** button beside Mining, Pilots and Moons in the fleet overview.
- Select and reconnect a corporation-data character for ESI contract access.
- Outstanding contracts, total value, Janice links, expected price and actual Jita-buy percentage.
- Green for passed checks, red for mismatched price or destination, amber for unverified data.
- Approved destinations: **Raren - Ducks Migration**, **Mazitah - Eagle One**, and **Joppaya IX - Moon 9 - Ardishapur Family Bureau**.
- Double-click a row to inspect quantities, volume, blueprint-copy flags, and items received or provided.
- Open contracts in the authorized character's EVE client.

## Background monitoring

- Moon and contract monitoring continue with their windows closed or minimized while Command Center is running.
- Contract checks every 30 minutes; moon checks every 61 minutes, with retries after errors.
- New-contract notifications and saved notification history to prevent repeats after restarting.
- Reconnect your contract-reading character once, then click **USE TOON / REFRESH**. The first refresh establishes a baseline.

## Update cleanup

- The temporary `.previous` executable is removed once the updated process starts successfully.
- The update overview continues to offer **Update & Restart** or **Skip & Launch**.
