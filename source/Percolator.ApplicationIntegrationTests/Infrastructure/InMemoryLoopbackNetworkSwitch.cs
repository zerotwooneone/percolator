using System.Collections.Concurrent;
using System.Threading.Channels;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Application2.Ports;
using Percolator.Application2IntegrationTests.Builders;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2IntegrationTests.Infrastructure;

public sealed class InMemoryLoopbackNetworkSwitch
{
    private readonly ConcurrentDictionary<EndpointPort, Func<ReadOnlyMemory<byte>, Task>> _portReceivers = new();
    private readonly ConcurrentDictionary<PublicIdentityId, EndpointPort> _identityToPort = new();
    private readonly ConcurrentDictionary<EndpointPort, PublicIdentityId> _portToIdentity = new();
    private readonly ConcurrentDictionary<(EndpointPort Source, EndpointPort Target), bool> _activeStreams = new();
    private readonly ConcurrentDictionary<(EndpointPort, EndpointPort, int), (Channel<IStreamFramePayload> Forward, Channel<IStreamFramePayload> Reverse)> _streamChannels = new();
    private readonly ConcurrentDictionary<PublicIdentityId, RelayNode> _relaysById = new();
    private readonly ConcurrentDictionary<PublicIdentityId, RelayNode> _recipientToRelay = new();
    private readonly ConcurrentDictionary<ChannelId, RelayNode> _channelToRelay = new();
    private readonly ConcurrentDictionary<ChannelId, (GroupMasterKey MasterKey, AuthCredentialMacBytes AuthMac)> _channelCredentials = new();
    private readonly ConcurrentDictionary<PublicIdentityId, IRelayProfileRepository> _peerProfileStores = new();
    private readonly ConcurrentDictionary<PublicIdentityId, Func<BlobId, CancellationToken, ValueTask<Stream?>>> _peerBlobResponders = new();

    public IReadOnlyCollection<RelayNode> AllRelays => _relaysById.Values.ToList();
    public IReadOnlyCollection<IRelayProfileRepository> AllPeerProfileStores => _peerProfileStores.Values.ToList();

    public void RegisterNode(EndpointPort port, PublicIdentityId identityId, Func<ReadOnlyMemory<byte>, Task> inboundReceiver)
    {
        _portReceivers[port] = inboundReceiver ?? throw new ArgumentNullException(nameof(inboundReceiver));
        _identityToPort[identityId] = port;
        _portToIdentity[port] = identityId;
    }

    public void RegisterRelay(RelayNode relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        _relaysById[relay.RelayIdentityId] = relay;
    }

    public void BindRecipientToRelay(PublicIdentityId recipientId, RelayNode relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        _recipientToRelay[recipientId] = relay;
        _relaysById[relay.RelayIdentityId] = relay;
    }

    public bool TryGetRelay(PublicIdentityId relayId, out RelayNode? relay)
    {
        return _relaysById.TryGetValue(relayId, out relay);
    }

    public bool TryGetRelayForRecipient(PublicIdentityId recipientId, out RelayNode? relay)
    {
        return _recipientToRelay.TryGetValue(recipientId, out relay);
    }

    public void RegisterRelayForChannel(ChannelId channelId, RelayNode relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        _channelToRelay[channelId] = relay;
        _relaysById[relay.RelayIdentityId] = relay;
    }

    public bool TryGetRelayForChannel(ChannelId channelId, out RelayNode? relay)
    {
        return _channelToRelay.TryGetValue(channelId, out relay);
    }

    public void RegisterGroupCredentials(ChannelId channelId, GroupMasterKey masterKey, AuthCredentialMacBytes authMac)
    {
        _channelCredentials[channelId] = (masterKey, authMac);
    }

    public bool TryGetGroupCredentials(ChannelId channelId, out (GroupMasterKey MasterKey, AuthCredentialMacBytes AuthMac) creds)
    {
        return _channelCredentials.TryGetValue(channelId, out creds);
    }

    public void RegisterPeerProfileStore(PublicIdentityId peerId, IRelayProfileRepository profileRepo)
    {
        _peerProfileStores[peerId] = profileRepo;
    }

    public bool TryGetPeerProfileStore(PublicIdentityId peerId, out IRelayProfileRepository? profileRepo)
    {
        return _peerProfileStores.TryGetValue(peerId, out profileRepo);
    }

