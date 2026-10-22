using System.Collections.Concurrent;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2IntegrationTests.Infrastructure;

public sealed class InMemoryLoopbackNetworkSwitch
{
    private readonly ConcurrentDictionary<EndpointPort, Func<ReadOnlyMemory<byte>, Task>> _portReceivers = new();
    private readonly ConcurrentDictionary<PublicIdentityId, EndpointPort> _identityToPort = new();
    private readonly ConcurrentDictionary<EndpointPort, PublicIdentityId> _portToIdentity = new();
    private readonly ConcurrentDictionary<(EndpointPort Source, EndpointPort Target), bool> _activeStreams = new();

    public void RegisterNode(EndpointPort port, PublicIdentityId identityId, Func<ReadOnlyMemory<byte>, Task> inboundReceiver)
    {
        _portReceivers[port] = inboundReceiver ?? throw new ArgumentNullException(nameof(inboundReceiver));
        _identityToPort[identityId] = port;
        _portToIdentity[port] = identityId;
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
}
