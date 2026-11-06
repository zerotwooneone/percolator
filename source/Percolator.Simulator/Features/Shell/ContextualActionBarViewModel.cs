using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using R3;

namespace Percolator.Simulator.Features.Shell;

internal sealed partial class ContextualActionBarViewModel : ViewModelBase
{
    private readonly ISelectionStateService _selectionState;
    private readonly IStatusToastService _toastService;

    [ObservableProperty]
    private string _initiatorText = "(None selected)";

    [ObservableProperty]
    private string _targetText = "(None selected)";

    [ObservableProperty]
    private bool _hasPairSelected;

    public ContextualActionBarViewModel(
        ISelectionStateService selectionState,
        IStatusToastService toastService)
    {
        _selectionState = selectionState;
        _toastService = toastService;

        Observable.CombineLatest(
            _selectionState.PrimarySelectedNode,
            _selectionState.SecondarySelectedNode,
            (primary, secondary) => (Primary: primary, Secondary: secondary))
            .Subscribe(pair =>
            {
                InitiatorText = pair.Primary?.DisplayName ?? "(None selected)";
                TargetText = pair.Secondary?.DisplayName ?? "(None selected)";
                HasPairSelected = pair.Primary is not null && pair.Secondary is not null;
            })
            .AddTo(ref Disposables);
    }

    [RelayCommand]
    private void SwapRoles()
    {
        _selectionState.SwapRoles();
    }

    [RelayCommand]
    private void DirectHandshake() => _toastService.ShowPlaceholder("Direct Handshake", "Phase 3B");

    [RelayCommand]
    private void RelayedHandshake() => _toastService.ShowPlaceholder("Relayed Handshake", "Phase 5");

    [RelayCommand]
    private void SendFilePreset() => _toastService.ShowPlaceholder("Send File Preset", "Phase 9");

    [RelayCommand]
    private void BulkRegisterMailboxes() => _toastService.ShowPlaceholder("Bulk Register Mailboxes", "Phase 5");

    [RelayCommand]
    private void BulkPublishPreKeys() => _toastService.ShowPlaceholder("Bulk Publish PreKeys", "Phase 5");

    [RelayCommand]
    private void CreateGroupOnRelay() => _toastService.ShowPlaceholder("Create Group on Relay", "Phase 7");
}
