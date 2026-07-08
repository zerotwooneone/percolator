using System.Net;
using Percolator.Contracts;

namespace Percolator.Application.Network;

public sealed record InviteHandshakeResponseDeliveryResult(bool Success, string SendPath, Exception? Error = null);

public interface IInviteHandshakeResponseDeliveryService
{
    Task<InviteHandshakeResponseDeliveryResult> DeliverAsync(
        Percolator.Network.NetworkPeerId inviterNetworkPeerId,
        DnsEndPoint? directCallbackEndpoint,
        InviteHandshakeResponse response,
        CancellationToken ct = default);
}