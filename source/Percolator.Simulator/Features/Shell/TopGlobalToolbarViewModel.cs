using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Shell;

internal sealed partial class TopGlobalToolbarViewModel : ViewModelBase
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly IStatusToastService _toastService;

    public TopGlobalToolbarViewModel(
        ISimulatedWorldStateService worldState,
        IStatusToastService toastService)
    {
        _worldState = worldState;
        _toastService = toastService;
    }

    [RelayCommand]
    private void AddPerson() => _toastService.ShowPlaceholder("Add Person", "Phase 3A");

    [RelayCommand]
    private void BulkAddPeople() => _toastService.ShowPlaceholder("Bulk Add People", "Phase 5");

    [RelayCommand]
    private void AddRelay() => _toastService.ShowPlaceholder("Add Relay", "Phase 5");

    [RelayCommand]
    private void AdvanceOneMinute() => _toastService.ShowPlaceholder("+1 Minute", "Phase 6A");

    [RelayCommand]
    private void AdvanceOneHour() => _toastService.ShowPlaceholder("+1 Hour", "Phase 6A");

    [RelayCommand]
    private void AdvanceTwentyFourHours() => _toastService.ShowPlaceholder("+24 Hours", "Phase 8");

    [RelayCommand]
    private void ResetWorld()
    {
        _worldState.ClearAll();
        _toastService.ShowToast("World Reset", "All simulated nodes and pinned cards have been cleared.", ToastSeverity.Warning);
    }
}
