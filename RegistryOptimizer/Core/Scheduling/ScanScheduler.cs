using System.Globalization;
using System.Security.Principal;
using DayOfWeek = System.DayOfWeek; // the TaskScheduler package also defines DayOfWeek-like types
using Microsoft.Win32.TaskScheduler;

namespace RegistryOptimizer.Core.Scheduling;

/// <summary>
/// Manages the "RegistryOptimizer Weekly Scan" Windows Task Scheduler task that runs this
/// executable with the "--scheduled-scan" argument.
/// </summary>
public sealed class ScanScheduler
{
    public const string TaskName = "RegistryOptimizer Weekly Scan";

    private const string TaskDescription =
        "Weekly Quick registry scan with automatic fix of safe issues (Registry Optimizer).";

    private const string ScheduledScanArgument = "--scheduled-scan";

    /// <summary>Creates or updates the weekly task from <see cref="AppSettings.Schedule"/>.</summary>
    public static void Enable(AppSettings settings)
    {
        try
        {
            var schedule = settings.Schedule ?? new ScanScheduleSettings();
            var timeOfDay = ParseTimeOfDay(schedule.TimeOfDay);
            var exePath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "RegistryOptimizer.exe");

            using var service = new TaskService();

            var definition = service.NewTask();
            definition.RegistrationInfo.Description = TaskDescription;
            definition.Principal.LogonType = TaskLogonType.InteractiveToken; // run only when the user is logged on
            definition.Principal.RunLevel = TaskRunLevel.Highest;            // highest privileges available to the user
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;

            definition.Triggers.Add(new WeeklyTrigger
            {
                DaysOfWeek = ToDaysOfTheWeek(schedule.Day),
                StartBoundary = DateTime.Today.Add(timeOfDay),
            });

            definition.Actions.Add(new ExecAction(
                exePath, ScheduledScanArgument, Path.GetDirectoryName(exePath)));

            // InteractiveToken + the current user: no password storage required.
            service.RootFolder.RegisterTaskDefinition(
                TaskName, definition, TaskCreation.CreateOrUpdate,
                WindowsIdentity.GetCurrent().Name, null, TaskLogonType.InteractiveToken, null);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not enable the scheduled scan: {ex.Message}", ex);
        }
    }

    /// <summary>Deletes the task if it exists.</summary>
    public static void Disable()
    {
        try
        {
            using var service = new TaskService();
            service.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not disable the scheduled scan: {ex.Message}", ex);
        }
    }

    /// <summary>True when the weekly task exists.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var service = new TaskService();
            return service.RootFolder.Tasks.Exists(TaskName);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not query the Task Scheduler: {ex.Message}", ex);
        }
    }

    /// <summary>Day/time of the current task, or the defaults (Sunday 10:00) when not scheduled.</summary>
    public static (DayOfWeek Day, string Time) GetCurrentSchedule()
    {
        try
        {
            using var service = new TaskService();
            Microsoft.Win32.TaskScheduler.Task? task = service.RootFolder.Tasks.Exists(TaskName)
                ? service.RootFolder.Tasks[TaskName]
                : null;
            var weekly = task?.Definition.Triggers.OfType<WeeklyTrigger>().FirstOrDefault();
            if (weekly is not null)
            {
                return (FromDaysOfTheWeek(weekly.DaysOfWeek),
                    weekly.StartBoundary.ToString("HH:mm", CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not read the scheduled scan: {ex.Message}", ex);
        }
        return (DayOfWeek.Sunday, "10:00");
    }

    private static TimeSpan ParseTimeOfDay(string timeOfDay) =>
        TimeSpan.TryParseExact(timeOfDay, @"hh\:mm", CultureInfo.InvariantCulture, out var t)
            ? t
            : TimeSpan.FromHours(10);

    private static DaysOfTheWeek ToDaysOfTheWeek(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => DaysOfTheWeek.Monday,
        DayOfWeek.Tuesday => DaysOfTheWeek.Tuesday,
        DayOfWeek.Wednesday => DaysOfTheWeek.Wednesday,
        DayOfWeek.Thursday => DaysOfTheWeek.Thursday,
        DayOfWeek.Friday => DaysOfTheWeek.Friday,
        DayOfWeek.Saturday => DaysOfTheWeek.Saturday,
        _ => DaysOfTheWeek.Sunday,
    };

    private static DayOfWeek FromDaysOfTheWeek(DaysOfTheWeek days) => days switch
    {
        DaysOfTheWeek.Monday => DayOfWeek.Monday,
        DaysOfTheWeek.Tuesday => DayOfWeek.Tuesday,
        DaysOfTheWeek.Wednesday => DayOfWeek.Wednesday,
        DaysOfTheWeek.Thursday => DayOfWeek.Thursday,
        DaysOfTheWeek.Friday => DayOfWeek.Friday,
        DaysOfTheWeek.Saturday => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };
}
