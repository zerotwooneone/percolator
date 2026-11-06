using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.SwarmSimulation;

internal sealed partial class SwarmPanelViewModel : ViewModelBase
{
    private readonly IStatusToastService _toastService;

    public SwarmPanelViewModel(IStatusToastService toastService)
    {
        _toastService = toastService;
    }

    [RelayCommand]
    private void PauseTransfer() => _toastService.ShowPlaceholder("Pause Transfer", "Phase 9");

    [RelayCommand]
    private void ResumeTransfer() => _toastService.ShowPlaceholder("Resume Transfer", "Phase 9");

    [RelayCommand]
    private void CancelTransfer() => _toastService.ShowPlaceholder("Cancel Transfer", "Phase 9");

    [RelayCommand]
    private void InjectLateDownloader() => _toastService.ShowPlaceholder("Inject Late Downloader", "Phase 9");
}
