using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObservableCollections;
using Percolator.Application.Invitations;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.Shell;
using R3;

namespace Percolator.Simulator.Features.Topology;

internal sealed partial class PersonaConnectPeerFlyoutViewModel : ViewModelBase
{
    private readonly ISimulatedPersonaHost _host;
    private readonly ISimulatedWorldStateService _worldState;
    private readonly IPeerInvitationCodec _codec;
    private readonly IPeerConnectionService _peerConnectionService;
    private readonly IStatusToastService _toastService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInWorldMode))]
    [NotifyPropertyChangedFor(nameof(IsExternalMode))]
    private PeerConnectionTargetKind _targetKind = PeerConnectionTargetKind.InWorldSimulatedNode;

    [ObservableProperty]
    private IReadOnlyList<SimulatedNodeSummary> _availableInWorldPersonas = Array.Empty<SimulatedNodeSummary>();

    [ObservableProperty]
    private SimulatedNodeSummary? _selectedInWorldTarget;

    [ObservableProperty]
    private string _externalInvitationInput = string.Empty;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsInWorldMode => TargetKind == PeerConnectionTargetKind.InWorldSimulatedNode;
    public bool IsExternalMode => TargetKind == PeerConnectionTargetKind.ExternalInvitationCode;

    public IReadOnlyList<PeerConnectionTargetKind> AvailableTargetKinds { get; } = Enum.GetValues<PeerConnectionTargetKind>();

    public PersonaConnectPeerFlyoutViewModel(
        ISimulatedPersonaHost host,
        ISimulatedWorldStateService worldState,
        IPeerInvitationCodec codec,
        IPeerConnectionService peerConnectionService,
        IStatusToastService toastService)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _peerConnectionService = peerConnectionService ?? throw new ArgumentNullException(nameof(peerConnectionService));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));

        RefreshAvailableInWorldPersonas();

        _worldState.Personas.ObserveChanged()
            .Subscribe(_ =>
            {
                UiTaskRunner.PostToUi(RefreshAvailableInWorldPersonas);
            })
            .AddTo(ref Disposables);
    }

    public void RefreshAvailableInWorldPersonas()
    {
        AvailableInWorldPersonas = _worldState.Personas
            .Where(p => p.Id != _host.Id)
            .ToList();

        if (SelectedInWorldTarget == null || !AvailableInWorldPersonas.Any(p => p.Id == SelectedInWorldTarget.Id))
        {
            SelectedInWorldTarget = AvailableInWorldPersonas.FirstOrDefault();
        }
    }

    [RelayCommand]
    public async Task ConnectAsync()
    {
        ErrorMessage = null;
        IsConnecting = true;
        try
        {
            if (TargetKind == PeerConnectionTargetKind.InWorldSimulatedNode)
            {
                if (SelectedInWorldTarget == null)
                {
                    ErrorMessage = "Please select an in-world persona to connect to.";
                    return;
                }

                var targetHost = _worldState.GetPersonaHost(SelectedInWorldTarget.Id);
                if (targetHost == null)
                {
                    ErrorMessage = "Target persona is not currently online or hosted.";
                    return;
                }

                var result = await _peerConnectionService.ConnectToDirectPeerAsync(
                    targetHost.PublicIdentityId,
                    targetHost.MailboxRoutingToken,
                    targetHost.MailboxDeliveryToken,
                    targetHost.BoundEndpoint,
                    null);

                if (result.IsFailure)
                {
                    ErrorMessage = result.Error.Description;
                    return;
                }

                _toastService.ShowToast(
                    "Connected",
                    $"Direct connection initiated to {targetHost.DisplayName}.",
                    ToastSeverity.Informational);
            }
            else
            {
                var input = ExternalInvitationInput?.Trim() ?? string.Empty;
                if (input.StartsWith("percolator://", StringComparison.OrdinalIgnoreCase))
                {
                    input = input["percolator://".Length..].Trim();
                }

                if (string.IsNullOrWhiteSpace(input))
                {
                    ErrorMessage = "Please enter an invitation URL or code.";
                    return;
                }

                var decodeResult = _codec.Decode(input);
                if (decodeResult.IsFailure)
                {
                    ErrorMessage = $"Invalid invitation: {decodeResult.Error.Description}";
                    return;
                }

                var payload = decodeResult.Value!;
                var endpoint = BuildUriFromPayload(payload);

                var result = await _peerConnectionService.ConnectToDirectPeerAsync(
                    payload.IdentityId,
                    payload.RoutingToken,
                    payload.DeliveryToken,
                    endpoint,
                    targetDeviceId: null);

                if (result.IsFailure)
                {
                    ErrorMessage = result.Error.Description;
                    return;
                }

                _toastService.ShowToast(
                    "Connected",
                    $"Direct connection initiated to {payload.IdentityId.Value.ToString()[..8]}.",
                    ToastSeverity.Informational);
                ExternalInvitationInput = string.Empty;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to connect: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private static Uri BuildUriFromPayload(PeerInvitationPayload payload)
    {
        var rawHost = payload.EndpointHosts.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h)) ?? "127.0.0.1";
        var port = payload.PortRange?.MinPort ?? 5100;

        if (rawHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            rawHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = new Uri(rawHost);
            return new UriBuilder(parsed) { Scheme = "https", Port = port }.Uri;
        }

        var formattedHost = rawHost.Contains(':') && !rawHost.StartsWith('[') ? $"[{rawHost}]" : rawHost;
        return new Uri($"https://{formattedHost}:{port}");
    }
}
