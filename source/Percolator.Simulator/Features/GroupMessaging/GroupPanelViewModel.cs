using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.GroupMessaging;

internal sealed partial class GroupPanelViewModel : ViewModelBase
{
    private readonly IStatusToastService _toastService;

    public GroupPanelViewModel(IStatusToastService toastService)
    {
        _toastService = toastService;
    }

    [RelayCommand]
    private void AddMember() => _toastService.ShowPlaceholder("Add Group Member", "Phase 7");

    [RelayCommand]
    private void FloodMessages() => _toastService.ShowPlaceholder("Flood 25 Messages", "Phase 7");
}
