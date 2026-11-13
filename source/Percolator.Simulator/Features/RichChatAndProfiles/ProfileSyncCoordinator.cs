using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application2.Ports;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Profiles;
using Percolator.Domain.Common;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.RichChatAndProfiles;

internal sealed class ProfileSyncCoordinator
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly ISimulatorLogSink _logSink;
    private readonly IStatusToastService _toastService;
    private readonly IFlightRecorderStateService _flightRecorder;

    public ProfileSyncCoordinator(
        ISimulatedWorldStateService worldState,
        ISimulatorStateBus stateBus,
        ISimulatorLogSink logSink,
        IStatusToastService toastService,
        IFlightRecorderStateService flightRecorder)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _flightRecorder = flightRecorder ?? throw new ArgumentNullException(nameof(flightRecorder));
    }

    public async Task<bool> RerollProfileAsync(
        SimulatedNodeId nodeId,
        string? newDisplayName = null,
        uint? identiconSeed = null,
        CancellationToken ct = default)
    {
        var host = _worldState.GetPersonaHost(nodeId);
        if (host == null)
        {
            _toastService.ShowToast("Reroll Failed", "Persona host not found in active topology.", ToastSeverity.Error);
            return false;
        }

        try
        {
            var chosenName = !string.IsNullOrWhiteSpace(newDisplayName)
                ? newDisplayName.Trim()
                : WordPairNameGenerator.Generate();

            var seed = identiconSeed ?? (uint)Random.Shared.Next();
            var newIdenticon = IdenticonGenerator.CreatePng(seed);

            host.UpdateProfile(chosenName, newIdenticon);

            var publishResult = await host.PublishProfileAsync(ct);
            if (!publishResult.IsSuccess)
            {
                _toastService.ShowToast("Publish Error", publishResult.Error.Description, ToastSeverity.Error);
                return false;
            }

            // Distribute updated profile capability to established peer contacts
            using (var scope = host.Services.CreateScope())
            {
                var contactRepo = scope.ServiceProvider.GetRequiredService<IPeerContactRepository>();
                var contacts = await contactRepo.GetAllForOwnerAsync(host.PublicIdentityId, ct);
                foreach (var contact in contacts)
                {
                    var peerHost = _worldState.FindPersonaHostByIdentity(contact.RemotePeerId);
                    if (peerHost != null && peerHost.Id != host.Id)
                    {
                        await ShareProfileCapabilityInternalAsync(host, peerHost, ct);
                    }
                }
            }

            _toastService.ShowToast("Profile Updated", $"Rerolled profile to '{chosenName}' (Rev {host.ProfileRevision}).", ToastSeverity.Informational);

            _stateBus.Publish(new SimulatorStateSignal(
                host.Id,
                SignalCategory.ProfileChanged,
                Details: chosenName,
                Payload: host.ProfileRevision));

            _flightRecorder.RecordStep(new RerollProfileStep(host.DisplayName, chosenName));
            return true;
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                host.DisplayName,
                host.Id,
                "ProfileSync",
                $"Exception rerolling profile: {ex.Message}"));
            _toastService.ShowToast("Reroll Failed", ex.Message, ToastSeverity.Error);
            return false;
        }
    }

    public async Task<bool> ShareProfileAsync(
        SimulatedNodeId senderId,
        SimulatedNodeId recipientId,
        CancellationToken ct = default)
    {
        var sender = _worldState.GetPersonaHost(senderId);
        var recipient = _worldState.GetPersonaHost(recipientId);

        if (sender == null || recipient == null)
        {
            return false;
        }

        return await ShareProfileCapabilityInternalAsync(sender, recipient, ct);
    }

    public async Task AutoShareProfileOnConnectAsync(
        ISimulatedPersonaHost host1,
        ISimulatedPersonaHost host2,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host1);
        ArgumentNullException.ThrowIfNull(host2);

        if (host1.AutoShareProfile)
        {
            await ShareProfileCapabilityInternalAsync(host1, host2, ct);
        }
        if (host2.AutoShareProfile)
        {
            await ShareProfileCapabilityInternalAsync(host2, host1, ct);
        }
    }

    private async Task<bool> ShareProfileCapabilityInternalAsync(
        ISimulatedPersonaHost sender,
        ISimulatedPersonaHost recipient,
        CancellationToken ct)
    {
        try
        {
            using var scope = sender.Services.CreateScope();
            var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();
            var cryptoEngine = scope.ServiceProvider.GetRequiredService<ICryptoEngine>();
            var routingRepo = scope.ServiceProvider.GetRequiredService<IPeerMailboxRoutingRepository>();
            var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
            var timeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();

            var channelId = ChannelIdUtility.DeterministicFor(sender.PublicIdentityId, recipient.PublicIdentityId);
            var directChannel = await channelRepo.GetDirectByIdAsync(channelId, ct);
            if (directChannel == null)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Warning,
                    sender.DisplayName,
                    sender.Id,
                    "ProfileSync",
                    $"Cannot share profile capability with '{recipient.DisplayName}': Direct channel not established."));
                return false;
            }

            var route = DeliveryRoute.DirectP2P;
            if (recipient.HomeRelay != null)
            {
                var now = timeProvider.UtcNow;
                var bestRoute = await routingRepo.GetBestRouteAsync(recipient.PublicIdentityId, now, ct);
                if (bestRoute != null)
                {
                    route = DeliveryRoute.RelayedOneToOne;
                }
            }

            string sourceEndpoint;
            BlindedRoutingToken mailboxRoutingToken;
            ProfileSourceType sourceType;

            if (sender.HomeRelay != null)
            {
                sourceEndpoint = $"https://127.0.0.1:{sender.HomeRelay.BoundPort}";
                mailboxRoutingToken = sender.MailboxRoutingToken;
                sourceType = ProfileSourceType.Relay;
            }
            else
            {
                sourceEndpoint = sender.BoundEndpoint.ToString();
                mailboxRoutingToken = default;
                sourceType = ProfileSourceType.DirectP2P;
            }

            var locator = BlindedProfileLocator.Derive(sender.IdentityPublicKey, sender.ProfileKey, cryptoEngine);

            var cmd = new ShareProfileCapabilityCommand(
                ChannelId: channelId,
                AuthorId: sender.PublicIdentityId,
                RecipientIdentityId: recipient.PublicIdentityId,
                RecipientDeviceId: DeviceId.Primary,
                ProfileKey: sender.ProfileKey,
                BlindedLocator: locator,
                MailboxRoutingToken: mailboxRoutingToken,
                Revision: sender.ProfileRevision,
                Route: route,
                AuthorPublicKey: sender.IdentityPublicKey,
                SourceEndpoint: sourceEndpoint,
                SourceType: sourceType);

            var shareResult = await chatService.ShareProfileCapabilityAsync(cmd, ct);
            if (!shareResult.IsSuccess)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Warning,
                    sender.DisplayName,
                    sender.Id,
                    "ProfileSync",
                    $"Failed to share profile capability with '{recipient.DisplayName}': {shareResult.Error.Description}"));
                return false;
            }

            await sender.FlushOutboxAsync(ct);

            if (route == DeliveryRoute.RelayedOneToOne && recipient.HomeRelay != null)
            {
                await recipient.DrainMailboxAsync(ct);
            }

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                sender.DisplayName,
                sender.Id,
                "ProfileSync",
                $"Shared profile capability (Rev {sender.ProfileRevision}) with '{recipient.DisplayName}' over {route}."));

            return true;
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                sender.DisplayName,
                sender.Id,
                "ProfileSync",
                $"Exception sharing profile capability with '{recipient.DisplayName}': {ex.Message}"));
            return false;
        }
    }
}
