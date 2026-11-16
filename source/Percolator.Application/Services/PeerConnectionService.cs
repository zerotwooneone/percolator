using System.Security.Cryptography;
using Percolator.Application.Ports;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Services;

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

    public PeerConnectionService(
        PublicIdentityId localIdentityId,
        DeviceId localDeviceId,
        IPreKeyBundleProvider preKeyBundleProvider,
        IHandshakeService handshakeService,
        IChannelRepository channelRepository,
        IDateTimeProvider timeProvider,
        IStreamRegistryManagement streamRegistry,
        IPeerMailboxRoutingRepository routingRepository)
    {
        _localIdentityId = localIdentityId;
        _localDeviceId = localDeviceId;
        _preKeyBundleProvider = preKeyBundleProvider ?? throw new ArgumentNullException(nameof(preKeyBundleProvider));
        _handshakeService = handshakeService ?? throw new ArgumentNullException(nameof(handshakeService));
        _channelRepository = channelRepository ?? throw new ArgumentNullException(nameof(channelRepository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _streamRegistry = streamRegistry ?? throw new ArgumentNullException(nameof(streamRegistry));
        _routingRepository = routingRepository ?? throw new ArgumentNullException(nameof(routingRepository));
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

        // 2. Initiate pairwise X3DH handshake via HandshakeService
        var handshakeResult = await _handshakeService.InitiateHandshakeAsync(
            _localIdentityId,
            _localDeviceId,
            targetPeerId,
            bundle,
            ct: ct).ConfigureAwait(false);

        if (!handshakeResult.IsSuccess)
        {
            return DomainResult<PeerConnectionResult>.Failure(handshakeResult.Error);
        }

        // 3. Ensure DirectChannel exists
        var channelId = await EnsureDirectChannelAsync(targetPeerId, ct).ConfigureAwait(false);

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

        // 1. Register directEndpoint into stream registry
        _streamRegistry.RegisterDirectEndpoint(targetPeerId, directEndpoint);

        // 2. Construct and persist PeerMailboxRoutingCapability
        var directRelayEndpoint = RelayEndpoint.FromHostAndPort(directEndpoint.Host, directEndpoint.Port);
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

        // 4. Initiate pairwise X3DH handshake via HandshakeService
        var handshakeResult = await _handshakeService.InitiateHandshakeAsync(
            _localIdentityId,
            _localDeviceId,
            targetPeerId,
            bundle,
            ct: ct).ConfigureAwait(false);

        if (!handshakeResult.IsSuccess)
        {
            return DomainResult<PeerConnectionResult>.Failure(handshakeResult.Error);
        }

        // 5. Ensure DirectChannel exists
        var channelId = await EnsureDirectChannelAsync(targetPeerId, ct).ConfigureAwait(false);

        return DomainResult<PeerConnectionResult>.Success(new PeerConnectionResult(channelId, bundle.DeviceId));
    }

    private async Task<ChannelId> EnsureDirectChannelAsync(PublicIdentityId targetPeerId, CancellationToken ct)
    {
        var existingChannel = await _channelRepository.GetDirectByPeerAsync(_localIdentityId, targetPeerId, ct).ConfigureAwait(false);
        if (existingChannel != null)
        {
            return existingChannel.Id;
        }

        var channelId = DeriveDirectChannelId(_localIdentityId, targetPeerId);
        var newChannelResult = DirectChannel.Create(channelId, _localIdentityId, targetPeerId, _timeProvider);
        if (newChannelResult.IsSuccess)
        {
            await _channelRepository.SaveDirectAsync(newChannelResult.Value!, ct).ConfigureAwait(false);
        }

        return channelId;
    }

    private static ChannelId DeriveDirectChannelId(PublicIdentityId a, PublicIdentityId b)
    {
        var (first, second) = a.Value.CompareTo(b.Value) < 0 ? (a, b) : (b, a);
        Span<byte> combined = stackalloc byte[32];
        first.TryWriteBytes(combined[..16]);
        second.TryWriteBytes(combined[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(combined, hash);
        return ChannelId.FromBytes(hash[..16]);
    }
}
