using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RegistryOptimizer.Core;
using RegistryOptimizer.Core.Scanning;
using RegistryOptimizer.Core.Scheduling;

namespace RegistryOptimizer.ViewModels;

/// <summary>One ScanCategory with its excluded state, bound to a settings checkbox.</summary>
public partial class CategoryExclusion : ObservableObject
{
    public ScanCategory Category { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isExcluded;

    public CategoryExclusion(ScanCategory category, string label, bool isExcluded)
    {
        Category = category;
        Label = label;
        _isExcluded = isExcluded;
    }
}

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;

    [ObservableProperty] private int _retentionSessions = 10;
    [ObservableProperty] private bool _isScheduled;
    [ObservableProperty] private DayOfWeek _scheduleDay = DayOfWeek.Sunday;
    [ObservableProperty] private string _timeOfDay = "10:00";

    public ObservableCollection<CategoryExclusion> ExcludedCategories { get; } = new();
    public Array DaysOfWeek => Enum.GetValues<DayOfWeek>();

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        Load();
    }

    private void Load()
    {
        ExcludedCategories.Clear();
        var excluded = _services.Settings.ExcludedSet;
        foreach (ScanCategory category in Enum.GetValues<ScanCategory>())
        {
            ExcludedCategories.Add(new CategoryExclusion(
                category, ScanIssueRow.CategoryLabel(category), excluded.Contains(category)));
        }
        RetentionSessions = Math.Clamp(_services.Settings.RetentionSessions, 1, 50);

        var schedule = _services.Settings.Schedule ?? new ScanScheduleSettings();
        ScheduleDay = schedule.Day;
        TimeOfDay = schedule.TimeOfDay;
        try
        {
            IsScheduled = ScanScheduler.IsEnabled();
        }
        catch
        {
            IsScheduled = schedule.Enabled;
        }
    }

    [RelayCommand]
    private void ApplySchedule()
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(TimeOfDay ?? string.Empty, @"^([01]\d|2[0-3]):[0-5]\d$"))
        {
            System.Windows.MessageBox.Show(
                "Enter the time in 24-hour HH:mm format (e.g. 10:30).",
                "Settings", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        try
        {
            var settings = _services.Settings;
            settings.Schedule = new ScanScheduleSettings
            {
                Enabled = IsScheduled,
                Day = ScheduleDay,
                TimeOfDay = TimeOfDay,
            };

            if (IsScheduled)
            {
                ScanScheduler.Enable(settings);
            }
            else
            {
                ScanScheduler.Disable();
            }
            settings.Save();
            System.Windows.MessageBox.Show(
                IsScheduled
                    ? $"Weekly scan scheduled for every {ScheduleDay} at {TimeOfDay}."
                    : "The weekly scheduled scan was disabled.",
                "Settings", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Settings",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Save()
    {
        var settings = _services.Settings;
        settings.ExcludedCategories = ExcludedCategories
            .Where(c => c.IsExcluded)
            .Select(c => c.Category.ToString())
            .ToList();
        settings.RetentionSessions = Math.Clamp(RetentionSessions, 1, 50);
        if (settings.Schedule is not null)
        {
            settings.Schedule.Enabled = IsScheduled;
            settings.Schedule.Day = ScheduleDay;
            settings.Schedule.TimeOfDay = TimeOfDay;
        }
        _services.SaveSettings();
        System.Windows.MessageBox.Show("Settings saved.", "Settings",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
}
