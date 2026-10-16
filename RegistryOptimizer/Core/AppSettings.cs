using System.Text.Json;
using System.Text.Json.Serialization;
using RegistryOptimizer.Core.Scanning;

namespace RegistryOptimizer.Core;

/// <summary>Persisted application settings (JSON, %ProgramData%\RegistryOptimizer\settings.json).</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>ScanIssue.Identity values the user chose to ignore.</summary>
    public List<string> IgnoreList { get; set; } = new();

    /// <summary>Names of ScanCategory values excluded from scans.</summary>
    public List<string> ExcludedCategories { get; set; } = new();

    public int RetentionSessions { get; set; } = 10;

    public DateTime? LastScanUtc { get; set; }
    public int LastScanIssueCount { get; set; }
    public ScanProfile? LastScanProfile { get; set; }

    public double? LastFragmentationPercent { get; set; }
    public DateTime? LastDefragUtc { get; set; }

    public ScanScheduleSettings? Schedule { get; set; }

    [JsonIgnore]
    public IReadOnlySet<string> IgnoredSet => IgnoreList.ToHashSet(StringComparer.Ordinal);

    [JsonIgnore]
    public IReadOnlySet<ScanCategory> ExcludedSet
    {
        get
        {
            var set = new HashSet<ScanCategory>();
            foreach (var name in ExcludedCategories)
                if (Enum.TryParse<ScanCategory>(name, out var c)) set.Add(c);
            return set;
        }
    }

    public void AddIgnore(string identity)
    {
        if (!IgnoreList.Contains(identity)) IgnoreList.Add(identity);
    }

    public void RemoveIgnore(string identity) => IgnoreList.Remove(identity);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // corrupted settings -> fall back to defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(AppPaths.SettingsFile, json);
    }
}

public sealed class ScanScheduleSettings
{
    public bool Enabled { get; set; }
    public DayOfWeek Day { get; set; } = DayOfWeek.Sunday;
    public string TimeOfDay { get; set; } = "10:00"; // HH:mm
}
