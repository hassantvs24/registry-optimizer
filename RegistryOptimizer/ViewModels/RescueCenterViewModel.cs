using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RegistryOptimizer.Core.Backup;

namespace RegistryOptimizer.ViewModels;

public partial class RescueCenterViewModel : ViewModelBase
{
    private readonly AppServices _services;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasSessions;
    [ObservableProperty] private bool _noSessions = true;

    public ObservableCollection<BackupSession> Sessions { get; } = new();
    public ObservableCollection<string> IgnoreList { get; } = new();

    public RescueCenterViewModel(AppServices services)
    {
        _services = services;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        Sessions.Clear();
        foreach (var session in _services.Backup.ListSessions()) Sessions.Add(session);
        HasSessions = Sessions.Count > 0;
        NoSessions = !HasSessions;

        IgnoreList.Clear();
        foreach (var identity in _services.Settings.IgnoreList) IgnoreList.Add(identity);
    }

    [RelayCommand]
    private async Task RestoreAsync(BackupSession? session)
    {
        if (session is null || IsBusy) return;
        var confirm = System.Windows.MessageBox.Show(
            $"Restore backup from {session.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} ({session.Kind})?\n\n" +
            $"This imports {session.RegFiles.Count} .reg file(s) back into the registry.",
            "Rescue Center", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);
            var result = await _services.Undo.RollbackAsync(session, progress);

            var failures = result.Failures.Count > 0
                ? "\n\nFailed files:\n" + string.Join(Environment.NewLine, result.Failures.Take(10))
                : string.Empty;
            System.Windows.MessageBox.Show(
                $"Restore complete: {result.Restored} file(s) imported, {result.Failed} failed.{failures}",
                "Rescue Center", System.Windows.MessageBoxButton.OK,
                result.Failed == 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
            StatusText = $"Restored {result.Restored} file(s), {result.Failed} failed.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Restore failed: {ex.Message}", "Rescue Center",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            StatusText = $"Restore failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void Delete(BackupSession? session)
    {
        if (session is null) return;
        var confirm = System.Windows.MessageBox.Show(
            $"Delete backup from {session.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}?\n\n{session.Folder}\n\nThis cannot be undone.",
            "Rescue Center", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            _services.Undo.DeleteSession(session);
            StatusText = $"Deleted backup {session.Id}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not delete the backup: {ex.Message}",
                "Rescue Center", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        Refresh();
    }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Creating backup ...";
        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);
            var session = await _services.Backup.BackupManualAsync(progress);
            StatusText = $"Backup {session.Id} saved ({session.KeyCount} keys).";
            System.Windows.MessageBox.Show(
                $"Backup created:\n{session.Folder}\n\n{session.KeyCount} key(s) exported.",
                "Rescue Center", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Backup failed: {ex.Message}", "Rescue Center",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            StatusText = $"Backup failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void RemoveIgnore(string? identity)
    {
        if (identity is null) return;
        _services.Settings.RemoveIgnore(identity);
        _services.SaveSettings();
        IgnoreList.Remove(identity);
        StatusText = $"Removed '{identity}' from the ignore list.";
    }
}
