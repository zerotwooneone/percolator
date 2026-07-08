namespace Percolator.Network.Messaging;

public sealed class DefaultRelayTopology : IRelayTopology
{
    private readonly IPeerRoutingProfileRepository _profiles;

    public DefaultRelayTopology(IPeerRoutingProfileRepository profiles)
    {
        _profiles = profiles;
    }

    public async Task<NetworkPeerId?> GetRelayForAsync(NetworkPeerId target, CancellationToken ct = default)
    {
        // Read routing profile; return first configured relay if present
        var profile = await _profiles.GetByIdAsync(target, ct).ConfigureAwait(false);
        var relay = profile?.Relays.FirstOrDefault();
        return relay is null ? null : relay.RelayNetworkPeerId;
    }
}
