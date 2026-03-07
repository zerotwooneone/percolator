using System.Threading;
using System.Threading.Tasks;
using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.Application.Network;

public interface IStandardHandshakeIngress
{
    Task<EstablishSessionResponse> HandleAsync(SelfId selfIdentityId, EstablishSessionRequest request, CancellationToken ct = default);
}
