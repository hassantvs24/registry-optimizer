using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Scanning.Scanners;

/// <summary>
/// Uninstall entries whose UninstallString references an executable that no longer
/// exists are leftovers from removed software. MSI-based and Windows-update (KB)
/// entries are deliberately left alone.
/// </summary>
public sealed class OrphanedUninstallScanner : IIssueScanner
{
    private static readonly (string Hive, string Root)[] Roots =
    {
        ("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        ("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        ("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        ("HKCU", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
    };

    public ScanCategory Category => ScanCategory.OrphanedUninstallEntries;
    public Severity Severity => Severity.Safe;
    public bool Supports(ScanProfile profile) => true; // both profiles

    public Task ScanAsync(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct)
    {
        foreach (var (hive, root) in Roots)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var name in RegistryWalker.SubKeyNames(hive, root))
            {
                ct.ThrowIfCancellationRequested();
                if (name.StartsWith("KB", StringComparison.OrdinalIgnoreCase)) continue; // Windows update stubs

                var keyPath = $@"{root}\{name}";
                try
                {
                    if (!IsOrphaned(context, hive, keyPath)) continue;

                    Report(context, progress, new ScanIssue
                    {
                        Category = Category, Severity = Severity.Safe, Hive = hive, KeyPath = keyPath,
                        ValueName = null, DataPreview = RegistryWalker.DataToString(RegistryWalker.GetValue(hive, keyPath, "UninstallString")),
                        Reason = "UninstallString points to a missing program; the entry is orphaned",
                        Action = FixAction.DeleteKey,
                    });
                }
                catch (Exception)
                {
                    // one bad key must never abort the scanner
                }
            }
        }
        return Task.CompletedTask;
    }

    private static bool IsOrphaned(ScanContext context, string hive, string keyPath)
    {
        var uninstallString = RegistryWalker.DataToString(RegistryWalker.GetValue(hive, keyPath, "UninstallString"));
        if (string.IsNullOrWhiteSpace(uninstallString)) return false;

        var exe = PathHelper.ExtractFilePath(uninstallString);
        if (string.IsNullOrWhiteSpace(exe)) return false;

        // MSI entries always go through msiexec.exe; the real payload lives in the MSI
        // package, so a "missing exe" here means nothing.
        var fileName = Path.GetFileNameWithoutExtension(exe.Trim().Trim('"'));
        if (fileName.Equals("msiexec", StringComparison.OrdinalIgnoreCase)) return false;

        // Only flag real filesystem paths (C:\... or \\server\...); bare names resolve via PATH.
        var looksAbsolute = exe.Contains(":\\", StringComparison.Ordinal) || exe.StartsWith(@"\\", StringComparison.Ordinal);
        if (!looksAbsolute) return false;

        return context.FileReferenceMissing(exe);
    }

    private void Report(ScanContext context, IProgress<ScanIssue> progress, ScanIssue issue)
    {
        if (!issue.CanFix || context.IsIgnored(issue)) return;
        progress.Report(issue);
    }
}
