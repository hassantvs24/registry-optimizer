using System.Text.Json;
using Microsoft.Win32;
using RegistryOptimizer.Core.Registry;

namespace RegistryOptimizer.Core.Defrag;

/// <summary>Persisted state of the boot-time defrag flow (JSON, %ProgramData%\RegistryOptimizer\defrag-status.json).</summary>
public sealed class DefragStatus
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool Scheduled { get; set; }
    public DateTime? ScheduledUtc { get; set; }
    public bool BootRunCompleted { get; set; }
    public DateTime? BootRunUtc { get; set; }
    public string? LastReportFile { get; set; }

    public static DefragStatus Load()
    {
        try
        {
            if (File.Exists(AppPaths.DefragStatusFile))
            {
                var status = JsonSerializer.Deserialize<DefragStatus>(File.ReadAllText(AppPaths.DefragStatusFile), JsonOptions);
                if (status is not null) return status;
            }
        }
        catch
        {
            // corrupted status -> fall back to defaults
        }
        return new DefragStatus();
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.DefragStatusFile, JsonSerializer.Serialize(this, JsonOptions));
    }
}

/// <summary>Registers/removes the RunOnce boot-time defrag helper entry.</summary>
public static class DefragScheduler
{
    public const string RunOnceValueName = "RegistryOptimizerDefrag";
    private const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";

    /// <summary>Register the exe to run with --boot-defrag at next boot, and mark status as scheduled.</summary>
    public static void Schedule()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            throw new InvalidOperationException("Could not determine the application executable path.");

        // The helper must live on a fixed local drive: RunOnce runs before any
        // network/USB storage is available, so a UNC or removable path would fail.
        string? root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(exePath));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not determine the drive of '{exePath}': {ex.Message}", ex);
        }
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            throw new InvalidOperationException($"The application must reside on a local drive to schedule a boot-time defrag (path: '{exePath}').");
        try
        {
            if (new DriveInfo(root.Substring(0, 2)).DriveType != DriveType.Fixed)
                throw new InvalidOperationException($"The application is on a non-fixed drive ('{root}'); boot-time defrag requires a local fixed drive.");
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"The application is on an unsupported drive ('{root}'); boot-time defrag requires a local fixed drive.", ex);
        }

        NativeMethods.EnableBackupRestorePrivileges();

        // NB: 'Registry.LocalMachine' would resolve to the RegistryOptimizer.Core.Registry namespace
        // (namespace shadows the Microsoft.Win32.Registry type here), so go through RegistryWalker.
        var hklm = RegistryWalker.MapHive("HKLM");
        using var key = hklm.OpenSubKey(RunOnceKey, writable: true)
            ?? hklm.CreateSubKey(RunOnceKey);
        key.SetValue(RunOnceValueName, $"\"{exePath}\" --boot-defrag");

        var status = DefragStatus.Load();
        status.Scheduled = true;
        status.ScheduledUtc = DateTime.UtcNow;
        status.Save();
    }

    /// <summary>True when the RunOnce helper entry exists.</summary>
    public static bool IsScheduled()
    {
        try
        {
            using var key = RegistryWalker.MapHive("HKLM").OpenSubKey(RunOnceKey, writable: false);
            return key?.GetValue(RunOnceValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Remove the RunOnce helper entry and clear the scheduled flag.</summary>
    public static void Cancel()
    {
        try
        {
            using var key = RegistryWalker.MapHive("HKLM").OpenSubKey(RunOnceKey, writable: true);
            key?.DeleteValue(RunOnceValueName, throwOnMissingValue: false);
        }
        catch
        {
            // best effort; absence of the value is the goal
        }

        var status = DefragStatus.Load();
        status.Scheduled = false;
        status.Save();
    }

    /// <summary>Path for a new boot-defrag report file.</summary>
    public static string BootDefragReportPath()
    {
        AppPaths.EnsureCreated();
        return Path.Combine(AppPaths.DefragReportsRoot, $"defrag-report-{DateTime.UtcNow.Ticks}.json");
    }
}
