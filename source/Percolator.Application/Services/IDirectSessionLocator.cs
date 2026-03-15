using Percolator.Network;

namespace Percolator.Application.Services;

public interface IDirectSessionLocator
{
    Task<DirectSessionId?> GetAsync(Percolator.Identity.PeerId remotePeerId, int selfIdentityId, CancellationToken cancellationToken = default);
}
