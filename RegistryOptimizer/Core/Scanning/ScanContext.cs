using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Scanning;

/// <summary>Shared services handed to every scanner while a scan runs.</summary>
public sealed class ScanContext
{
    /// <summary>Identities (ScanIssue.Identity) the user asked never to see again.</summary>
    public required IReadOnlySet<string> IgnoreList { get; init; }

    public ScanProfile Profile { get; init; } = ScanProfile.Quick;

    /// <summary>Cached file-existence check with environment-variable expansion.</summary>
    private readonly Dictionary<string, bool> _fileCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the registry data references a file/path that does not exist.</summary>
    public bool FileReferenceMissing(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        var expanded = PathHelper.Expand(reference.Trim().Trim('"'));
        if (PathHelper.IsSkippableReference(expanded)) return false;
        if (_fileCache.TryGetValue(expanded, out var cached)) return cached;
        bool missing;
        try
        {
            missing = !File.Exists(expanded) && !Directory.Exists(expanded);
        }
        catch
        {
            missing = false; // unreadable -> don't flag
        }
        _fileCache[expanded] = missing;
        return missing;
    }

    public bool IsIgnored(ScanIssue issue) => IgnoreList.Contains(issue.Identity);
}
