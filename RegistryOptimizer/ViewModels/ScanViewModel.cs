using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RegistryOptimizer.Core;
using RegistryOptimizer.Core.Backup;
using RegistryOptimizer.Core.Scanning;

namespace RegistryOptimizer.ViewModels;

/// <summary>One row of the results grid: wraps a ScanIssue and its selection state.</summary>
public partial class ScanIssueRow : ObservableObject
{
    public ScanIssue Issue { get; }

    [ObservableProperty]
    private bool _isSelected;

    public ScanCategory Category => Issue.Category;
    public Severity Severity => Issue.Severity;
    public string SeverityText => Issue.Severity.ToString();
    public string CategoryText => CategoryLabel(Issue.Category);
    public string FullPath => Issue.FullPath;
    public string ValueText => Issue.ValueName ?? "(key)";
    public string Reason => Issue.Reason;
    public bool CanFix => Issue.CanFix;

    public ScanIssueRow(ScanIssue issue, bool isSelected)
    {
        Issue = issue;
        _isSelected = isSelected;
    }

    public static string CategoryLabel(ScanCategory category) => category switch
    {
        ScanCategory.MissingFileReferences => "Missing files",
        ScanCategory.SharedDlls => "Shared DLLs",
        ScanCategory.OrphanedUninstallEntries => "Uninstall entries",
        ScanCategory.BrokenFileAssociations => "File associations",
        ScanCategory.StartupEntries => "Startup entries",
        ScanCategory.InvalidComObjects => "COM objects",
        ScanCategory.MruHistory => "MRU history",
        ScanCategory.OrphanedKeys => "Orphaned keys",
        _ => category.ToString(),
    };
}

public partial class ScanViewModel : ViewModelBase
{
    private readonly AppServices _services;

    [ObservableProperty] private ScanProfile _profile = ScanProfile.Quick;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _summaryText = "No scan results yet.";
    [ObservableProperty] private string _selectedSummaryText = string.Empty;
    [ObservableProperty] private string _categoryFilter = "All";
    [ObservableProperty] private bool _advancedWarningVisible;
    [ObservableProperty] private bool _showEmptyHint = true;
    [ObservableProperty] private string _emptyHintText =
        "Click Start Scan to check your registry for issues. Nothing is changed until you press Fix Selected.";

    public ObservableCollection<ScanIssueRow> Results { get; } = new();
    public ObservableCollection<string> CategoryFilters { get; } = new() { "All" };

    private List<ScanIssueRow> _allRows = new();
    private CancellationTokenSource? _scanCts;

    public ScanViewModel(AppServices services)
    {
        _services = services;
        UpdateSelectedSummary();
    }

    partial void OnCategoryFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Results.Clear();
        foreach (var row in CategoryFilter == "All"
                     ? _allRows
                     : _allRows.Where(r => r.CategoryText == CategoryFilter))
        {
            Results.Add(row);
        }
        UpdateSelectedSummary();
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        ProgressPercent = 0;
        StatusText = "Starting scan ...";
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        _allRows.Clear();
        Results.Clear();
        CategoryFilters.Clear();
        CategoryFilters.Add("All");
        CategoryFilter = "All";
        SummaryText = "Scanning ...";
        ShowEmptyHint = false;

        // Progress: the service runs all enabled scanners concurrently; we approximate
        // percent from the status messages and bump slightly per reported issue.
        var scannersTotal = _services.Scanner.Scanners
            .Count(s => s.Supports(Profile) && !_services.Settings.ExcludedSet.Contains(s.Category));
        var finished = 0;
        var issueCount = 0;

        var statusProgress = new Progress<string>(msg =>
        {
            StatusText = msg;
            if (msg.StartsWith("Finished ", StringComparison.Ordinal) && finished < scannersTotal)
            {
                finished++;
                ProgressPercent = scannersTotal == 0 ? 100 : finished * 100.0 / scannersTotal;
            }
        });
        var issueProgress = new Progress<ScanIssue>(_ =>
        {
            issueCount++;
            if (scannersTotal > 0)
                ProgressPercent = Math.Min(99, (finished + Math.Min(1.0, issueCount / 50.0)) * 100.0 / scannersTotal);
        });

