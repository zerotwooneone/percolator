using System.Net;
using Percolator.Contracts;
using Percolator.Network.Services;

namespace Percolator.ApplicationIntegrationTests.TestDoubles;

internal sealed class SingleHostGrpcSessionLoopback : ISessionEstablishmentTransport
{
    private readonly IServiceProvider _hostProvider;

    public SingleHostGrpcSessionLoopback(IServiceProvider hostProvider)
    {
        _hostProvider = hostProvider;
    }

    public Task<EstablishDirectSessionResponse> EstablishDirectSessionAsync(DnsEndPoint endpoint, EstablishDirectSessionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new EstablishDirectSessionResponse
        {
            Version = 1,
            Queued = new EstablishDirectSessionResponse.Types.Queued { Version = 1 }
        });
    }

    public Task<EstablishSessionResponse> EstablishSessionAsync(
        DnsEndPoint endpoint,
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new EstablishSessionResponse
        {
            Version = 1,
            Never = new EstablishSessionResponse.Types.Never { Version = 1 }
        });
    }

    public Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponseAsync(DnsEndPoint endpoint, InviteHandshakeResponse request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DeliverInviteHandshakeResponseAck { Version = 1 });
    }
}
