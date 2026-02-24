using System.Threading;
using System.Threading.Tasks;
using Percolator.Contracts;

namespace Percolator.Application.Network;

public interface IStandardHandshakeIngress
{
    Task<EstablishSessionResponse> HandleAsync(EstablishSessionRequest request, CancellationToken ct = default);
}
