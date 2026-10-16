using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Defrag;

/// <summary>Locates registry hive files on disk: system config hives + per-user hives.</summary>
public static class HiveLocator
{
    private static readonly string[] ConfigHiveNames = { "SYSTEM", "SOFTWARE", "DEFAULT", "SECURITY", "SAM", "BBI" };

    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>Find all hives that exist on this machine.</summary>
    public static List<HiveInfo> Locate()
    {
        var hives = new List<HiveInfo>();
        AddConfigHives(hives);
        AddUserHives(hives);
        return hives;
    }

    private static void AddConfigHives(List<HiveInfo> hives)
    {
        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "config");
        foreach (var name in ConfigHiveNames)
        {
            var path = Path.Combine(configDir, name);
            if (!File.Exists(path)) continue;
            hives.Add(new HiveInfo
            {
                Name = name,
                LogicalKey = $@"HKLM\{name}",
                FilePath = path,
                IsSystemHive = true,
            });
        }
    }

    private static void AddUserHives(List<HiveInfo> hives)
    {
        foreach (var sid in RegistryWalker.SubKeyNames("HKLM", ProfileListKey))
        {
            // Skip backup copies and non-user SIDs (system accounts like S-1-5-18 don't matter here,
            // but we specifically only want real user profile SIDs S-1-5-21-*).
            if (sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
            if (!sid.StartsWith("S-1-5-21", StringComparison.OrdinalIgnoreCase)) continue;

            var profilePath = RegistryWalker.GetValue("HKLM", $@"{ProfileListKey}\{sid}", "ProfileImagePath") as string;
            if (string.IsNullOrWhiteSpace(profilePath)) continue;
            try
            {
                profilePath = Environment.ExpandEnvironmentVariables(profilePath);
            }
            catch
            {
                continue;
            }
            if (!Directory.Exists(profilePath)) continue;

            var dirName = new DirectoryInfo(profilePath).Name;
            AddIfExists(hives, Path.Combine(profilePath, "NTUSER.DAT"), $"NTUSER.DAT ({dirName})", isSystemHive: false);
            // Note: per-user hives are analyzed but never defragged at the runtime boot pass,
            // because RegReplaceKey on a mounted user's hive while logged in is unsafe.
            var usrClass = Path.Combine(profilePath, @"AppData\Local\Microsoft\Windows\UsrClass.dat");
            AddIfExists(hives, usrClass, $"UsrClass.dat ({dirName})", isSystemHive: false);
        }
    }

    private static void AddIfExists(List<HiveInfo> hives, string path, string name, bool isSystemHive)
    {
        if (!File.Exists(path)) return;
        hives.Add(new HiveInfo
        {
            Name = name,
            LogicalKey = string.Empty, // per-user hives are skipped by the runtime boot defrag
            FilePath = path,
            IsSystemHive = isSystemHive,
        });
    }
}
