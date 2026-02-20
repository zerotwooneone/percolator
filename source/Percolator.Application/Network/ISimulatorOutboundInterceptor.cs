using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Contracts;

namespace Percolator.Application.Network;

public interface ISimulatorOutboundInterceptor
{
    bool TryDeliverInviteHandshakeResponse(
        DnsEndPoint endpoint,
        InviteHandshakeResponse request,
        out Task<DeliverInviteHandshakeResponseAck> result);

    bool TryDeliverOpaqueMessage(
        DnsEndPoint endpoint,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken,
        out Task<DeliverOpaqueMessageResponse> result);
}
