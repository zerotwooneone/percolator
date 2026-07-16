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
    /// Publish a group message to the relay (anonymous egress, no auth headers).
    /// </summary>
    Task<SubmitGroupMessageResponse> PublishAsync(
        SubmitGroupMessageRequest request,
        CallOptions? callOptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueue an opaque message for 1:1 delivery (authenticated egress with DeliveryCertificate headers).
    /// </summary>
    Task<EnqueueOpaqueMessageResponse> EnqueueOpaqueMessageAsync(
        EnqueueOpaqueMessageRequest request,
        CallOptions? callOptions = null,
        CancellationToken cancellationToken = default);
}
