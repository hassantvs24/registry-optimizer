using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Scanning.Scanners;

/// <summary>
/// File associations: extension keys whose default ProgID no longer exists, and
/// OpenWithList entries naming executables that are nowhere to be found. Quick profile only.
/// </summary>
public sealed class BrokenFileAssocScanner : IIssueScanner
{
    private const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const int MaxExtensions = 5000;
    private const int MaxOpenWithValues = 5000;

    private static readonly (string Hive, string Root)[] Roots =
    {
        ("HKCR", string.Empty),
        ("HKLM", @"SOFTWARE\Classes"),
    };

    public ScanCategory Category => ScanCategory.BrokenFileAssociations;
    public Severity Severity => Severity.Safe;
    public bool Supports(ScanProfile profile) => profile == ScanProfile.Quick; // quick only

    public Task ScanAsync(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct)
    {
        var seenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensionCount = 0;
        var openWithCount = 0;

        foreach (var (hive, root) in Roots)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var name in RegistryWalker.SubKeyNames(hive, root))
            {
                ct.ThrowIfCancellationRequested();
                if (!name.StartsWith('.')) continue;
                if (!seenExtensions.Add(name)) continue; // HKCR and HKLM\SOFTWARE\Classes overlap
                if (++extensionCount > MaxExtensions) break;

                try
                {
                    var keyPath = root.Length == 0 ? name : $@"{root}\{name}";
                    ScanProgIdRef(context, progress, hive, name, keyPath);

                    if (openWithCount < MaxOpenWithValues)
                        openWithCount += ScanOpenWithList(context, progress, hive, name, keyPath, ct);
                }
                catch (Exception)
                {
                    // one bad key must never abort the scanner
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Default value names a ProgID: flag only when the key is otherwise empty.</summary>
    private void ScanProgIdRef(ScanContext context, IProgress<ScanIssue> progress, string hive, string ext, string keyPath)
    {
        var progId = RegistryWalker.DataToString(RegistryWalker.GetValue(hive, keyPath, null));
        if (string.IsNullOrWhiteSpace(progId)) return;
        progId = progId.Trim();
        using (var progIdKey = RegistryWalker.OpenKey("HKCR", progId))
        {
            if (progIdKey is not null) return; // ProgID still registered
        }

        // Only safe to delete the extension key when nothing else lives inside it
        // (ignoring the default value itself).
        var (subKeys, otherValues) = KeyShape(hive, keyPath);
        if (subKeys > 0 || otherValues > 0) return;

        Report(context, progress, new ScanIssue
        {
            Category = Category, Severity = Severity.Safe, Hive = hive, KeyPath = keyPath,
            ValueName = null, DataPreview = progId,
            Reason = $"Extension references missing ProgID '{progId}' and the key is otherwise empty",
            Action = FixAction.DeleteKey,
        });
    }

    /// <summary>OpenWithList values name executables; flag ones with no App Paths entry and not in System32.</summary>
    private int ScanOpenWithList(ScanContext context, IProgress<ScanIssue> progress, string hive, string ext, string keyPath, CancellationToken ct)
    {
        var count = 0;
        var openWithPath = $@"{keyPath}\OpenWithList";
        foreach (var (valueName, data) in RegistryWalker.Values(hive, openWithPath))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var exe = RegistryWalker.DataToString(data);
                if (string.IsNullOrWhiteSpace(exe)) continue;
                if (++count > MaxOpenWithValues) break;

                var exeName = Path.GetFileName(exe.Trim());
                if (IsKnownExe(exeName)) continue;

                Report(context, progress, new ScanIssue
                {
                    Category = Category, Severity = Severity.Safe, Hive = hive, KeyPath = openWithPath,
                    ValueName = valueName, DataPreview = exeName,
                    Reason = $"OpenWithList entry '{exeName}' has no App Paths registration and is not a system executable",
                    Action = FixAction.DeleteValue,
                });
            }
            catch (Exception)
            {
            }
        }
        return Math.Min(count, MaxOpenWithValues);
    }

    private static bool IsKnownExe(string exeName)
    {
        if (exeName.Length == 0) return true;
        // App Paths registration covers most third-party tools.
        using (var appPath = RegistryWalker.OpenKey("HKLM", $@"{AppPaths}\{exeName}"))
        {
            if (appPath is not null) return true;
        }
        // explorer, notepad, etc. resolve straight out of the system directories.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return File.Exists(Path.Combine(windows, "System32", exeName)) ||
               File.Exists(Path.Combine(windows, "SysWOW64", exeName));
    }

    /// <summary>Counts subkeys and values, excluding the default (unnamed) value.</summary>
    private static (int SubKeys, int OtherValues) KeyShape(string hive, string keyPath)
    {
        using var key = RegistryWalker.OpenKey(hive, keyPath);
        if (key is null) return (0, 0);
        try
        {
            var values = key.GetValueNames().Count(n => n.Length > 0);
            return (key.GetSubKeyNames().Length, values);
        }
        catch
        {
            return (0, 0);
        }
    }

    private void Report(ScanContext context, IProgress<ScanIssue> progress, ScanIssue issue)
    {
        if (!issue.CanFix || context.IsIgnored(issue)) return;
        progress.Report(issue);
    }
}
