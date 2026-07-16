using Grpc.Core;
using Percolator.Contracts;

namespace Percolator.Infrastructure.Network;

/// <summary>
/// Interface for TransportService gRPC client operations.
/// Abstracts the actual gRPC client for testability and decoupling.
/// </summary>
public interface ITransportServiceClient
{
    /// <summary>
    /// Deliver an opaque message to a direct P2P session.
    /// </summary>
    Task<DeliverOpaqueMessageResponse> DeliverOpaqueMessageAsync(
        DeliverOpaqueMessageRequest request,
        CallOptions? callOptions = null,
        CancellationToken cancellationToken = default);
}
