namespace RegistryOptimizer.Core;

/// <summary>Well-known filesystem locations used by the app.</summary>
public static class AppPaths
{
    /// <summary>All mutable app data lives here so the exe itself stays portable.</summary>
    public static string DataRoot { get; } =
        Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "RegistryOptimizer");

    public static string BackupsRoot => Path.Combine(DataRoot, "Backups");
    public static string DefragReportsRoot => Path.Combine(DataRoot, "DefragReports");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string DefragStatusFile => Path.Combine(DataRoot, "defrag-status.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(BackupsRoot);
        Directory.CreateDirectory(DefragReportsRoot);
    }
}
