using Percolator.Application2.Ports;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class LoopbackStreamRegistry : IStreamRegistry
{
    private readonly PublicIdentityId _localIdentityId;
    private readonly DeviceId _localDeviceId;
    private readonly InMemoryLoopbackNetworkSwitch _networkSwitch;
    private readonly IChannelRepository _channelRepository;

    public LoopbackStreamRegistry(
        PublicIdentityId localIdentityId,
        DeviceId localDeviceId,
        InMemoryLoopbackNetworkSwitch networkSwitch,
        IChannelRepository channelRepository)
    {
        _localIdentityId = localIdentityId;
        _localDeviceId = localDeviceId;
        _networkSwitch = networkSwitch ?? throw new ArgumentNullException(nameof(networkSwitch));
        _channelRepository = channelRepository ?? throw new ArgumentNullException(nameof(channelRepository));
    }

    public bool HasActiveStream(PublicIdentityId targetId)
    {
        return _networkSwitch.HasActiveStreamForPeer(_localIdentityId, targetId);
    }

    public bool HasActiveRelayStream(PublicIdentityId relayId)
    {
        return _networkSwitch.HasActiveStreamForPeer(_localIdentityId, relayId);
    }

    public async ValueTask<StreamWriteResult> TryWriteAsync(
        PublicIdentityId targetId,
        ReadOnlyMemory<byte> framedPayload,
        CancellationToken ct = default)
    {
        if (!HasActiveStream(targetId))
        {
            return StreamWriteResult.Closed($"No active stream registered between {_localIdentityId} and {targetId}.");
        }

        if (framedPayload.IsEmpty)
        {
            return StreamWriteResult.Closed("Payload is empty.");
        }

        // Resolve direct channel to encode ChannelId in the 53-byte sealed frame
        var directChannel = await _channelRepository.GetDirectByPeerAsync(_localIdentityId, targetId, ct).ConfigureAwait(false);
        if (directChannel is null)
        {
            return StreamWriteResult.Closed($"No direct channel between {_localIdentityId} and {targetId}.");
        }

        var kind = (WireFrameKind)framedPayload.Span[0];
        if (kind is not (WireFrameKind.DirectRatchet or WireFrameKind.GroupBroadcast or WireFrameKind.X3dhHandshake))
        {
            return StreamWriteResult.Closed($"Invalid wire frame kind 0x{framedPayload.Span[0]:X2}.");
        }

        var sealedBytes = SealedWireFraming.Encode(
            kind,
            directChannel.Id,
            _localIdentityId,
            _localDeviceId,
            framedPayload);

        var sendResult = await _networkSwitch.SendToPeerAsync(targetId, sealedBytes).ConfigureAwait(false);
        if (!sendResult.IsSuccess)
        {
            return StreamWriteResult.Closed(sendResult.Error.Description);
        }

        return StreamWriteResult.Success();
    }
}
