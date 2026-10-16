using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Scanning.Scanners;

/// <summary>
/// Autostart entries (Run keys, Winlogon Shell/Userinit) whose executable no longer
/// exists. Only values with absolute filesystem paths are considered.
/// </summary>
public sealed class StartupEntryScanner : IIssueScanner
{
    /// <summary>Run-style keys under both HKLM and HKCU.</summary>
    private static readonly string[] RunKeys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnceEx",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run",
    };

    private const string Winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private static readonly string[] Hives = { "HKLM", "HKCU" };

    public ScanCategory Category => ScanCategory.StartupEntries;
    public Severity Severity => Severity.Safe;
    public bool Supports(ScanProfile profile) => true; // both profiles

    public Task ScanAsync(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct)
    {
        foreach (var hive in Hives)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var runKey in RunKeys)
                ScanRunKey(context, progress, hive, runKey, ct);
        }

        ScanWinlogon(context, progress, ct);
        return Task.CompletedTask;
    }

    /// <summary>Run-style values: data is a command line; test the executable it launches.</summary>
    private void ScanRunKey(ScanContext context, IProgress<ScanIssue> progress, string hive, string keyPath, CancellationToken ct)
    {
        foreach (var (valueName, data) in RegistryWalker.Values(hive, keyPath))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var command = RegistryWalker.DataToString(data);
                if (!LooksLikeAbsolutePath(command)) continue; // PATH-resolved names are out of scope

                var exe = PathHelper.ExtractFilePath(command);
                if (!context.FileReferenceMissing(exe)) continue;

                Report(context, progress, new ScanIssue
                {
                    Category = Category, Severity = Severity.Safe, Hive = hive, KeyPath = keyPath,
                    ValueName = valueName, DataPreview = exe,
                    Reason = "Startup entry points to a missing executable", Action = FixAction.DeleteValue,
                });
            }
            catch (Exception)
            {
                // one bad value must never abort the scanner
            }
        }
    }

    /// <summary>Winlogon: Shell and Userinit. Only path-like tokens are tested.</summary>
    private void ScanWinlogon(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct)
    {
        const string hive = "HKLM";

        // Userinit: comma-separated list of executables.
        var userinit = RegistryWalker.DataToString(RegistryWalker.GetValue(hive, Winlogon, "Userinit"));
        if (!string.IsNullOrWhiteSpace(userinit))
        {
            foreach (var token in userinit.Split(','))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var entry = token.Trim();
                    if (!LooksLikeAbsolutePath(entry)) continue; // e.g. plain "userinit.exe" is resolved by Winlogon
                    var exe = PathHelper.ExtractFilePath(entry);
                    if (!context.FileReferenceMissing(exe)) continue;

                    Report(context, progress, new ScanIssue
                    {
                        Category = Category, Severity = Severity.Safe, Hive = hive, KeyPath = Winlogon,
                        ValueName = "Userinit", DataPreview = exe,
                        Reason = "Userinit entry points to a missing executable", Action = FixAction.DeleteValue,
                    });
                }
                catch (Exception)
                {
                }
            }
        }

        // Shell: comma-separated list; normally "explorer.exe" (bare name -> skipped above rule applies per token).
        var shell = RegistryWalker.DataToString(RegistryWalker.GetValue(hive, Winlogon, "Shell"));
        if (string.IsNullOrWhiteSpace(shell)) return;
        foreach (var token in shell.Split(','))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entry = token.Trim();
                if (!LooksLikeAbsolutePath(entry)) continue;
                var exe = PathHelper.ExtractFilePath(entry);
                if (!context.FileReferenceMissing(exe)) continue;

                Report(context, progress, new ScanIssue
                {
                    Category = Category, Severity = Severity.Safe, Hive = hive, KeyPath = Winlogon,
                    ValueName = "Shell", DataPreview = exe,
                    Reason = "Shell entry points to a missing executable", Action = FixAction.DeleteValue,
                });
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Bare executable names are resolved via PATH and must not be flagged.</summary>
    private static bool LooksLikeAbsolutePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        return command.Contains(":\\", StringComparison.Ordinal) || command.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private void Report(ScanContext context, IProgress<ScanIssue> progress, ScanIssue issue)
    {
        if (!issue.CanFix || context.IsIgnored(issue)) return;
        progress.Report(issue);
    }
}
