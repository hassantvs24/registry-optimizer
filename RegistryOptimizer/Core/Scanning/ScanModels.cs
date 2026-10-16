namespace RegistryOptimizer.Core.Scanning;

/// <summary>Issue categories shown grouped in the UI.</summary>
public enum ScanCategory
{
    MissingFileReferences,
    SharedDlls,
    OrphanedUninstallEntries,
    BrokenFileAssociations,
    StartupEntries,
    InvalidComObjects,
    MruHistory,
    OrphanedKeys,
}

/// <summary>How risky fixing an issue is. Safe items are pre-selected; Advanced are not.</summary>
public enum Severity
{
    Safe,
    Advanced,
}

/// <summary>What the fixer should do for a given issue.</summary>
public enum FixAction
{
    None,
    DeleteValue,
    DeleteKey,
    DecrementSharedDll,
}

public enum ScanProfile
{
    Quick,
    Deep,
}

/// <summary>A single registry problem found by a scanner.</summary>
public sealed class ScanIssue
{
    public required ScanCategory Category { get; init; }
    public Severity Severity { get; init; } = Severity.Safe;
    /// <summary>Logical hive name: HKLM, HKCU, HKCR, HKU.</summary>
    public required string Hive { get; init; }
    /// <summary>Key path without the hive prefix, e.g. SOFTWARE\Microsoft\Windows\...</summary>
    public required string KeyPath { get; init; }
    /// <summary>Name of the offending value, or null when the key itself is the problem.</summary>
    public string? ValueName { get; init; }
    /// <summary>Short preview of the offending data (e.g. the referenced path).</summary>
    public string? DataPreview { get; init; }
    /// <summary>Human-readable explanation shown in the results grid.</summary>
    public string Reason { get; init; } = string.Empty;
    public FixAction Action { get; init; } = FixAction.None;

    public bool CanFix => Action != FixAction.None;

    /// <summary>Stable identity used by the ignore list and the fixer.</summary>
    public string Identity => $"{Hive}|{KeyPath}|{ValueName ?? "<key>"}|{Category}";

    public string FullPath => $"{Hive}\\{KeyPath}{(ValueName is null ? string.Empty : $"\\{ValueName}")}";
}

/// <summary>Aggregated outcome of a full scan run.</summary>
public sealed class ScanResult
{
    public required ScanProfile Profile { get; init; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime CompletedUtc { get; set; }
    public bool Cancelled { get; set; }
    public List<ScanIssue> Issues { get; } = new();
    public TimeSpan Duration => CompletedUtc - StartedUtc;

    public int SafeCount => Issues.Count(i => i.Severity == Severity.Safe);
    public int AdvancedCount => Issues.Count(i => i.Severity == Severity.Advanced);

    public IReadOnlyDictionary<ScanCategory, int> CountByCategory =>
        Issues.GroupBy(i => i.Category).ToDictionary(g => g.Key, g => g.Count());
}
