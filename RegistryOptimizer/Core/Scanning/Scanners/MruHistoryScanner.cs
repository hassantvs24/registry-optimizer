using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Scanning.Scanners;

/// <summary>
/// Text-based MRU history: Explorer's RunMRU and TypedPaths entries whose target no
/// longer exists. (PIDL-based MRUs like RecentDocs are binary blobs and out of scope.)
/// Deep profile only.
/// </summary>
public sealed class MruHistoryScanner : IIssueScanner
{
    private const string RunMru = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\RunMRU";
    private const string TypedPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths";

    public ScanCategory Category => ScanCategory.MruHistory;
    public Severity Severity => Severity.Advanced;
    public bool Supports(ScanProfile profile) => profile == ScanProfile.Deep; // deep only

    public Task ScanAsync(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct)
    {
        const string hive = "HKCU";

        // RunMRU: value data is a command, terminated by '?' (the MRUList value is skipped
        // because its single-letter data never looks like a path).
        foreach (var (valueName, data) in RegistryWalker.Values(hive, RunMru))
        {
            ct.ThrowIfCancellationRequested();
            if (valueName.Equals("MRUList", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var entry = RegistryWalker.DataToString(data)?.TrimEnd('?');
                var target = PathHelper.ExtractFilePath(entry);
                if (!context.FileReferenceMissing(target)) continue;

                Report(context, progress, new ScanIssue
                {
                    Category = Category, Severity = Severity.Advanced, Hive = hive, KeyPath = RunMru,
                    ValueName = valueName, DataPreview = target,
                    Reason = "Run MRU entry points to a missing file", Action = FixAction.DeleteValue,
                });
            }
            catch (Exception)
            {
                // one bad value must never abort the scanner
            }
        }

        // TypedPaths: url1, url2, ... hold typed folder paths (URLs are filtered by the context).
        foreach (var (valueName, data) in RegistryWalker.Values(hive, TypedPaths))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var target = RegistryWalker.DataToString(data);
                if (!context.FileReferenceMissing(target)) continue;

                Report(context, progress, new ScanIssue
                {
                    Category = Category, Severity = Severity.Advanced, Hive = hive, KeyPath = TypedPaths,
                    ValueName = valueName, DataPreview = target,
                    Reason = "Typed path no longer exists", Action = FixAction.DeleteValue,
                });
            }
            catch (Exception)
            {
            }
        }
        return Task.CompletedTask;
    }

    private void Report(ScanContext context, IProgress<ScanIssue> progress, ScanIssue issue)
    {
        if (!issue.CanFix || context.IsIgnored(issue)) return;
        progress.Report(issue);
    }
}
