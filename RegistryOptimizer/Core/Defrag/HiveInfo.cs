using System.Text.Json;

namespace RegistryOptimizer.Core.Defrag;

/// <summary>Describes a single registry hive file (location + analysis metrics).</summary>
public sealed class HiveInfo
{
    /// <summary>e.g. "SYSTEM", "SOFTWARE", "NTUSER.DAT (john)"</summary>
    public required string Name { get; init; }

    /// <summary>e.g. "HKLM\SYSTEM" — used by RegSaveKey/RegReplaceKey. Empty for per-user hives.</summary>
    public required string LogicalKey { get; init; }

    /// <summary>e.g. C:\Windows\System32\config\SYSTEM</summary>
    public required string FilePath { get; init; }

    /// <summary>true for config hives; false for NTUSER/UsrClass (analyzed only, not defragged at runtime boot).</summary>
    public bool IsSystemHive { get; init; }

    public long FileSizeBytes { get; set; }

    /// <summary>Internal hive slack, 0..100.</summary>
    public double FreeSpacePercent { get; set; }

    /// <summary>0..100</summary>
    public double FragmentationPercent { get; set; }

    public string? Error { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusText => Error ?? $"{FragmentationPercent:F1}% fragmented, {FreeSpacePercent:F1}% free space";
}

/// <summary>Persisted result of a hive fragmentation analysis.</summary>
public sealed class DefragAnalysis
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<HiveInfo> Hives { get; } = new();

    public DateTime AnalyzedUtc { get; } = DateTime.UtcNow;

    public double OverallFragmentationPercent =>
        Hives.Where(h => h.Error is null).Select(h => h.FragmentationPercent).DefaultIfEmpty(0).Average();

    /// <summary>Persist as analysis-&lt;utcTicks&gt;.json under the defrag reports folder.</summary>
    public void Save()
    {
        AppPaths.EnsureCreated();
        var path = Path.Combine(AppPaths.DefragReportsRoot, $"analysis-{AnalyzedUtc.Ticks}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Read the newest analysis-*.json, or null when none exists / all are corrupt.</summary>
    public static DefragAnalysis? LoadLatest()
    {
        try
        {
            var dir = AppPaths.DefragReportsRoot;
            if (!Directory.Exists(dir)) return null;
            var latest = Directory.EnumerateFiles(dir, "analysis-*.json")
                .Select(p => (Path: p, Ticks: ParseTicks(Path.GetFileName(p))))
                .Where(x => x.Ticks > 0)
                .OrderByDescending(x => x.Ticks)
                .Select(x => x.Path)
                .FirstOrDefault();
            if (latest is null) return null;
            return JsonSerializer.Deserialize<DefragAnalysis>(File.ReadAllText(latest), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static long ParseTicks(string fileName)
    {
        // analysis-<ticks>.json
        var start = fileName.IndexOf('-') + 1;
        var len = fileName.LastIndexOf('.') - start;
        return long.TryParse(fileName.Substring(start, len), out var ticks) ? ticks : 0;
    }
}
