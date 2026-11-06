using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed partial class RelayCardViewModel : ViewModelBase
{
    private readonly IStatusToastService _toastService;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _boundEndpoint;

    [ObservableProperty]
    private int _heldEnvelopeCount = 0;

    [ObservableProperty]
    private int _hostedOpkCount = 25;

    public RelayCardViewModel(SimulatedNodeSummary summary, IStatusToastService toastService)
    {
        _displayName = summary.DisplayName;
        _boundEndpoint = summary.Subtitle;
        _toastService = toastService;
    }

    [RelayCommand]
    private void DuplicateEnvelope() => _toastService.ShowPlaceholder("Duplicate Envelope", "Phase 5");

    [RelayCommand]
    private void FlipBit() => _toastService.ShowPlaceholder("Flip 1 Bit", "Phase 5");

    [RelayCommand]
    private void ShuffleQueue() => _toastService.ShowPlaceholder("Shuffle Queue", "Phase 5");
}
