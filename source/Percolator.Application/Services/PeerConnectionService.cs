namespace Percolator.Application.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Handshake;
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
    private readonly IStreamRegistryManagement _streamRegistry;
    private readonly IPeerMailboxRoutingRepository _routingRepository;
    private readonly IPeerContactRepository _contactRepository;
    private readonly IEventPublisher _eventPublisher;
    private readonly IInitialHandshakePayloadProvider _initialPayloadProvider;

    public PeerConnectionService(
        PublicIdentityId localIdentityId,
        DeviceId localDeviceId,
        IPreKeyBundleProvider preKeyBundleProvider,
        IHandshakeService handshakeService,
        IChannelRepository channelRepository,
        IDateTimeProvider timeProvider,
        IStreamRegistryManagement streamRegistry,
        IPeerMailboxRoutingRepository routingRepository,
        IPeerContactRepository contactRepository,
        IEventPublisher eventPublisher,
        IInitialHandshakePayloadProvider initialPayloadProvider)
    {
        _localIdentityId = localIdentityId;
        _localDeviceId = localDeviceId;
        _preKeyBundleProvider = preKeyBundleProvider ?? throw new ArgumentNullException(nameof(preKeyBundleProvider));
        _handshakeService = handshakeService ?? throw new ArgumentNullException(nameof(handshakeService));
        _channelRepository = channelRepository ?? throw new ArgumentNullException(nameof(channelRepository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _streamRegistry = streamRegistry ?? throw new ArgumentNullException(nameof(streamRegistry));
        _routingRepository = routingRepository ?? throw new ArgumentNullException(nameof(routingRepository));
        _contactRepository = contactRepository ?? throw new ArgumentNullException(nameof(contactRepository));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _initialPayloadProvider = initialPayloadProvider ?? throw new ArgumentNullException(nameof(initialPayloadProvider));
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

        // 4. Build initial 0-RTT capability payload via provider (cloaked: allowDirectEndpoint: false)
        var initialPayloadResult = await _initialPayloadProvider.CreateInitialPayloadAsync(
            _localIdentityId,
            channelId,
            allowDirectEndpoint: false,
            ct).ConfigureAwait(false);

        var (initialAppId, initialPayload) = initialPayloadResult.HasValue
            ? (initialPayloadResult.Value.AppId, initialPayloadResult.Value.Payload.ToArray())
            : ((AppId?)null, (byte[]?)null);

        // 5. Initiate pairwise X3DH handshake via HandshakeService
        var handshakeResult = await _handshakeService.InitiateHandshakeAsync(
            _localIdentityId,
            _localDeviceId,
            targetPeerId,
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

    public async Task<DomainResult<PeerConnectionResult>> ConnectToDirectPeerAsync(
        PublicIdentityId targetPeerId,
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        Uri directEndpoint,
        DeviceId? targetDeviceId = null,
        CancellationToken ct = default)
    {
        if (!targetPeerId.IsValid)
        {
            return DomainResult<PeerConnectionResult>.Failure(new DomainError(
                "INVALID_PEER_ID", "Target peer identity is invalid."));
        }

        ArgumentNullException.ThrowIfNull(directEndpoint);

        // 1. Register the direct endpoint into the StreamRegistry so transport dispatcher routes to it
        _streamRegistry.RegisterDirectEndpoint(targetPeerId, directEndpoint);

        // 2. Synthesize RelayEndpoint from direct URI for prekey retrieval over loopback/direct TCP
        var endpointStr = $"{directEndpoint.Host}:{directEndpoint.Port}";
        var endpointResult = RelayEndpoint.Create(endpointStr);
        if (endpointResult.IsFailure)
        {
            return DomainResult<PeerConnectionResult>.Failure(endpointResult.Error);
        }

        var directRelayEndpoint = endpointResult.Value!;

        // Also save direct routing capability to IPeerMailboxRoutingRepository for fallback resolution
        var routingCapability = new PeerMailboxRoutingCapability(
            targetPeerId,
            routingToken,
            deliveryToken,
            directRelayEndpoint,
            _timeProvider.UtcNow);

        await _routingRepository.SaveAsync(routingCapability, ct).ConfigureAwait(false);

        // 3. Fetch consumable PreKeyBundle using the direct relay endpoint
        var bundleResult = await _preKeyBundleProvider.FetchConsumableBundleAsync(
            targetPeerId,
            targetDeviceId,
            directRelayEndpoint,
            ct).ConfigureAwait(false);

        if (bundleResult.IsFailure)
        {
            return DomainResult<PeerConnectionResult>.Failure(bundleResult.Error);
        }

        var bundle = bundleResult.Value!;

        // 4. Ensure DirectChannel exists with relayEndpoint: null (direct P2P channel)
        var channelId = await EnsureDirectChannelAsync(targetPeerId, relayEndpoint: null, ct).ConfigureAwait(false);

        // 5. Ensure PeerContact exists for the connected peer
        await EnsureContactAsync(targetPeerId, bundle.IdentityKey, ct).ConfigureAwait(false);

        // 6. Build initial 0-RTT capability payload via provider (direct: allowDirectEndpoint: true)
        var initialPayloadResult = await _initialPayloadProvider.CreateInitialPayloadAsync(
            _localIdentityId,
            channelId,
            allowDirectEndpoint: true,
            ct).ConfigureAwait(false);

        var (initialAppId, initialPayload) = initialPayloadResult.HasValue
            ? (initialPayloadResult.Value.AppId, initialPayloadResult.Value.Payload.ToArray())
            : ((AppId?)null, (byte[]?)null);

        // 7. Initiate pairwise X3DH handshake via HandshakeService
        var handshakeResult = await _handshakeService.InitiateHandshakeAsync(
            _localIdentityId,
            _localDeviceId,
            targetPeerId,
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

    private async Task<ChannelId> EnsureDirectChannelAsync(PublicIdentityId targetPeerId, RelayEndpoint? relayEndpoint, CancellationToken ct)
    {
        var existingChannel = await _channelRepository.GetDirectByPeerAsync(_localIdentityId, targetPeerId, ct).ConfigureAwait(false);
        if (existingChannel != null)
        {
            return existingChannel.Id;
        }

        var channelId = ChannelId.DeterministicForDirect(_localIdentityId, targetPeerId);
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
