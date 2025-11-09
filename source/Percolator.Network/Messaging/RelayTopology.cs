namespace Percolator.Network.Messaging;

public sealed class DefaultRelayTopology : IRelayTopology
{
    private readonly IPeerConnectionRepository _peerConnections;

    public DefaultRelayTopology(IPeerConnectionRepository peerConnections)
    {
        _peerConnections = peerConnections;
    }

    public async Task<PeerId?> GetRelayForAsync(PeerId target, CancellationToken ct = default)
    {
        // Persisted association lookup (returns null if not configured)
        return await _peerConnections.GetRelayAsync(target).ConfigureAwait(false);
    }
}
