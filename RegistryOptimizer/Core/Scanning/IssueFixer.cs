using Microsoft.Win32;
using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Scanning;

/// <summary>Outcome of a fix pass.</summary>
public sealed class FixSummary
{
    public int Fixed { get; set; }
    public int Failed { get; set; }
    public List<string> Failures { get; } = new();
}

/// <summary>
/// Applies the <see cref="FixAction"/> of each fixable issue. Only ever opens keys
/// writable through <see cref="RegistryWalker.OpenKeyWritable"/> and re-checks
/// protected areas before every write. Failures are collected, never thrown.
/// </summary>
public static class IssueFixer
{
    private const string SharedDllsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLS";

    public static FixSummary Apply(IReadOnlyList<ScanIssue> issues, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var summary = new FixSummary();
        foreach (var issue in issues)
        {
            ct.ThrowIfCancellationRequested();
            if (!issue.CanFix) continue;

            // Hard safety net: never touch protected areas, no matter what the scanners found.
            if (RegistryWalker.IsProtected(issue.Hive, issue.KeyPath))
            {
                summary.Failed++;
                summary.Failures.Add($"{issue.FullPath}: protected registry area, left untouched");
                progress?.Report($"Skipped {issue.FullPath} (protected area).");
                continue;
            }

            try
            {
                ApplyOne(issue, progress);
                summary.Fixed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                summary.Failed++;
                summary.Failures.Add($"{issue.FullPath}: {ex.Message}");
                progress?.Report($"Failed to fix {issue.FullPath}: {ex.Message}");
            }
        }
        progress?.Report($"Fixing done: {summary.Fixed} fixed, {summary.Failed} failed.");
        return summary;
    }

    private static void ApplyOne(ScanIssue issue, IProgress<string>? progress)
    {
        switch (issue.Action)
        {
            case FixAction.DeleteValue:
                DeleteValue(issue);
                break;

            case FixAction.DeleteKey:
                DeleteKey(issue);
                break;

            case FixAction.DecrementSharedDll:
                DecrementSharedDll(issue);
                break;

            default:
                throw new InvalidOperationException($"Unsupported fix action: {issue.Action}");
        }
        progress?.Report($"Fixed {issue.FullPath}");
    }

    private static void DeleteValue(ScanIssue issue)
    {
        using var key = RegistryWalker.OpenKeyWritable(issue.Hive, issue.KeyPath)
            ?? throw new InvalidOperationException("Key missing or no write access.");
        if (issue.ValueName is null) throw new InvalidOperationException("Issue has no value name.");
        key.DeleteValue(issue.ValueName, throwOnMissingValue: false);
    }

    private static void DeleteKey(ScanIssue issue)
    {
        var parent = GetParent(issue.KeyPath, out var leaf);
        if (leaf.Length == 0) throw new InvalidOperationException("Cannot delete a hive root.");
        if (RegistryWalker.IsProtected(issue.Hive, parent)) throw new InvalidOperationException("Parent key is protected.");

        using var key = RegistryWalker.OpenKeyWritable(issue.Hive, parent)
            ?? throw new InvalidOperationException("Parent key missing or no write access.");
        try
        {
            // Leaf delete first; a non-empty key raises InvalidOperationException.
            key.DeleteSubKey(leaf);
        }
        catch (InvalidOperationException)
        {
            try
            {
                key.DeleteSubKeyTree(leaf);
            }
            catch (ArgumentException)
            {
                // key vanished between attempts -> treat as fixed
            }
        }
    }

    private static void DecrementSharedDll(ScanIssue issue)
    {
        var dllPath = issue.ValueName ?? throw new InvalidOperationException("Missing DLL path value name.");

        using var key = RegistryWalker.OpenKeyWritable("HKLM", SharedDllsPath)
            ?? throw new InvalidOperationException("SharedDLLS key missing or no write access.");

        var raw = key.GetValue(dllPath);
        var count = raw switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var n) => n,
            null => throw new InvalidOperationException("Reference count value already removed."),
            _ => Convert.ToInt32(raw),
        };

        count--;
        if (count <= 0)
            key.DeleteValue(dllPath, throwOnMissingValue: false); // nobody references the DLL anymore
        else
            key.SetValue(dllPath, count, RegistryValueKind.DWord);
    }

    private static string GetParent(string keyPath, out string leaf)
    {
        var idx = keyPath.LastIndexOf('\\');
        if (idx < 0)
        {
            leaf = keyPath;
            return string.Empty;
        }
        leaf = keyPath[(idx + 1)..];
        return keyPath[..idx];
    }
}
