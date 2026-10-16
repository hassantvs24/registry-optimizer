using System.Windows;
using RegistryOptimizer.ViewModels;

namespace RegistryOptimizer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(AppServices.Instance);
    }
}
