using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RegistryOptimizer.Core;
using RegistryOptimizer.Core.Defrag;

namespace RegistryOptimizer.ViewModels;

public partial class DefragViewModel : ViewModelBase
{
    private readonly AppServices _services;

    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Run an analysis to see how fragmented your registry hives are.";
    [ObservableProperty] private double _overallPercent;
    [ObservableProperty] private Brush _gaugeBrush;
    [ObservableProperty] private bool _isScheduled;
    [ObservableProperty] private string _scheduleText = string.Empty;
    [ObservableProperty] private string _lastReportText = string.Empty;

    public ObservableCollection<HiveInfo> Hives { get; } = new();

    public DefragViewModel(AppServices services)
    {
        _services = services;
        _gaugeBrush = Brushes.Gray;
        RefreshState();

        var analysis = DefragAnalysis.LoadLatest();
        if (analysis is not null)
        {
            OverallPercent = analysis.OverallFragmentationPercent;
            Hives.Clear();
            foreach (var hive in analysis.Hives) Hives.Add(hive);
            StatusText = $"Last analysis: {analysis.AnalyzedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        }
        UpdateGauge();
    }

    partial void OnOverallPercentChanged(double value) => UpdateGauge();

    private void UpdateGauge() =>
        GaugeBrush = new SolidColorBrush(OverallPercent switch
        {
            < 10 => Color.FromRgb(0x22, 0xC5, 0x5E),   // Success
            < 25 => Color.FromRgb(0xF5, 0x9E, 0x0B),   // Warning
            _ => Color.FromRgb(0xEF, 0x44, 0x44),      // Danger
        });

    private void RefreshState()
    {
        IsScheduled = DefragScheduler.IsScheduled();
        var status = DefragStatus.Load();
        ScheduleText = IsScheduled
            ? $"Optimization scheduled at next boot (since {status.ScheduledUtc?.ToLocalTime():yyyy-MM-dd HH:mm})."
            : status.BootRunCompleted
                ? $"Last boot optimization: {status.BootRunUtc?.ToLocalTime():yyyy-MM-dd HH:mm}."
                : "No boot optimization scheduled.";
        LoadLastReport(status);
    }

    private void LoadLastReport(DefragStatus status)
    {
        try
        {
            if (status.LastReportFile is null || !File.Exists(status.LastReportFile))
            {
                LastReportText = "No optimization report yet.";
                return;
            }
            var report = JsonSerializer.Deserialize<DefragReport>(
                File.ReadAllText(status.LastReportFile), DefragReport.JsonOptions);
            if (report is null || report.Hives.Count == 0)
            {
                LastReportText = "No optimization report yet.";
                return;
            }
            var lines = report.Hives.Select(h =>
            {
                var before = h.BeforeBytes / 1024.0 / 1024.0;
                var after = h.AfterBytes / 1024.0 / 1024.0;
                var free = h.BeforeFreePercent is null ? "" :
                    $", fragmentation {h.BeforeFreePercent:F1}% -> {(h.AfterFreePercent ?? h.BeforeFreePercent):F1}%";
                var state = h.Success ? "OK"
                    : h.Skipped ? $"skipped - {h.SkippedReason}"
                    : $"FAILED - {h.Error}";
                return h.Success || !h.Skipped
                    ? $"{h.Name}: {before:F1} MB -> {after:F1} MB{free} ({state})"
                    : $"{h.Name}: {state}";
            });
            LastReportText = $"Report from {report.RanAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n" + string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            LastReportText = $"Could not read the last report: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsAnalyzing) return;
        IsAnalyzing = true;
        StatusText = "Locating registry hives ...";
        Hives.Clear();
        try
        {
            var hives = await Task.Run(() => HiveLocator.Locate());
            var progress = new Progress<string>(msg => StatusText = msg);
            var analysis = await Task.Run(() => HiveFragmentationAnalyzer.Analyze(hives, progress));

            analysis.Save();
            _services.Settings.LastFragmentationPercent = analysis.OverallFragmentationPercent;
            _services.SaveSettings();

            Hives.Clear();
            foreach (var hive in analysis.Hives) Hives.Add(hive);
            OverallPercent = analysis.OverallFragmentationPercent;
            StatusText = $"Analysis complete: {analysis.Hives.Count} hive(s), " +
                         $"{analysis.OverallFragmentationPercent:F1}% average fragmentation.";
        }
        catch (Exception ex)
        {
            StatusText = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private async Task OptimizeAsync()
    {
        if (IsBusy) return;
        var explain = System.Windows.MessageBox.Show(
            "Registry optimization works in two reboots:\n\n" +
            "1. This app registers a one-time boot helper and backs up the registry.\n" +
            "2. At the NEXT boot the helper prepares compacted hive files.\n" +
            "3. Windows swaps in the compacted hives at the boot AFTER that.\n\n" +
            "A full registry backup is required before scheduling. Continue?",
            "Optimize registry", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (explain != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            StatusText = "Creating registry backup ...";
            var progress = new Progress<string>(msg => StatusText = msg);
            var backup = await _services.Backup.BackupManualAsync(progress);

            StatusText = "Scheduling boot optimization ...";
            await Task.Run(() => DefragScheduler.Schedule());

            RefreshState();
            System.Windows.MessageBox.Show(
                $"Backup saved to:\n{backup.Folder}\n\n" +
                "Boot optimization is scheduled and will run at the next startup.\n" +
                "The compacted registry hives take effect one reboot later.",
                "Optimization scheduled", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not schedule the optimization:\n{ex.Message}",
                "Optimize registry", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            RefreshState();
        }
    }

    [RelayCommand]
    private void CancelSchedule()
    {
        try
        {
            DefragScheduler.Cancel();
            RefreshState();
            System.Windows.MessageBox.Show("The scheduled boot optimization was cancelled.",
                "Optimize registry", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not cancel: {ex.Message}",
                "Optimize registry", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
