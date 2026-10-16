# AGENTS.md

## Project
Registry Cleaner & Optimizer — WPF (.NET 8, x64, `net8.0-windows`) single-project solution in
`E:\optimizer\RegistryOptimizer\RegistryOptimizer.sln`. MVVM via `CommunityToolkit.Mvvm`.
Packages: `AlphaVSS` (VSS snapshots), `TaskScheduler` (weekly scan task).

## Build & test
- PATH must include `C:\Program Files\dotnet` in the bash shell: `export PATH="$PATH:/c/Program Files/dotnet"`.
- Build: `dotnet build` from `E:\optimizer`.
- Publish portable exe + installer: `powershell -File E:\optimizer\publish.ps1` (Inno Setup optional).
- Real-registry tests modify this machine — only run fix/undo tests deliberately; scans are read-only.

## Conventions
- Nullable enabled, implicit usings, file-scoped namespaces, `RegistryOptimizer.Core.*` namespaces.
- All app data goes to `%ProgramData%\RegistryOptimizer\` (`Core/AppPaths.cs`) — never next to the exe (portable).
- Scanners in `Core/Scanning/Scanners/` must be READ-ONLY, `CancellationToken`-aware, and wrap per-item work in try/catch.
- The fixer must never write under `HKLM\SYSTEM|SAM|SECURITY` (`RegistryWalker.IsProtected`).
- Boot defrag flow: `RegSaveKey` (the save IS the compaction — the live hive file carries
  ~5% internal free space + hbin padding) -> `RegReplaceKey`; hives are swapped by Session
  Manager at the FOLLOWING boot. Never replace hive files directly at runtime.
- UI: dark theme in `Resources/Styles.xaml`; keep views in `Views/`, view models in `ViewModels/`.

## Release
`publish.ps1` produces `dist/RegistryOptimizer.exe` (portable, self-contained single-file) and,
when Inno Setup (`iscc.exe`) is available, `dist/RegistryOptimizer-Setup.exe`.
The app icon (`RegistryOptimizer/Resources/app.ico`, multi-size PNG-in-ICO) is regenerated with
`powershell -File assets/generate-icon.ps1` — it is embedded in the exe (ApplicationIcon), the
installer (SetupIconFile), and the running window. NOTE: WPF single-file publishing cannot
resolve `pack://` resources for `Window.Icon`, so the window icon is applied in code from
embedded bytes (`AppIcon.Generated.cs`, emitted by the same generator script) — do NOT set
`Icon=` in MainWindow.xaml.
