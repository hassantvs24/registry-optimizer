using CommunityToolkit.Mvvm.ComponentModel;

namespace RegistryOptimizer.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
}

/// <summary>Navigation contract implemented by MainViewModel; VMs use it to switch pages.</summary>
public interface INavigator
{
    void NavigateTo(ViewModelBase viewModel);
}
