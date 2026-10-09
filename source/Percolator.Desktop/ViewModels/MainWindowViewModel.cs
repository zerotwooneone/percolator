using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Percolator.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _statusText = "Cross-Platform P2P Communication Platform";

    [ObservableProperty]
    private object? _selectedNavigationItem;

    public MainWindowViewModel()
    {
    }

    [RelayCommand]
    private void RefreshStatus()
    {
        StatusText = $"Active - {System.DateTime.UtcNow:HH:mm:ss} UTC";
    }
}
