using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.DiscoverySimulation;

internal sealed partial class DiscoveryTabViewModel : ViewModelBase
{
    private readonly IStatusToastService _toastService;

    public DiscoveryTabViewModel(IStatusToastService toastService)
    {
        _toastService = toastService;
    }

    [RelayCommand]
    private void PublishBlindedLocator() => _toastService.ShowPlaceholder("Publish Blinded Locator", "Phase 8");

    [RelayCommand]
    private void QueryNearbyPeers() => _toastService.ShowPlaceholder("Query Nearby Peers", "Phase 8");
}
