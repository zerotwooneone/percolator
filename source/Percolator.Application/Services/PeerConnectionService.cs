namespace Percolator.Application.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Connections;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Handshake;
using Percolator.Application.Invitations;
using Percolator.Application.Ports;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Events;
using Percolator.PluginSdk.Profiles;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

/// <summary>
/// Host implementation of IPeerConnectionService exposed to plugins via PluginSdk.
/// Orchestrates prekey retrieval, pairwise X3DH handshake initiation, and direct channel provisioning.
/// </summary>
public sealed class PeerConnectionService : IPeerConnectionService
{
    private readonly PublicIdentityId _localIdentityId;
    private readonly DeviceId _localDeviceId;
    private readonly IPreKeyBundleProvider _preKeyBundleProvider;
    private readonly IHandshakeService _handshakeService;
    private readonly IChannelRepository _channelRepository;
    private readonly IDateTimeProvider _timeProvider;
    private readonly IPeerMailboxRoutingRepository _routingRepository;
    private readonly IPeerContactRepository _contactRepository;
    private readonly IEventPublisher _eventPublisher;
    private readonly IInitialHandshakePayloadProvider _initialPayloadProvider;
    private readonly IOutboundConnectionWorkflow _outboundWorkflow;

    public PeerConnectionService(
        PublicIdentityId localIdentityId,
        DeviceId localDeviceId,
        IPreKeyBundleProvider preKeyBundleProvider,
        IHandshakeService handshakeService,
        IChannelRepository channelRepository,
        IDateTimeProvider timeProvider,
        IPeerMailboxRoutingRepository routingRepository,
        IPeerContactRepository contactRepository,
        IEventPublisher eventPublisher,
        IInitialHandshakePayloadProvider initialPayloadProvider,
        IOutboundConnectionWorkflow outboundWorkflow)
    {
        _localIdentityId = localIdentityId;
        _localDeviceId = localDeviceId;
        _preKeyBundleProvider = preKeyBundleProvider ?? throw new ArgumentNullException(nameof(preKeyBundleProvider));
        _handshakeService = handshakeService ?? throw new ArgumentNullException(nameof(handshakeService));
        _channelRepository = channelRepository ?? throw new ArgumentNullException(nameof(channelRepository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _routingRepository = routingRepository ?? throw new ArgumentNullException(nameof(routingRepository));
        _contactRepository = contactRepository ?? throw new ArgumentNullException(nameof(contactRepository));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _initialPayloadProvider = initialPayloadProvider ?? throw new ArgumentNullException(nameof(initialPayloadProvider));
        _outboundWorkflow = outboundWorkflow ?? throw new ArgumentNullException(nameof(outboundWorkflow));
    }

    public async Task<DomainResult<PeerConnectionResult>> ConnectWithInvitationAsync(
        PublicIdentityId ownerId,
        PeerInvitation invitation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        var payload = PeerInvitationPayload.FromDomain(invitation);
        var result = await _outboundWorkflow.ConnectAsync(ownerId, payload, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return DomainResult<PeerConnectionResult>.Failure(result.Error);
        }

        var channelId = ChannelId.DeterministicForDirect(ownerId, invitation.InviterIdentityId, invitation.RelayEndpoint);
        return DomainResult<PeerConnectionResult>.Success(new PeerConnectionResult(channelId, DeviceId.Primary));
    }

    public async Task<DomainResult<PeerConnectionResult>> ConnectToPeerAsync(
        PublicIdentityId targetPeerId,
        DeviceId? targetDeviceId = null,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default)
    {
        if (!targetPeerId.IsValid)
        {
            return DomainResult<PeerConnectionResult>.Failure(new DomainError(
                "INVALID_PEER_ID", "Target peer identity is invalid."));
        }

        if (relayEndpoint == null)
        {
            return DomainResult<PeerConnectionResult>.Failure(new DomainError(
                "MISSING_RELAY_ENDPOINT", "RelayEndpoint is required to connect to peer."));
        }

        // 1. Fetch consumable PreKeyBundle using the driven port
        var bundleResult = await _preKeyBundleProvider.FetchConsumableBundleAsync(
            targetPeerId,
            targetDeviceId,
            relayEndpoint,
            ct).ConfigureAwait(false);

        if (bundleResult.IsFailure)
        {
            return DomainResult<PeerConnectionResult>.Failure(bundleResult.Error);
        }

        var bundle = bundleResult.Value!;

        // 2. Ensure DirectChannel exists
        var channelId = await EnsureDirectChannelAsync(targetPeerId, relayEndpoint, ct).ConfigureAwait(false);

        // 3. Ensure PeerContact exists for the connected peer
        await EnsureContactAsync(targetPeerId, bundle.IdentityKey, ct).ConfigureAwait(false);

        // 4. Build initial 0-RTT capability payload via provider
        var initialPayloadResult = await _initialPayloadProvider.CreateInitialPayloadAsync(
            _localIdentityId,
            channelId,
            ct).ConfigureAwait(false);

        var (initialAppId, initialPayload) = initialPayloadResult.HasValue
            ? (initialPayloadResult.Value.AppId, initialPayloadResult.Value.Payload.ToArray())
            : ((AppId?)null, (byte[]?)null);

        // 5. Initiate pairwise X3DH handshake via HandshakeService
        var handshakeResult = await _handshakeService.InitiateHandshakeAsync(
            _localIdentityId,
            _localDeviceId,
            targetPeerId,
            relayEndpoint,
            bundle,
            initialAppId,
            initialPayload,
            ct: ct).ConfigureAwait(false);

        if (!handshakeResult.IsSuccess)
        {
            return DomainResult<PeerConnectionResult>.Failure(handshakeResult.Error);
        }

        return DomainResult<PeerConnectionResult>.Success(new PeerConnectionResult(channelId, bundle.DeviceId));
    }

    private async Task<ChannelId> EnsureDirectChannelAsync(PublicIdentityId targetPeerId, RelayEndpoint relayEndpoint, CancellationToken ct)
    {
        var channelId = ChannelId.DeterministicForDirect(_localIdentityId, targetPeerId, relayEndpoint);
        var existingChannel = await _channelRepository.GetDirectByIdAsync(channelId, ct).ConfigureAwait(false);
        if (existingChannel != null)
        {
            return existingChannel.Id;
        }

        var newChannelResult = DirectChannel.Create(
            channelId,
            _localIdentityId,
            targetPeerId,
            _timeProvider,
            DirectChannelStatus.PendingAcceptance,
            relayEndpoint);

        if (newChannelResult.IsSuccess)
        {
            var channel = newChannelResult.Value!;
            await _channelRepository.SaveDirectAsync(channel, ct).ConfigureAwait(false);
            try
            {
                foreach (var domainEvt in channel.DomainEvents)
                {
                    var appEvt = domainEvt.ToApplicationEvent();
                    if (appEvt != null)
                    {
                        await _eventPublisher.PublishAsync(appEvt, ct).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                channel.ClearDomainEvents();
            }
        }

        return channelId;
    }

    private async Task EnsureContactAsync(PublicIdentityId targetPeerId, IdentityKey identityKey, CancellationToken ct)
    {
        var existingContact = await _contactRepository.GetByPeerIdAsync(_localIdentityId, targetPeerId, ct).ConfigureAwait(false);
        if (existingContact == null)
        {
            var contactResult = PeerContact.CreateOutboundRequest(_localIdentityId, targetPeerId, identityKey, _timeProvider);
            if (contactResult.IsSuccess)
            {
                await _contactRepository.SaveAsync(contactResult.Value!, ct).ConfigureAwait(false);
            }
        }
    }
}
