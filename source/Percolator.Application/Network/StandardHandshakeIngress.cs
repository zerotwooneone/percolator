using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Contracts;

namespace Percolator.Application.Network;

internal sealed class StandardHandshakeIngress : IStandardHandshakeIngress
{
    public Task<EstablishSessionResponse> HandleAsync(EstablishSessionRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        ct.ThrowIfCancellationRequested();

        // Chunk D (D1) scaffolding: wire server-side RPC surface to an application ingress.
        // Full X3DH/DR bootstrap is implemented in later tasks.
        return Task.FromResult(new EstablishSessionResponse
        {
            Version = 1,
            Never = new EstablishSessionResponse.Types.Never { Version = 1 }
        });
    }
}
