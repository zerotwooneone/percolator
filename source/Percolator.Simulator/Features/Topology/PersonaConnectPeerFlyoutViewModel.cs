using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Connections;
using Percolator.Application.Invitations;
using Percolator.Application.Profiles;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;
using Percolator.Simulator.Features.Shell;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Simulator.Features.Topology;

internal sealed partial class PersonaConnectPeerFlyoutViewModel : ViewModelBase
{
    private readonly ISimulatedPersonaHost _host;
    private readonly IPeerInvitationCodec _codec;
    private readonly IOutboundConnectionWorkflow _workflow;
    private readonly IStatusToastService _toastService;

    [ObservableProperty]
    private string _invitationInput = string.Empty;

    public string ExternalInvitationInput
    {
        get => InvitationInput;
        set => InvitationInput = value;
    }

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private string? _errorMessage;

    public PersonaConnectPeerFlyoutViewModel(
        ISimulatedPersonaHost host,
        IPeerInvitationCodec codec,
        IOutboundConnectionWorkflow workflow,
        IStatusToastService toastService)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
    }

    public PersonaConnectPeerFlyoutViewModel(
        ISimulatedPersonaHost host,
        ISimulatedWorldStateService worldState,
        IPeerInvitationCodec codec,
        IOutboundConnectionWorkflow workflow,
        IStatusToastService toastService)
        : this(host, codec, workflow, toastService)
    {
    }

    [RelayCommand]
    public async Task ConnectAsync()
    {
        ErrorMessage = null;
        IsConnecting = true;

        try
        {
            var rawInput = InvitationInput?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                ErrorMessage = "Please enter an invitation URL or code.";
                return;
            }

            var input = rawInput;
            if (input.StartsWith("percolator://", StringComparison.OrdinalIgnoreCase))
            {
                input = input["percolator://".Length..].Trim();
            }

            // 1. Try decoding as PeerInvitationPayload (query param or base64 format)
            var decodeResult = _codec.Decode(input);
            if (decodeResult.IsSuccess)
            {
                var payload = decodeResult.Value!;
                var result = await _workflow.ConnectAsync(payload, TimeSpan.FromSeconds(3)).ConfigureAwait(true);
                if (result.IsFailure)
                {
                    ErrorMessage = result.Error.Description;
                    return;
                }

                await _host.FlushOutboxAsync().ConfigureAwait(false);

                _toastService.ShowToast(
                    "Connected",
                    $"Direct connection initiated to {payload.IdentityId.Value.ToString()[..8]}.",
                    ToastSeverity.Informational);
                InvitationInput = string.Empty;
                return;
            }

            // 2. Try decoding as InvitationLink (relayed base64 or direct URL format)
            if (InvitationLink.TryParse(rawInput, out var link, out _) ||
                InvitationLink.TryParse(input, out link, out _))
            {
                if (link?.Kind == InvitationKind.Relayed && link.PeerIdentityId.HasValue && link.RelayEndpoint != null)
                {
                    using var scope = _host.Services.CreateScope();
                    var routingRepo = scope.ServiceProvider.GetRequiredService<IPeerMailboxRoutingRepository>();
                    var timeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
                    var peerConnService = scope.ServiceProvider.GetRequiredService<IPeerConnectionService>();

                    var cap = new PeerMailboxRoutingCapability(
                        link.PeerIdentityId.Value,
                        link.RoutingToken ?? BlindedRoutingToken.New(),
                        link.DeliveryToken ?? DeliveryToken.GenerateRandom(),
                        link.RelayEndpoint,
                        timeProvider.UtcNow);

                    await routingRepo.SaveAsync(cap).ConfigureAwait(false);

                    var connResult = await peerConnService.ConnectToPeerAsync(
                        link.PeerIdentityId.Value,
                        link.PeerDeviceId,
                        link.RelayEndpoint).ConfigureAwait(true);

                    if (connResult.IsFailure)
                    {
                        ErrorMessage = connResult.Error.Description;
                        return;
                    }

                    await _host.FlushOutboxAsync().ConfigureAwait(false);

                    _toastService.ShowToast(
                        "Connected",
                        $"Relayed connection initiated to {link.PeerIdentityId.Value.ToString()[..8]} via {link.RelayEndpoint}.",
                        ToastSeverity.Informational);
                    InvitationInput = string.Empty;
                    return;
                }
            }

            ErrorMessage = $"Invalid invitation: {decodeResult.Error.Description}";
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
}
