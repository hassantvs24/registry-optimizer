using RegistryOptimizer.Core;
using RegistryOptimizer.Core.Backup;
using RegistryOptimizer.Core.Scanning;

namespace RegistryOptimizer;

/// <summary>Shared services handed to every ViewModel via its constructor.</summary>
public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    public AppSettings Settings { get; }
    public ScanService Scanner { get; }
    public BackupService Backup { get; }
    public UndoService Undo { get; }

    public void SaveSettings() => Settings.Save();

    private AppServices()
    {
        Settings = AppSettings.Load();
        Scanner = new ScanService(Settings);
        Backup = new BackupService(Settings);
        Undo = new UndoService(Settings);
    }
}
