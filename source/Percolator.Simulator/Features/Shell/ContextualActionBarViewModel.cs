using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.DirectChat;
using R3;

namespace Percolator.Simulator.Features.Shell;

internal sealed partial class ContextualActionBarViewModel : ViewModelBase
{
    private readonly ISelectionStateService _selectionState;
    private readonly IStatusToastService _toastService;
    private readonly DirectHandshakeOrchestrator _handshakeOrchestrator;
    private SimulatedNodeSummary? _currentPrimary;
    private SimulatedNodeSummary? _currentSecondary;

    [ObservableProperty]
    private string _initiatorText = "(None selected)";

    [ObservableProperty]
    private string _targetText = "(None selected)";

    [ObservableProperty]
    private bool _hasPairSelected;

    public ContextualActionBarViewModel(
        ISelectionStateService selectionState,
        IStatusToastService toastService,
        DirectHandshakeOrchestrator handshakeOrchestrator)
    {
        _selectionState = selectionState;
        _toastService = toastService;
        _handshakeOrchestrator = handshakeOrchestrator;

        Observable.CombineLatest(
            _selectionState.PrimarySelectedNode,
            _selectionState.SecondarySelectedNode,
            (primary, secondary) => (Primary: primary, Secondary: secondary))
            .Subscribe(pair =>
            {
                _currentPrimary = pair.Primary;
                _currentSecondary = pair.Secondary;
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
    private async Task DirectHandshakeAsync()
    {
        if (_currentPrimary is null || _currentSecondary is null)
        {
            _toastService.ShowToast(
                "Selection Required",
                "Select an Initiator (1°) and Target (2°) persona in the Roster first.",
                ToastSeverity.Warning);
            return;
        }

        await _handshakeOrchestrator.InitiateDirectHandshakeAsync(_currentPrimary.Id, _currentSecondary.Id);
    }

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