    public void RegisterPeerBlobResponder(PublicIdentityId peerId, Func<BlobId, CancellationToken, ValueTask<Stream?>> responder)
    {
        _peerBlobResponders[peerId] = responder;
    }

    public async ValueTask<DomainResult<Stream>> TransferBlobAcrossStreamAsync(
        PublicIdentityId requesterId,
        PublicIdentityId peerId,
        BlobId blobId,
        CancellationToken ct = default)
    {
        if (!HasActiveStreamForPeer(requesterId, peerId))
        {
            return DomainResult<Stream>.Failure(new DomainError(
                "STREAM_NOT_CONNECTED", $"No direct active stream between {requesterId} and {peerId}."));
        }

        if (!_peerBlobResponders.TryGetValue(peerId, out var responder))
        {
            return DomainResult<Stream>.Failure(new DomainError(
                "PEER_BLOB_SERVICE_UNAVAILABLE", $"Peer {peerId} has no active blob transfer responder."));
        }

        var sourceStream = await responder(blobId, ct).ConfigureAwait(false);
        if (sourceStream is null)
        {
            return DomainResult<Stream>.Failure(new DomainError(
                "BLOB_NOT_FOUND_ON_PEER", $"Blob {blobId} not found on peer {peerId}."));
        }

        var wireBuffer = new MemoryStream();
        await sourceStream.CopyToAsync(wireBuffer, ct).ConfigureAwait(false);
        wireBuffer.Position = 0;

        return DomainResult<Stream>.Success(wireBuffer);
    }

    public void OpenDirectStream(EndpointPort sourcePort, EndpointPort targetPort)
    {
        _activeStreams[(sourcePort, targetPort)] = true;
        _activeStreams[(targetPort, sourcePort)] = true;
    }

    public void CloseDirectStream(EndpointPort sourcePort, EndpointPort targetPort)
    {
        _activeStreams.TryRemove((sourcePort, targetPort), out _);
        _activeStreams.TryRemove((targetPort, sourcePort), out _);
    }

    public bool HasActiveStream(EndpointPort sourcePort, EndpointPort targetPort)
    {
        return _activeStreams.TryGetValue((sourcePort, targetPort), out var active) && active;
    }

    public bool HasActiveStreamForPeer(PublicIdentityId sourcePeerId, PublicIdentityId targetPeerId)
    {
        if (_identityToPort.TryGetValue(sourcePeerId, out var srcPort) &&
            _identityToPort.TryGetValue(targetPeerId, out var tgtPort))
        {
            return HasActiveStream(srcPort, tgtPort);
        }

        return false;
    }

    public async Task<DomainResult> SendAsync(EndpointPort targetPort, ReadOnlyMemory<byte> frameBytes)
    {
        if (!_portReceivers.TryGetValue(targetPort, out var receiver))
        {
            return DomainResult.Failure(new DomainError("PORT_UNREACHABLE", $"No active node listening on port {targetPort}."));
        }

        await receiver(frameBytes).ConfigureAwait(false);
        return DomainResult.Success();
    }

    public async Task<DomainResult> SendToPeerAsync(PublicIdentityId targetPeerId, ReadOnlyMemory<byte> frameBytes)
    {
        if (!_identityToPort.TryGetValue(targetPeerId, out var port))
        {
            return DomainResult.Failure(new DomainError("PEER_UNKNOWN", $"No port registration found for peer {targetPeerId}."));
        }

        return await SendAsync(port, frameBytes).ConfigureAwait(false);
    }

    public EndpointPort? TryGetPortForPeer(PublicIdentityId peerId)
    {
        return _identityToPort.TryGetValue(peerId, out var port) ? port : null;
    }

    public (ChannelWriter<IStreamFramePayload> Outgoing, ChannelReader<IStreamFramePayload> Incoming) GetOrCreateStreamPair(
        EndpointPort localPort,
        EndpointPort remotePort,
        int streamIndex)
    {
        var key = localPort.Value < remotePort.Value
            ? (localPort, remotePort, streamIndex)
            : (remotePort, localPort, streamIndex);

        var (lowToHigh, highToLow) = _streamChannels.GetOrAdd(key, _ =>
        {
            var options = new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = false
            };
            return (Channel.CreateUnbounded<IStreamFramePayload>(options), Channel.CreateUnbounded<IStreamFramePayload>(options));
        });

        return localPort.Value < remotePort.Value
            ? (lowToHigh.Writer, highToLow.Reader)
            : (highToLow.Writer, lowToHigh.Reader);
    }
}
