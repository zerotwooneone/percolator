using Percolator.Application2.Ports;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2.Services;

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

    public PeerConnectionService(
        PublicIdentityId localIdentityId,
        DeviceId localDeviceId,
        IPreKeyBundleProvider preKeyBundleProvider,
        IHandshakeService handshakeService,
        IChannelRepository channelRepository,
        IDateTimeProvider timeProvider)
    {
        _localIdentityId = localIdentityId;
        _localDeviceId = localDeviceId;
        _preKeyBundleProvider = preKeyBundleProvider ?? throw new ArgumentNullException(nameof(preKeyBundleProvider));
        _handshakeService = handshakeService ?? throw new ArgumentNullException(nameof(handshakeService));
        _channelRepository = channelRepository ?? throw new ArgumentNullException(nameof(channelRepository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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
            bundle,
            ct: ct).ConfigureAwait(false);

        if (!handshakeResult.IsSuccess)
        {
            return DomainResult<PeerConnectionResult>.Failure(handshakeResult.Error);
        }

        // 3. Ensure DirectChannel exists
        var existingChannel = await _channelRepository.GetDirectByPeerAsync(_localIdentityId, targetPeerId, ct).ConfigureAwait(false);
        ChannelId channelId;
        if (existingChannel != null)
        {
            channelId = existingChannel.Id;
        }
        else
        {
            channelId = ChannelId.New();
            var newChannelResult = DirectChannel.Create(channelId, _localIdentityId, targetPeerId, _timeProvider);
            if (newChannelResult.IsSuccess)
            {
                await _channelRepository.SaveDirectAsync(newChannelResult.Value!, ct).ConfigureAwait(false);
            }
        }

        return DomainResult<PeerConnectionResult>.Success(new PeerConnectionResult(channelId, bundle.DeviceId));
    }
}
