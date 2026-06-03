using Percolator.Contracts;
using System.Net;

namespace Percolator.Network.Services;

public interface ISessionEstablishmentTransport
{
    Task<EstablishDirectSessionResponse> EstablishDirectSessionAsync(DnsEndPoint endpoint, EstablishDirectSessionRequest request, CancellationToken cancellationToken = default);
    Task<EstablishSessionResponse> EstablishSessionAsync(DnsEndPoint endpoint, EstablishSessionRequest request, CancellationToken cancellationToken = default);
    Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponseAsync(DnsEndPoint endpoint, InviteHandshakeResponse request, CancellationToken cancellationToken = default);
}
