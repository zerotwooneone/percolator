using System.Threading;
using System.Threading.Tasks;
using Percolator.Network;
using Percolator.Identity;

namespace Percolator.Application.Services;

public interface IDirectSessionLocator
{
    Task<DirectSessionId?> GetAsync(Percolator.Identity.PeerId remotePeerId, int selfIdentityId, CancellationToken cancellationToken = default);
}
