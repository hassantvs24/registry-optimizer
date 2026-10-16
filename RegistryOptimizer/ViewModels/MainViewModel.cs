using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RegistryOptimizer.ViewModels;

public partial class MainViewModel : ViewModelBase, INavigator
{
    [ObservableProperty]
    private ViewModelBase _currentViewModel;

    [ObservableProperty]
    private int _selectedNavIndex;

    partial void OnSelectedNavIndexChanged(int value) => CurrentViewModel = value switch
    {
        1 => Scan,
        2 => Defrag,
        3 => Rescue,
        4 => Settings,
        _ => Dashboard,
    };

    public DashboardViewModel Dashboard { get; }
    public ScanViewModel Scan { get; }
    public DefragViewModel Defrag { get; }
    public RescueCenterViewModel Rescue { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(AppServices services)
    {
        Dashboard = new DashboardViewModel(services, this);
        Scan = new ScanViewModel(services);
        Defrag = new DefragViewModel(services);
        Rescue = new RescueCenterViewModel(services);
        Settings = new SettingsViewModel(services);
        _currentViewModel = Dashboard;
    }

    public void NavigateTo(ViewModelBase viewModel)
    {
        SelectedNavIndex = viewModel switch
        {
            ScanViewModel => 1,
            DefragViewModel => 2,
            RescueCenterViewModel => 3,
            SettingsViewModel => 4,
            _ => 0,
        };
        CurrentViewModel = viewModel;
    }

    [RelayCommand]
    private void ShowDashboard() => NavigateTo(Dashboard);

    [RelayCommand]
    private void ShowScan() => NavigateTo(Scan);

    [RelayCommand]
    private void ShowDefrag() => NavigateTo(Defrag);

    [RelayCommand]
    private void ShowRescue() => NavigateTo(Rescue);

    [RelayCommand]
    private void ShowSettings() => NavigateTo(Settings);
}
