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
        var netPeerId = new Percolator.Network.PeerId(remotePeerId.Value);
        var direct = await _repo.GetByRemotePeerIdAsync(netPeerId, selfIdentityId).ConfigureAwait(false);
        return direct?.SessionId;
    }
}
