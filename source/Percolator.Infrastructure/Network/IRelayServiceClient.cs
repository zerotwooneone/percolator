using Grpc.Core;
using Percolator.Contracts;

namespace Percolator.Infrastructure.Network;

/// <summary>
/// Interface for RelayService gRPC client operations.
/// Abstracts the actual gRPC client for testability and decoupling.
/// </summary>
public interface IRelayServiceClient
{
    /// <summary>
    /// Enqueue an opaque message for 1:1 delivery (authenticated egress with DeliveryCertificate headers).
    /// </summary>
    Task<EnqueueOpaqueMessageResponse> EnqueueOpaqueMessageAsync(
        EnqueueOpaqueMessageRequest request,
        CallOptions? callOptions = null,
        CancellationToken cancellationToken = default);
}
