namespace Percolator.Network;

public sealed class DirectSession
{
    public NetworkPeerId RemoteNetworkPeerId { get; }
    public DirectSessionId SessionId { get; }

    public DirectSession(NetworkPeerId remoteNetworkPeerId, DirectSessionId sessionId)
    {
        RemoteNetworkPeerId = remoteNetworkPeerId;
        SessionId = sessionId;
    }
}
