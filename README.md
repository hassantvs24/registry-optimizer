# Registry Cleaner & Optimizer

[![Release](https://img.shields.io/github/v/release/hassantvs24/registry-optimizer)](https://github.com/hassantvs24/registry-optimizer/releases/latest)

**[Download the latest release](https://github.com/hassantvs24/registry-optimizer/releases/latest)** — installer or portable exe.

A Windows desktop app (Advanced System Optimizer style) focused entirely on **registry tools**:

- **Registry Cleaner** — Quick and Deep scans across 8 issue categories (missing file references,
  shared DLLs, orphaned uninstall entries, broken file associations, startup entries, invalid COM
  objects, MRU history, orphaned keys), grouped review, ignore list, one-click **Fix** with
  automatic `.reg` backups and full **undo (Rescue Center)**.
- **Registry Optimizer** — fragmentation analysis of all registry hives (SYSTEM, SOFTWARE,
  NTUSER.DAT, UsrClass.dat, ...) read through VSS snapshots, plus safe boot-time hive
  compaction/defragmentation with before/after reports. Genuinely fragmented hives get a
  coalescing pass (measured: 51% internal free space → 4%); per-user hives of logged-off
  users are optimized too — only the logged-on user's hives wait for a logoff boot.
- **Backup & Rescue Center** — automatic backups before every change, restore, undo history.
- **Scheduler** — weekly automatic Quick scan via Windows Task Scheduler.

## Build

Requirements: .NET 8 SDK, Windows 10/11 x64.

```powershell
dotnet build -c Release          # build
.\publish.ps1                    # publish portable exe + installer (needs Inno Setup for the installer)
```

Output:

- `dist\RegistryOptimizer.exe` — portable, self-contained single-file exe (~70–90 MB, embedded
  .NET runtime). Runs on any Windows 10/11 x64 PC, works from a USB stick. All app data
  (backups, reports, settings) lives in `%ProgramData%\RegistryOptimizer\`.
- `dist\RegistryOptimizer-Setup.exe` — Inno Setup installer (Start Menu shortcut, uninstall entry).

## Architecture

- `RegistryOptimizer/` — single WPF app, MVVM (`CommunityToolkit.Mvvm`), dark theme.
  - `Core/Scanning` — scanner contracts, `ScanService`, 8 category scanners, `IssueFixer`.
  - `Core/Registry` — `RegistryWalker`, `PathHelper`, native advapi32 interop.
  - `Core/Backup` — `.reg` snapshot export, fix backups, undo service, restore points.
  - `Core/Defrag` + `Core/Vss` — hive locator, fragmentation analyzer, compactor, boot-time
    defrag scheduler/runner (NTREGOPT-style: save → compact → `RegReplaceKey`, swap completes
    at the following boot).
  - `Core/Scheduling` — weekly scan task via the TaskScheduler library.

The app always runs elevated (`requireAdministrator`) because it edits HKLM, takes VSS snapshots,
and registers boot-time hive replacement.

## Safety

- Scans are read-only; nothing changes until **Fix**/**Optimize** is confirmed.
- Every fix is preceded by an automatic `.reg` backup; Rescue Center can roll any session back.
- Registry defrag never touches live hives at runtime: compaction output is swapped in by the
  Windows Session Manager at boot, after a full hive backup.
- `HKLM\SYSTEM`, `HKLM\SAM` and `HKLM\SECURITY` are never written by the fixer.
