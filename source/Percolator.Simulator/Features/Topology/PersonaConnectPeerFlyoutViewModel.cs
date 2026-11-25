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

            PeerInvitationPayload payload;
            var codec = new PeerInvitationCodec();
            var decodeResult = codec.Decode(rawInput);
            if (decodeResult.IsFailure)
            {
                ErrorMessage = $"Invalid invitation format: {decodeResult.Error.Description}";
                return;
            }
            payload = decodeResult.Value;

            var result = await _workflow.ConnectAsync(_host.PublicIdentityId, payload, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            if (result.IsFailure)
            {
                ErrorMessage = result.Error.Description;
                return;
            }

            _toastService.ShowToast(
                "Connected",
                $"⚡ Handshake initiated to {payload.IdentityId.Value.ToString()[..8]}.",
                ToastSeverity.Success);
            InvitationInput = string.Empty;
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
