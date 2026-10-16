using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Scanning.Scanners;

/// <summary>
/// COM classes whose InprocServer32 / LocalServer32 server file is gone. Missing files
/// under Windows system directories or WinSxS are skipped (they get re-registered), as
/// are servers whose directory still exists (WOW64 bitness mismatches). Deep profile only.
/// </summary>
public sealed class InvalidComClsidScanner : IIssueScanner
{
    private const string ClsidRoot = @"CLSID";
    private const int MaxDepth = 2;
    private const int MaxKeys = 15000;

    private static readonly string[] ServerSubKeys = { "InprocServer32", "LocalServer32" };

    public ScanCategory Category => ScanCategory.InvalidComObjects;
    public Severity Severity => Severity.Advanced;
    public bool Supports(ScanProfile profile) => profile == ScanProfile.Deep; // deep only

    public Task ScanAsync(ScanContext context, IProgress<ScanIssue> progress, CancellationToken ct)
    {
        foreach (var clsidPath in RegistryWalker.WalkSubKeys("HKCR", ClsidRoot, MaxDepth, MaxKeys))
        {
            ct.ThrowIfCancellationRequested();
            var guid = clsidPath[(clsidPath.LastIndexOf('\\') + 1)..];
            if (!guid.StartsWith('{') || !guid.EndsWith('}')) continue; // only direct {GUID} children

            try
            {
                foreach (var subKey in ServerSubKeys)
                {
                    var serverPath = PathHelper.ExtractFilePath(
                        RegistryWalker.DataToString(RegistryWalker.GetValue("HKCR", $@"{clsidPath}\{subKey}", null)));
                    if (!context.FileReferenceMissing(serverPath)) continue;
                    if (ShouldSkip(serverPath!)) break; // server vanished but the class is recoverable

                    Report(context, progress, new ScanIssue
                    {
                        Category = Category, Severity = Severity.Advanced, Hive = "HKCR", KeyPath = clsidPath,
                        ValueName = null, DataPreview = serverPath,
                        Reason = $"COM server '{serverPath}' is missing; the class is no longer functional",
                        Action = FixAction.DeleteKey,
                    });
                    break; // one issue per {GUID}
                }
            }
            catch (Exception)
            {
                // one bad key must never abort the scanner
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>System-directory and WinSxS servers get re-registered by Windows; existing directories mean bitness mismatch.</summary>
    private static bool ShouldSkip(string serverPath)
    {
        var p = serverPath.Replace('/', '\\');
        if (p.Contains(@"\windows\system32\", StringComparison.OrdinalIgnoreCase) ||
            p.Contains(@"\windows\syswow64\", StringComparison.OrdinalIgnoreCase) ||
            p.Contains(@"\winsxs\", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var dir = Path.GetDirectoryName(p);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
        }
        catch
        {
            return false;
        }
    }

    private void Report(ScanContext context, IProgress<ScanIssue> progress, ScanIssue issue)
    {
        if (!issue.CanFix || context.IsIgnored(issue)) return;
        progress.Report(issue);
    }
}
