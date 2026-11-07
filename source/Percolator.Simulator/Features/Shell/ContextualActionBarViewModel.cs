using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.GroupMessaging;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Topology;
using R3;

namespace Percolator.Simulator.Features.Shell;

internal sealed partial class ContextualActionBarViewModel : ViewModelBase
{
    private readonly ISelectionStateService _selectionState;
    private readonly IStatusToastService _toastService;
    private readonly DirectHandshakeOrchestrator _handshakeOrchestrator;
    private readonly RelayProvisioningCoordinator _relayCoordinator;
    private readonly GroupChatCoordinator _groupCoordinator;
    private readonly ISimulatedWorldStateService _worldState;
    private readonly IFlightRecorderStateService? _flightRecorder;
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
        DirectHandshakeOrchestrator handshakeOrchestrator,
        RelayProvisioningCoordinator relayCoordinator,
        GroupChatCoordinator groupCoordinator,
        ISimulatedWorldStateService worldState,
        IFlightRecorderStateService? flightRecorder = null)
    {
        _selectionState = selectionState ?? throw new ArgumentNullException(nameof(selectionState));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _handshakeOrchestrator = handshakeOrchestrator ?? throw new ArgumentNullException(nameof(handshakeOrchestrator));
        _relayCoordinator = relayCoordinator ?? throw new ArgumentNullException(nameof(relayCoordinator));
        _groupCoordinator = groupCoordinator ?? throw new ArgumentNullException(nameof(groupCoordinator));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _flightRecorder = flightRecorder;

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

        var primaryId = _currentPrimary.Id;
        var secondaryId = _currentSecondary.Id;

        await UiTaskRunner.RunAsync(
            () => _handshakeOrchestrator.InitiateDirectHandshakeAsync(primaryId, secondaryId),
            category: "Handshake",
            toastService: _toastService);
    }

    [RelayCommand]
    private async Task RelayedHandshakeAsync()
    {
        if (_currentPrimary is null || _currentSecondary is null)
        {
            _toastService.ShowToast(
                "Selection Required",
                "Select an Initiator (1°) and Target (2°) persona in the Roster first.",
                ToastSeverity.Warning);
            return;
        }

        var relay = _worldState.GetAllRelayHosts().FirstOrDefault();
        if (relay == null)
        {
            _toastService.ShowToast(
                "Relay Required",
                "Spawn a Relay node first using '+ Add Relay' in the top toolbar.",
                ToastSeverity.Warning);
            return;
        }

        var primaryId = _currentPrimary.Id;
        var secondaryId = _currentSecondary.Id;

        var success = await UiTaskRunner.RunAsync(
            () => _relayCoordinator.InitiateRelayedHandshakeAsync(primaryId, secondaryId, relay.Id),
            category: "RelayedHandshake",
            toastService: _toastService);

        if (success)
        {
            _flightRecorder?.RecordStep(new InitiateRelayedHandshakeStep(_currentPrimary.DisplayName, _currentSecondary.DisplayName, relay.DisplayName));
        }
    }

    [RelayCommand]
    private async Task BulkRegisterMailboxesAsync()
    {
        var relay = _worldState.GetAllRelayHosts().FirstOrDefault();
        if (relay == null)
        {
            _toastService.ShowToast(
                "Relay Required",
                "Spawn a Relay node first using '+ Add Relay' in the top toolbar.",
                ToastSeverity.Warning);
            return;
        }

        var personas = _worldState.Personas
            .Select(p => _worldState.GetPersonaHost(p.Id))
            .Where(h => h != null)
            .Cast<ISimulatedPersonaHost>()
            .ToList();

        if (personas.Count == 0)
        {
            _toastService.ShowToast("No Personas", "Add personas to the world first.", ToastSeverity.Warning);
            return;
        }

        var count = await UiTaskRunner.RunAsync(
            () => _relayCoordinator.BulkRegisterMailboxesAsync(personas, relay),
            category: "RelayProvisioning",
            toastService: _toastService);

        var names = personas.Select(p => p.DisplayName).ToList();
        _flightRecorder?.RecordStep(new RegisterMailboxesStep(relay.DisplayName, names));

        _toastService.ShowToast("Mailboxes Registered", $"Registered {count} mailboxes on relay '{relay.DisplayName}'.", ToastSeverity.Success);
    }

    [RelayCommand]
    private async Task BulkPublishPreKeysAsync()
    {
        var relay = _worldState.GetAllRelayHosts().FirstOrDefault();
        if (relay == null)
        {
            _toastService.ShowToast(
                "Relay Required",
                "Spawn a Relay node first using '+ Add Relay' in the top toolbar.",
                ToastSeverity.Warning);
            return;
        }

        var personas = _worldState.Personas
            .Select(p => _worldState.GetPersonaHost(p.Id))
            .Where(h => h != null)
            .Cast<ISimulatedPersonaHost>()
            .ToList();

        if (personas.Count == 0)
        {
            _toastService.ShowToast("No Personas", "Add personas to the world first.", ToastSeverity.Warning);
            return;
        }

        var count = await UiTaskRunner.RunAsync(
            () => _relayCoordinator.BulkPublishPreKeysAsync(personas, relay, 25),
            category: "RelayProvisioning",
            toastService: _toastService);

        var names = personas.Select(p => p.DisplayName).ToList();
        _flightRecorder?.RecordStep(new PublishPreKeysStep(relay.DisplayName, names, 25));

        _toastService.ShowToast("PreKeys Published", $"Published 25 OPKs for {count} personas on relay '{relay.DisplayName}'.", ToastSeverity.Success);
    }

    [RelayCommand]
    private async Task CreateGroupOnRelayAsync()
    {
        var relay = _worldState.GetAllRelayHosts().FirstOrDefault();
        if (relay == null)
        {
            _toastService.ShowToast(
                "Relay Required",
                "Spawn a Relay node first using '+ Add Relay' in the top toolbar.",
                ToastSeverity.Warning);
            return;
        }

        var personas = _worldState.GetAllPersonaHosts();
        if (personas.Count < 2)
        {
            _toastService.ShowToast(
                "Personas Required",
                "At least 2 personas are needed to create a group.",
                ToastSeverity.Warning);
            return;
        }

        var admin = _currentPrimary != null ? _worldState.GetPersonaHost(_currentPrimary.Id) : personas.First();
        if (admin == null) admin = personas.First();

        var members = personas.Where(p => p.Id != admin.Id).ToList();
        var groupName = "Dev Group";

        var channelId = await UiTaskRunner.RunAsync(
            () => _groupCoordinator.CreateGroupOnRelayAsync(admin, relay, groupName, members),
            category: "GroupProvisioning",
            toastService: _toastService);

        if (channelId != default)
        {
            var memberNames = members.Select(m => m.DisplayName).ToList();
            _flightRecorder?.RecordStep(new CreateGroupStep(admin.DisplayName, relay.DisplayName, groupName, memberNames));
        }
    }

    [RelayCommand]
    private void SendFilePreset() => _toastService.ShowPlaceholder("Send File Preset", "Phase 9");
}
