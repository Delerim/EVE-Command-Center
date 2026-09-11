# Contracts and background operations

Contracts lists outstanding, unexpired contracts assigned to the chosen character's corporation. ESI pagination is followed. The selected character and last successful snapshot are saved locally; a failed refresh keeps that snapshot and shows its error and age.

## Permissions

Use RECONNECT / ADD inside Contracts to request `esi-contracts.read_corporation_contracts.v1`, `esi-universe.read_structures.v1`, and `esi-ui.open_window.v1`, in addition to the existing pilot scopes. The character must also have the corporation permissions required by ESI. Structure names require access to that structure. Open in Game sends an ESI UI request for the selected character, who must be logged into EVE. The app does not accept contracts.

## Validation

The reference executable supplied by the user was inspected as a PyInstaller Python 3.9 archive. Its bytecode showed outstanding/assignee filtering, Janice links extracted from titles, Jita immediate buy total times 90%, a default tolerance of max(1 ISK, 0.1% of expected price), and optional location checks. This implementation recreates those behaviors in C#/WPF. No executable code or credentials from the reference app are included.

Destination validation is stricter than the reference: it uses the ESI destination, never title keywords. Joppaya IX - Moon 9 - Ardishapur Family Bureau was resolved through ESI to station ID 60008740. Player structures must resolve through authenticated ESI and exactly match Raren - Ducks Migration or Mazitah - Eagle One (case and dash variants normalized). Unknown locations never pass.

Green means the supported price, market, contract type and destination checks passed. Red means a known mismatch. Amber means required data is missing or could not be verified. Actual Jita buy percentage, ISK difference and reasons are displayed. Appraisal quantities are not automatically reconciled against the manifest; the contents window shows both received and requested items for inspection.

Janice appraisals use the same public Appraisal.get RPC as the reference app and cache for 30 minutes. A Janice error is shown as unverified. ESI rate limits stop the refresh rather than continuing requests.

## Monitoring

An app-owned BackgroundOperations service starts at application startup. It polls contracts every 30 minutes and moons every 61 minutes; failures retry after 10 minutes. Views do not own polling. Saved selected characters are used after restart. Existing mining and combat monitoring already run independently of their views. Monitoring stops when Command Center exits.

The first successful contract refresh per corporation establishes a baseline. New IDs after that trigger notifications, and seen IDs persist locally across restarts. Moon notifications use a six-hour cooldown and rearm when an issue disappears. Notification switches are respected.

## Verification

`dotnet run --project Tests/CommandCenter.Checks.csproj -c Release` runs calculation, destination, persistence, pagination, simulated ESI, and XAML loading checks without real credentials. Pass an output PNG path after `--` to render sample overview and contents layouts.

References: https://esi.evetech.net/meta/openapi.json and https://github.com/E-351/janice