        try
        {
            var result = await _services.Scanner.ScanAsync(Profile, issueProgress, statusProgress, ct);

            foreach (var old in _allRows) old.PropertyChanged -= Row_PropertyChanged;
            _allRows = result.Issues
                .Select(i => new ScanIssueRow(i, i.Severity == Severity.Safe))
                .ToList();
            foreach (var row in _allRows) row.PropertyChanged += Row_PropertyChanged;
            ApplyFilter();

            foreach (var group in result.Issues.GroupBy(i => ScanIssueRow.CategoryLabel(i.Category)))
            {
                CategoryFilters.Add(group.Key);
            }

            var byCategory = string.Join(", ",
                result.Issues.GroupBy(i => ScanIssueRow.CategoryLabel(i.Category))
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key}: {g.Count()}"));
            ShowEmptyHint = result.Issues.Count == 0;
            EmptyHintText = ShowEmptyHint
                ? $"No issues found — your registry looks clean. ({result.Profile} scan, {result.Duration:mm\\:ss})"
                : EmptyHintText;
            SummaryText = result.Cancelled
                ? $"Scan cancelled — {result.Issues.Count} issue(s) found so far."
                : $"Scan complete: {result.Issues.Count} issue(s) " +
                  $"({result.SafeCount} safe, {result.AdvancedCount} advanced) in {result.Duration:mm\\:ss}." +
                  (byCategory.Length > 0 ? Environment.NewLine + byCategory : string.Empty);
            ProgressPercent = 100;
            StatusText = result.Cancelled ? "Scan cancelled." : "Scan complete.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
            SummaryText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _scanCts = null;
            UpdateSelectedSummary();
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private void SelectSafeOnly()
    {
        foreach (var row in Results) row.IsSelected = row.Severity == Severity.Safe;
        UpdateSelectedSummary();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Results) row.IsSelected = true;
        UpdateSelectedSummary();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var row in Results) row.IsSelected = false;
        UpdateSelectedSummary();
    }

    private void UpdateSelectedSummary()
    {
        var selected = Results.Where(r => r.IsSelected).ToList();
        var fixable = selected.Count(r => r.CanFix);
        var advanced = selected.Count(r => r.Severity == Severity.Advanced);
        SelectedSummaryText = selected.Count == 0
            ? "Nothing selected."
            : $"{selected.Count} selected — {fixable} will be fixed" +
              (advanced > 0 ? $" ({advanced} Advanced)" : string.Empty);
        AdvancedWarningVisible = advanced > 0;
    }

    private void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanIssueRow.IsSelected)) UpdateSelectedSummary();
    }

    [RelayCommand]
    private async Task FixSelectedAsync()
    {
        var selected = Results.Where(r => r.IsSelected && r.CanFix).Select(r => r.Issue).ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "Select at least one fixable issue first.", "Registry Cleaner",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Fix {selected.Count} selected issue(s)?\n\n" +
            "A backup of every affected registry key will be saved to:\n" +
            $"{AppPaths.BackupsRoot}\n\n" +
            "A system restore point will also be created when possible.",
            "Confirm fix", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsScanning = true; // reuse the busy flag to disable buttons
        StatusText = "Creating restore point ...";
        try
        {
            var rp = await RestorePointService.TryCreateAsync("Registry Optimizer - Fix issues");
            System.Windows.MessageBox.Show(
                rp.Created
                    ? $"System restore point created.\n\n{rp.Message}"
                    : $"Could not create a system restore point.\n\n{rp.Message}\n\nThe registry backup will still be created.",
                "Restore point", System.Windows.MessageBoxButton.OK,
                rp.Created ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);

            StatusText = "Backing up registry keys ...";
            var backup = await _services.Backup.BackupForFixAsync(selected);

            StatusText = "Fixing issues ...";
            var progress = new Progress<string>(msg => StatusText = msg);
            var summary = await Task.Run(() => IssueFixer.Apply(selected, progress));

            var failedDetails = summary.Failures.Count > 0
                ? "\n\nFailed items:\n" + string.Join(Environment.NewLine, summary.Failures.Take(10))
                : string.Empty;
            System.Windows.MessageBox.Show(
                $"Fixed {summary.Fixed} issue(s), {summary.Failed} failed.\n" +
                $"Backup saved: {backup.Folder}{failedDetails}",
                "Fix complete", System.Windows.MessageBoxButton.OK,
                summary.Failed == 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);

            var fixedIdentities = selected.Select(i => i.Identity).ToHashSet(StringComparer.Ordinal);
            foreach (var row in _allRows.Where(r => fixedIdentities.Contains(r.Issue.Identity)).ToList())
            {
                row.PropertyChanged -= Row_PropertyChanged;
                _allRows.Remove(row);
            }
            ApplyFilter();

            var settings = _services.Settings;
            settings.LastScanUtc = DateTime.UtcNow;
            settings.LastScanIssueCount = _allRows.Count;
            settings.LastScanProfile = Profile;
            _services.SaveSettings();

            StatusText = "Done.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Fixing failed: {ex.Message}", "Registry Cleaner",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            StatusText = $"Fixing failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            UpdateSelectedSummary();
        }
    }

    [RelayCommand]
    private void Ignore(ScanIssueRow? row)
    {
        if (row is null) return;
        _services.Settings.AddIgnore(row.Issue.Identity);
        _services.SaveSettings();
        _allRows.Remove(row);
        ApplyFilter();
        StatusText = $"Ignored: {row.FullPath}";
    }
}
