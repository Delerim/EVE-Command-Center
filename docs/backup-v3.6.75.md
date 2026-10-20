# Backup and recovery: v3.6.75

## User workflow

Open Settings > Backup. Keep the existing Google connection and recovery password. Use **Back up now** for a full encrypted snapshot. There is no new test button.

Enable **Your backup schedule**, enter your own interval (5 to 43,200 minutes), and save. Leave it disabled for manual backups. Existing saved preferences are retained on upgrade; new installations default to disabled. Scheduling runs while the application is open, checking approximately once a minute; it does not install a Windows background task. The interval is measured from the last successful backup. There are no separate daily, weekly, monthly or exit backups.

Google Drive retains the newest two snapshots after a successful backup. A new object is uploaded and downloaded for byte verification before older objects are deleted. This intentionally replaces the previous multi-tier retention policy, per the user's updated request. Failed upload/verification does not delete earlier copies or mark success. If deletion fails, the UI reports that extra copies may remain; it does not remove the new or previous copy to force a misleading success.

Choose a restore point and use **Restore**. Encrypted archives are validated before live recovery writes. Application data returns to its recorded locations, external EVE logs to Documents/EVE/logs, and saved EVE refresh tokens to Windows Credential Manager. The app restarts using the restored data. The existing local restore-safety copies remain separate from the two cloud restore points.

## Captured history and limits

Includes the existing saved application data scope (JSON/JSONL, SQLite snapshots and other supported local files), logs under Documents/EVE/logs, linked EVE logins and the explicitly selected private moon CSV. Google credentials and the recovery password are excluded; retain the password separately and reconnect Google on a replacement PC. Missing EVE logins are reported as needing reconnection.

Recovery preserves captured mining, moon, contracts, PI, industry, market and wallet history. It cannot reconstruct observations that were never collected. Raw mining logs are preserved, but the existing initial mining importer still has a one-year backfill window; this patch does not claim to import every older log into the dashboard. Custom external data outside the existing source scope is not automatically discovered.

The unchanged ECCENC02 AES-GCM format is retained. Verification currently supports up to 512 MiB of **compressed encrypted archive**, not 512 MiB of original source files. Larger archives fail explicitly without deleting earlier backups. Upload/read-back allow up to 15 minutes each. Recovery is not a cross-file database transaction; the existing safety copies protect overwritten local files if an unrelated disk/write failure interrupts recovery.

## Compiler investigation

On SDK 9.0.319/runtime 9.0.21, the original v3.6.74 source and both supplied v3.6.75 patches built successfully in an isolated worktree. No SDK changes, processor-count overrides or analyzer-disabling workaround were used for final validation.

Windows Application events record historical dotnet/coreclr access violations (0xc0000005), including 2026-10-20 17:06 and 17:16. They do not establish the triggering source change or prove a corrupted installation. The crash could not be reproduced, so no exact compiler root cause is claimed.

Comparison results:

| Candidate | Build | Result |
| --- | --- | --- |
| v3.6.74 | Single worker, shared compilation/analyzers off | Pass, 9.17 s |
| Supplied simplified patch | Same controlled settings | Pass, 11.17 s |
| Supplied simplified patch | Normal rebuild, analyzers enabled | Pass, 14.10 s |
| Supplied earlier patch | Normal rebuild, analyzers enabled | Pass, 8.65 s |
| v3.6.74 | Normal rebuild | Pass, 7.17 s |
| Corrected v3.6.75 app and checks | Normal rebuild | Pass, zero warnings/errors, 8.93 s |
| Corrected v3.6.75 app | Single worker, shared compilation off, analyzers enabled | Pass, zero warnings/errors, 14.43 s |

## Validation commands

Run from the repository root on Windows:

```powershell
dotnet build Tests/CommandCenter.Checks.csproj -c Release --nologo -t:Rebuild
dotnet run --project Tests/CommandCenter.Checks.csproj -c Release
dotnet build EveCommandCenter.csproj -c Release --nologo -t:Rebuild -m:1 /p:UseSharedCompilation=false
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Validate-ReleaseNotes.ps1 -Tag v3.6.75
```

Full regression suite: **861 checks passed**. New checks use isolated settings, synthetic archives and an in-memory HTTP handler. They exercise successful rotation, repeat backups, upload/download failures, corrupted read-back, cancellation, wrong password, failed retention, login-payload consistency, unsafe destinations, source inclusion and custom scheduling. Existing SQLite consistency, encryption, history and preview regressions also pass.

These checks do **not** perform a live Google Drive backup, delete real restore points, expose credentials or restore into live application data. Archive integrity checks are not a completed new-PC recovery acceptance test.

## Release / rollback

Changes were prepared and validated in a detached worktree at v3.6.74 (`0f164b2`), leaving the primary source unchanged during validation. Publish only after the checks pass. Version and release notes match v3.6.75; the existing tagged GitHub release workflow produces the portable executable, ZIP and checksums. Earlier Apply scripts are preserved. Roll back the executable to v3.6.74 if necessary; existing encrypted backups keep their format. Rolling back code cannot restore older cloud copies removed by the explicitly requested two-copy policy.
