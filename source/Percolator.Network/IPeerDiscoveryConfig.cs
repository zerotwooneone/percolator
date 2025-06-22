namespace Percolator.Network;

public interface IPeerDiscoveryConfig
{
    int BroadcastPort { get; }
    int ListenPort { get; }
    TimeSpan BroadcastInterval { get; }
    TimeSpan PeerExpiration { get; }
}