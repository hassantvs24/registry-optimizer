using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RegistryOptimizer.Core;
using RegistryOptimizer.Core.Backup;
using RegistryOptimizer.Core.Defrag;
using RegistryOptimizer.Core.Scanning;

namespace RegistryOptimizer;

/// <summary>
/// Application startup. Three modes:
///  - normal UI (default),
///  - --boot-defrag: minimal log window running the boot-time defrag helper,
///  - --scheduled-scan: fully silent quick scan + auto-fix of safe issues.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureCreated();

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(AppPaths.DataRoot, "ui-error.log"),
                    $"{DateTime.UtcNow:u}  {args.Exception}{Environment.NewLine}");
            }
            catch { /* logging is best effort */ }
            MessageBox.Show($"Unexpected error: {args.Exception.Message}", "Registry Optimizer",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        if (e.Args.Contains("--boot-defrag"))
        {
            RunBootDefragMode();
        }
        else if (e.Args.Contains("--scheduled-scan"))
        {
            RunScheduledScanMode();
        }
        else
        {
            // Touch the singleton so settings load once and are shared by every VM.
            _ = AppServices.Instance;
            var window = new MainWindow();
            // The icon comes from embedded bytes: WPF single-file publishing cannot resolve
            // pack:// resources for Window.Icon at XAML-load time.
            window.Icon = AppIcon.Create();
            window.Show();
        }
    }

    /// <summary>Small dark log window shown briefly at boot while the defrag helper runs.</summary>
    private void RunBootDefragMode()
    {
        var logText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10),
        };

        var scroll = new ScrollViewer
        {
            Content = logText,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x19, 0x1C)),
        };

        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x19, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x48)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            Child = scroll,
        };

        var window = new Window
        {
            Title = "Registry Optimizer - Boot optimization",
            Width = 620,
            Height = 380,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x19, 0x1C)),
            Content = root,
            Topmost = true,
        };

        void AppendLog(string line)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => AppendLog(line));
                return;
            }
            logText.Text += line + Environment.NewLine;
            scroll.ScrollToEnd();
        }

        window.Loaded += async (_, _) =>
        {
            AppendLog("Starting boot-time registry optimization ...");
            int code;
            try
            {
                code = await Task.Run(() => BootDefragRunner.Run(AppendLog));
            }
            catch (Exception ex)
            {
                AppendLog($"Unexpected error: {ex.Message}");
                code = 1;
            }
            AppendLog("Finished.");
            await Task.Delay(TimeSpan.FromSeconds(10));
            Shutdown(code);
        };

        window.Show();
    }

    /// <summary>Silent scheduled scan: quick scan, auto-fix only safe issues, log to a file.</summary>
    private void RunScheduledScanMode()
    {
        _ = Task.Run(async () =>
        {
            var logFile = Path.Combine(AppPaths.DataRoot, "scheduled-scan-log.txt");
            var services = AppServices.Instance;
            var settings = services.Settings;
            var exitCode = 0;
            try
            {
                var result = await services.Scanner.ScanAsync(ScanProfile.Quick, ct: CancellationToken.None);
                var safe = result.Issues.Where(i => i.Severity == Severity.Safe && i.CanFix).ToList();

                var rp = await RestorePointService.TryCreateAsync("Registry Optimizer - Scheduled scan");
                var rpText = rp.Created ? "restore point OK" : $"restore point failed: {rp.Message}";

                BackupSession? backup = null;
                if (safe.Count > 0)
                {
                    backup = await services.Backup.BackupForFixAsync(safe);
                    var summary = IssueFixer.Apply(safe);
                    settings.LastScanUtc = DateTime.UtcNow;
                    settings.LastScanIssueCount = result.Issues.Count;
                    settings.LastScanProfile = ScanProfile.Quick;
                    settings.Save();
                    File.AppendAllText(logFile,
                        $"{DateTime.UtcNow:u}  Scheduled scan: {result.Issues.Count} issue(s), " +
                        $"{safe.Count} safe auto-fixed ({summary.Fixed} fixed/{summary.Failed} failed), {rpText}, " +
                        $"backup {(backup is null ? "none" : backup.Id)}.{Environment.NewLine}");
                }
                else
                {
                    settings.LastScanUtc = DateTime.UtcNow;
                    settings.LastScanIssueCount = result.Issues.Count;
                    settings.Save();
                    File.AppendAllText(logFile,
                        $"{DateTime.UtcNow:u}  Scheduled scan: {result.Issues.Count} issue(s), nothing safe to fix, {rpText}.{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                exitCode = 1;
                try
                {
                    File.AppendAllText(logFile, $"{DateTime.UtcNow:u}  Scheduled scan FAILED: {ex}{Environment.NewLine}");
                }
                catch { /* never surface UI in this mode */ }
            }
            _ = Dispatcher.BeginInvoke(() => Shutdown(exitCode));
        });
    }
}
