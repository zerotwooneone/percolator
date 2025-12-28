using Percolator.Contracts;
using System.Net;

namespace Percolator.Application.Network
{
    /// <summary>
    /// Service for handling gRPC session establishment with TOFU support
    /// </summary>
    public interface IGrpcSessionService
    {
        /// <summary>
        /// Establishes a gRPC session with a remote endpoint with trust-on-first-use (TOFU) support
        /// </summary>
        /// <param name="endpoint">The endpoint to connect to</param>
        /// <param name="request">The request to send</param>
        /// <param name="remoteCert">The certificate to trust, if provided</param>
        /// <returns>The response from the remote endpoint</returns>
        Task<EstablishDirectSessionResponse> EstablishDirectSessionAsync(
            DnsEndPoint endpoint, 
            EstablishDirectSessionRequest request);

        Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponseAsync(
            DnsEndPoint endpoint,
            InviteHandshakeResponse request);
    }
}
