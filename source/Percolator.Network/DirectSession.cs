namespace Percolator.Network;

public sealed class DirectSession
{
    public PeerId RemotePeerId { get; }
    public DirectSessionId SessionId { get; }

    public DirectSession(PeerId remotePeerId, DirectSessionId sessionId)
    {
        RemotePeerId = remotePeerId;
        SessionId = sessionId;
    }
}
