using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RegistryOptimizer.Core;
using RegistryOptimizer.Core.Defrag;

namespace RegistryOptimizer.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly INavigator _navigator;

    [ObservableProperty] private string _lastScanText = "Never";
    [ObservableProperty] private string _lastScanIssuesText = "-";
    [ObservableProperty] private string _fragmentationText = "Not analyzed";
    [ObservableProperty] private string _lastDefragText = "Never";

    public DashboardViewModel(AppServices services, INavigator navigator)
    {
        _services = services;
        _navigator = navigator;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var s = _services.Settings;
        LastScanText = s.LastScanUtc is { } scanUtc
            ? scanUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "Never";
        LastScanIssuesText = s.LastScanUtc is null
            ? "-"
            : $"{s.LastScanIssueCount} issue(s) ({s.LastScanProfile})";

        var status = DefragStatus.Load();
        FragmentationText = s.LastFragmentationPercent is { } frag
            ? $"{frag:F1}% fragmented"
            : "Not analyzed";
        LastDefragText = status.BootRunUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                         ?? (status.Scheduled ? "Scheduled (runs at next boot)" : "Never");
    }

    [RelayCommand]
    private void OpenScan() => _navigator.NavigateTo(((MainViewModel)_navigator).Scan);

    [RelayCommand]
    private void OpenOptimizer() => _navigator.NavigateTo(((MainViewModel)_navigator).Defrag);
}
