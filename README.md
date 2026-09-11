# EVE Command Center

A Windows command center for EVE Online: live client previews, mining fleet monitoring, pilot intelligence, and corporation moon operations.

## Download

**[Download for Windows x64](https://github.com/Delerim/EVE-Command-Center/releases/latest/download/EVE-Command-Center-Windows-x64.zip)**

Extract the ZIP to a writable folder and run **EVE Command Center.exe**. Requires Windows 10 version 2004 or later. The .NET runtime is included.

Upgrading: close the app and replace its executable. Keep your settings JSON files beside it. Older filenames migrate automatically. Linked credentials and moon history remain in local application data.

[Release notes](https://github.com/Delerim/EVE-Command-Center/releases) | [Report a bug](https://github.com/Delerim/EVE-Command-Center/issues)

## Features

- Live client thumbnails, hotkeys, independent Picture-in-Picture previews, and saved layouts.
- Mining fleet overview with idle alerts, cycle timers, ore valuation, and history.
- Pilot skills, training, assets, fittings, wallet information, and ship defense calculations.
- Corporation moon calendar, mining ledger, ore profiles, and period reporting.
- Desktop moon alerts for low fuel and drills without an extraction scheduled. Monitoring runs while Moon Report is open, including minimized.
- Dark teal interface, configurable overlays, translated settings, and per-character controls.

## Build

Install the .NET 8 SDK on Windows, then:

```powershell
git clone https://github.com/Delerim/EVE-Command-Center.git
cd EVE-Command-Center
dotnet publish EveCommandCenter.csproj -c Release -o artifacts/publish
```

Output: `artifacts/publish/EVE Command Center.exe`.

## Releases

Push a version tag matching the project version, such as `v2.4.0`, to publish a GitHub Release with a portable ZIP, updater executable, and SHA-256 checksums. Pushes to `main` also produce a GitHub Actions build artifact. The app checks this repository for updates.

## Credits

See [NOTICE.md](NOTICE.md) for project origins and acknowledgments. EVE Online belongs to CCP Games; this is a community project.
