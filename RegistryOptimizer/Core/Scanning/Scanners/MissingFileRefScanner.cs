using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Scanning.Scanners;

/// <summary>
/// Finds registry values that point at files or folders which no longer exist:
/// App Paths entries, font registrations, IFEO debuggers, and uninstall metadata.
/// </summary>
public sealed class MissingFileRefScanner : IIssueScanner
{
    private const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private static readonly (string Hive, string Root)[] UninstallRoots =
    {
        ("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        ("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    };

    public ScanCategory Category => ScanCategory.MissingFileReferences;
    public Severity Severity => Severity.Safe;
    public bool Supports(ScanProfile profile) => true; // both profiles

    public Task ScanAsync(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ScanAppPaths(context, progress, ct);
        ScanFonts(context, progress, ct);
        ScanIfeo(context, progress, ct);
        ScanUninstallRefs(context, progress, ct);
        return Task.CompletedTask;
    }

    /// <summary>App Paths: default value (target exe) and "Path" (working directory) per subkey.</summary>
    private static void ScanAppPaths(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct)
    {
        foreach (var name in RegistryWalker.SubKeyNames("HKLM", AppPaths))
        {
            ct.ThrowIfCancellationRequested();
            var keyPath = $@"{AppPaths}\{name}";
            try
            {
                var target = PathHelper.ExtractFilePath(RegistryWalker.DataToString(RegistryWalker.GetValue("HKLM", keyPath, null)));
                if (context.FileReferenceMissing(target))
                    Report(context, progress, new ScanIssue
                    {
                        Category = ScanCategory.MissingFileReferences, Severity = Severity.Safe, Hive = "HKLM", KeyPath = keyPath,
                        ValueName = null, DataPreview = target,
                        Reason = "App Paths entry points to a missing executable", Action = FixAction.DeleteValue,
                    });

                var dir = PathHelper.Expand(RegistryWalker.DataToString(RegistryWalker.GetValue("HKLM", keyPath, "Path")) ?? string.Empty);
                if (context.FileReferenceMissing(dir))
                    Report(context, progress, new ScanIssue
                    {
                        Category = ScanCategory.MissingFileReferences, Severity = Severity.Safe, Hive = "HKLM", KeyPath = keyPath,
                        ValueName = "Path", DataPreview = dir,
                        Reason = "App Paths working directory no longer exists", Action = FixAction.DeleteValue,
                    });
            }
            catch (Exception)
            {
                // one bad key must never abort the scanner
            }
        }
    }

    /// <summary>Fonts: value data is a font filename, normally resolved against %WinDir%\Fonts.</summary>
    private static void ScanFonts(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct)
    {
        const string fonts = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Fonts";
        var fontsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

        foreach (var hive in new[] { "HKLM", "HKCU" })
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (name, data) in RegistryWalker.Values(hive, fonts))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var file = RegistryWalker.DataToString(data);
                    if (string.IsNullOrWhiteSpace(file)) continue;
                    // Bare filenames live in %WinDir%\Fonts; anything else is tested as written.
                    var candidate = file.Contains('\\') || file.Contains(':') ? file : Path.Combine(fontsDir, file);
                    if (!context.FileReferenceMissing(candidate)) continue;

                    Report(context, progress, new ScanIssue
                    {
                        Category = ScanCategory.MissingFileReferences, Severity = Severity.Safe, Hive = hive, KeyPath = fonts,
                        ValueName = name, DataPreview = candidate,
                        Reason = "Registered font file is missing", Action = FixAction.DeleteValue,
                    });
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// <summary>IFEO: a Debugger value pointing at a missing binary breaks every launch of that image.</summary>
    private static void ScanIfeo(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct)
    {
        foreach (var name in RegistryWalker.SubKeyNames("HKLM", Ifeo))
        {
            ct.ThrowIfCancellationRequested();
            var keyPath = $@"{Ifeo}\{name}";
            try
            {
                var debugger = PathHelper.ExtractFilePath(RegistryWalker.DataToString(RegistryWalker.GetValue("HKLM", keyPath, "Debugger")));
                if (!context.FileReferenceMissing(debugger)) continue;

                Report(context, progress, new ScanIssue
                {
                    Category = ScanCategory.MissingFileReferences, Severity = Severity.Safe, Hive = "HKLM", KeyPath = keyPath,
                    ValueName = "Debugger", DataPreview = debugger,
                    Reason = "Image File Execution Options debugger points to a missing file", Action = FixAction.DeleteValue,
                });
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Uninstall metadata: InstallLocation and DisplayIcon referencing vanished files/folders.</summary>
    private static void ScanUninstallRefs(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct)
    {
        foreach (var (hive, root) in UninstallRoots)
        {
            foreach (var name in RegistryWalker.SubKeyNames(hive, root))
            {
                ct.ThrowIfCancellationRequested();
                var keyPath = $@"{root}\{name}";
                try
                {
                    CheckRef(context, progress, hive, keyPath, "InstallLocation",
                        RegistryWalker.DataToString(RegistryWalker.GetValue(hive, keyPath, "InstallLocation")),
                        "Install location no longer exists");

                    var icon = PathHelper.ExtractFilePath(RegistryWalker.DataToString(RegistryWalker.GetValue(hive, keyPath, "DisplayIcon")));
                    CheckRef(context, progress, hive, keyPath, "DisplayIcon", icon,
                        "Display icon points to a missing file");
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static void CheckRef(ScanContext context, IProgress<ScanIssue> progress, string hive,
        string keyPath, string valueName, string? reference, string reason)
    {
        if (!context.FileReferenceMissing(reference)) return;
        Report(context, progress, new ScanIssue
        {
            Category = ScanCategory.MissingFileReferences, Severity = Severity.Safe, Hive = hive, KeyPath = keyPath,
            ValueName = valueName, DataPreview = reference, Reason = reason, Action = FixAction.DeleteValue,
        });
    }

    private static void Report(ScanContext context, IProgress<ScanIssue> progress, ScanIssue issue)
    {
        if (!issue.CanFix || context.IsIgnored(issue)) return;
        progress.Report(issue);
    }
}
