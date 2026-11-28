namespace Percolator.Simulator.Features.Pairing;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Connections;
using Percolator.Application.Invitations;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Security.Ports;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

internal interface ISimulatorSettingsService
{
    bool AutoAcceptHandshakes { get; set; }
}

internal sealed class SimulatorSettingsService : ISimulatorSettingsService
{
    public bool AutoAcceptHandshakes { get; set; } = true;
}

internal sealed class QuickPairCoordinator
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorSettingsService _settings;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;

    public QuickPairCoordinator(
        ISimulatedWorldStateService worldState,
        ISimulatorSettingsService settings,
        IStatusToastService toastService,
        ISimulatorLogSink logSink)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
    }

    public async Task<bool> ExecutePairAsync(
        SimulatedNodeId inviterId,
        SimulatedNodeId inviteeId,
        CancellationToken ct = default)
    {
        var inviter = _worldState.GetPersonaHost(inviterId);
        var invitee = _worldState.GetPersonaHost(inviteeId);
        if (inviter == null || invitee == null || inviter.Id == invitee.Id)
        {
            _toastService.ShowToast("Pairing Failed", "Must select two distinct active personas.", ToastSeverity.Warning);
            return false;
        }

        var inviterRelay = inviter.GetPrimaryLinkedRelay();
        if (inviterRelay == null)
        {
            _toastService.ShowToast("Pairing Failed", $"{inviter.DisplayName} has no linked relay.", ToastSeverity.Error);
            return false;
        }

        // 1. Inviter exports 4-field PeerInvitation (pinned IdentityKey + Exchange routing coordinates, zero DeliveryToken)
        using var inviterScope = inviter.Services.CreateScope();
        var invService = inviterScope.ServiceProvider.GetRequiredService<IPeerInvitationService>();
        var inviteResult = await invService.CreatePeerInvitationAsync(
            inviter.PublicIdentityId, inviterRelay.Endpoint, ct);
        if (inviteResult.IsFailure)
        {
            _toastService.ShowToast("Pairing Failed", inviteResult.Error.Description, ToastSeverity.Error);
            return false;
        }
        var codec = new PeerInvitationCodec();
        var inviteUri = codec.Encode(PeerInvitationPayload.FromDomain(inviteResult.Value));

        // 2. Invitee connects via public driving port (authenticated 1-RTT FetchPreKeyBundle + pinned IdentityKey verify + authenticated 4-DH SendInitialHandshake)
        using var inviteeScope = invitee.Services.CreateScope();
        var workflow = inviteeScope.ServiceProvider.GetRequiredService<IOutboundConnectionWorkflow>();
        var decodedResult = codec.Decode(inviteUri);
        if (decodedResult.IsFailure)
        {
            _toastService.ShowToast("Pairing Failed", decodedResult.Error.Description, ToastSeverity.Error);
            return false;
        }
        var decoded = decodedResult.Value;
        var connectResult = await workflow.ConnectAsync(invitee.PublicIdentityId, decoded, TimeSpan.FromSeconds(10), ct);
        if (!connectResult.IsSuccess)
        {
            _toastService.ShowToast("Pairing Failed", connectResult.Error.Description, ToastSeverity.Error);
            return false;
        }

        _logSink.Log(new SimulatorLogEntry(DateTimeOffset.UtcNow, LogLevel.Information, invitee.DisplayName, invitee.Id,
            "Handshake", $"Fetched PreKeyBundle, verified pinned IdentityKey, and enqueued 4-DH PreKeyMessage to {inviterRelay.Endpoint.Value} for {inviter.DisplayName}."));

        // 3. Inviter's RelayMailboxDrainHost receives envelope from relay; if auto-accept enabled, approve via inbound workflow:
        if (_settings.AutoAcceptHandshakes)
        {
            var inboundWorkflow = inviterScope.ServiceProvider.GetRequiredService<IInboundConnectionWorkflow>();
            await inboundWorkflow.AcceptRequestAsync(inviter.PublicIdentityId, invitee.PublicIdentityId, ct);
        }

        _toastService.ShowToast("Pairing Complete", $"⚡ Handshake initiated between {inviter.DisplayName} and {invitee.DisplayName}.", ToastSeverity.Success);
        return true;
    }
}
