using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Scanning.Scanners;

/// <summary>
/// Stray keys nobody maintains anymore: MUICache entries for deleted programs and
/// completely empty keys under common software roots. Deep profile only.
/// </summary>
public sealed class OrphanedKeyScanner : IIssueScanner
{
    private const string MuiCache = @"Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
    private const int MaxIssues = 1000;

    private static readonly (string Hive, string Root, int MaxDepth, int MaxKeys)[] EmptyKeyRoots =
    {
        ("HKCU", @"SOFTWARE", 6, 8000),
        ("HKLM", @"SOFTWARE\WOW6432Node", 4, 4000),
    };

    public ScanCategory Category => ScanCategory.OrphanedKeys;
    public Severity Severity => Severity.Advanced;
    public bool Supports(ScanProfile profile) => profile == ScanProfile.Deep; // deep only

    public Task ScanAsync(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct)
    {
        var issues = 0;

        // (1) MUICache: value NAMES are cached executable paths.
        foreach (var (name, _) in RegistryWalker.Values("HKCU", MuiCache))
        {
            ct.ThrowIfCancellationRequested();
            if (issues >= MaxIssues) return Task.CompletedTask;
            try
            {
                if (!LooksLikePath(name)) continue;
                if (!context.FileReferenceMissing(name)) continue;

                issues++;
                Report(context, progress, new ScanIssue
                {
                    Category = Category, Severity = Severity.Advanced, Hive = "HKCU", KeyPath = MuiCache,
                    ValueName = name, DataPreview = name,
                    Reason = "MUICache entry for a program that no longer exists", Action = FixAction.DeleteValue,
                });
            }
            catch (Exception)
            {
                // one bad value must never abort the scanner
            }
        }

        // (2) Empty keys: zero subkeys and zero values.
        foreach (var (hive, root, maxDepth, maxKeys) in EmptyKeyRoots)
        {
            foreach (var keyPath in RegistryWalker.WalkSubKeys(hive, root, maxDepth, maxKeys))
            {
                ct.ThrowIfCancellationRequested();
                if (issues >= MaxIssues) return Task.CompletedTask;
                if (keyPath.Equals(root, StringComparison.OrdinalIgnoreCase)) continue; // never the root itself
                try
                {
                    if (ShouldSkip(keyPath)) continue;
                    if (!IsEmpty(hive, keyPath)) continue;

                    issues++;
                    Report(context, progress, new ScanIssue
                    {
                        Category = Category, Severity = Severity.Advanced, Hive = hive, KeyPath = keyPath,
                        ValueName = null, DataPreview = keyPath,
                        Reason = "Key has no subkeys and no values", Action = FixAction.DeleteKey,
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

    private static bool LooksLikePath(string valueName) =>
        valueName.Contains(":\\", StringComparison.Ordinal) || valueName.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Areas that are managed by Windows or other scanners are off-limits.</summary>
    private static bool ShouldSkip(string keyPath)
    {
        var p = keyPath.Replace('/', '\\');
        if (p.Contains(@"\Uninstall", StringComparison.OrdinalIgnoreCase)) return true;          // covered by the uninstall scanners
        if (p.Contains(@"\Windows\CurrentVersion\Explorer", StringComparison.OrdinalIgnoreCase)) return true; // shell-managed
        if (p.Contains("Classes", StringComparison.OrdinalIgnoreCase)) return true;              // COM/association classes
        return false;
    }

    private static bool IsEmpty(string hive, string keyPath)
    {
        using var key = RegistryWalker.OpenKey(hive, keyPath);
        if (key is null) return false;
        try
        {
            return key.GetSubKeyNames().Length == 0 && key.GetValueNames().Length == 0;
        }
        catch
        {
            return false; // unreadable -> don't flag
        }
    }

    private void Report(ScanContext context, IProgress<ScanIssue> progress, ScanIssue issue)
    {
        if (!issue.CanFix || context.IsIgnored(issue)) return;
        progress.Report(issue);
    }
}
