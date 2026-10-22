using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using R3;

namespace Percolator.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly INetworkStealthGate _stealthGate;
    private readonly IWindowManager _windowManager;

    [ObservableProperty]
    private string _statusText = "Network Active - Zero-Leak P2P Mesh";

    [ObservableProperty]
    private int _activeIdentitiesCount;

    [ObservableProperty]
    private object? _selectedNavigationItem;

    public MainWindowViewModel(INetworkStealthGate stealthGate, IWindowManager windowManager)
    {
        _stealthGate = stealthGate ?? throw new ArgumentNullException(nameof(stealthGate));
        _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));

        ActiveIdentitiesCount = _stealthGate.ActiveIdentities.Count;

        _stealthGate.NetworkActiveChanged
            .Subscribe(active =>
            {
                ActiveIdentitiesCount = _stealthGate.ActiveIdentities.Count;
                StatusText = active
                    ? $"Network Active ({ActiveIdentitiesCount} Identities) - {DateTime.UtcNow:HH:mm:ss} UTC"
                    : "Stealth Mode - All Listeners Unbound";
            })
            .AddTo(ref Bag);
    }

    [RelayCommand]
    private void RefreshStatus()
    {
        StatusText = $"Active ({ActiveIdentitiesCount} Identities) - {DateTime.UtcNow:HH:mm:ss} UTC";
    }

    [RelayCommand]
    private async Task LockSessionAsync()
    {
        await _stealthGate.DeactivateAsync();
        _windowManager.SwitchToIdentityGate();
    }
}
