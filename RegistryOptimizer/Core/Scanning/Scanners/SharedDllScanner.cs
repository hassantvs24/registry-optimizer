using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Scanning.Scanners;

/// <summary>
/// SharedDLLS tracks how many installations reference a DLL via reference counting.
/// When the DLL file is gone and the count has dropped to its last reference, the
/// leftover entry can be safely decremented away.
/// </summary>
public sealed class SharedDllScanner : IIssueScanner
{
    private const string SharedDlls = @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLS";

    public ScanCategory Category => ScanCategory.SharedDlls;
    public Severity Severity => Severity.Safe;
    public bool Supports(ScanProfile profile) => true; // both profiles

    public Task ScanAsync(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct)
    {
        // The value NAME is the DLL path; the DWORD data is the reference count.
        foreach (var (name, data) in RegistryWalker.Values("HKLM", SharedDlls))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var count = ReadCount(data);
                // Only flag the last dangling reference; higher counts may still be in use.
                if (count > 1) continue;
                if (!context.FileReferenceMissing(name)) continue;

                Report(context, progress, new ScanIssue
                {
                    Category = Category, Severity = Severity.Safe, Hive = "HKLM", KeyPath = SharedDlls,
                    ValueName = name, DataPreview = name,
                    Reason = $"DLL is missing and its reference count is down to {count}",
                    Action = FixAction.DecrementSharedDll,
                });
            }
            catch (Exception)
            {
                // one bad value must never abort the scanner
            }
        }
        return Task.CompletedTask;
    }

    private static int ReadCount(object? data) => data switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, out var n) => n,
        _ => 0, // unparseable -> treat as last reference
    };

    private void Report(ScanContext context, IProgress<ScanIssue> progress, ScanIssue issue)
    {
        if (!issue.CanFix || context.IsIgnored(issue)) return;
        progress.Report(issue);
    }
}
