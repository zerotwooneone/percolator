using Percolator.Network;

namespace Percolator.Application.Services;

public sealed class DirectSessionLocator : IDirectSessionLocator
{
    private readonly IDirectSessionRepository _repo;

    public DirectSessionLocator(IDirectSessionRepository repo)
    {
        _repo = repo;
    }

    public async Task<DirectSessionId?> GetAsync(Percolator.Identity.PeerId remotePeerId, uint selfIdentityId, CancellationToken cancellationToken = default)
    {
        var netPeerId = new Percolator.Network.NetworkPeerId(remotePeerId.Value);
        var direct = await _repo.GetByRemotePeerIdAsync(netPeerId, new NetworkSelfId(selfIdentityId)).ConfigureAwait(false);
        return direct?.SessionId;
    }
}
