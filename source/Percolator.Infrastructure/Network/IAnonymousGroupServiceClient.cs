using Grpc.Core;
using Percolator.Contracts;

namespace Percolator.Infrastructure.Network;

/// <summary>
/// Interface for AnonymousGroupService gRPC client operations.
/// Abstracts the actual gRPC client for testability and decoupling.
/// </summary>
public interface IAnonymousGroupServiceClient
{
    /// <summary>
    /// Process an anonymous group request (anonymous egress, no auth headers).
    /// </summary>
    Task<ProcessAnonymousGroupResponse> ProcessAnonymousGroupRequestAsync(
        AnonymousGroupRequest request,
        CallOptions? callOptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get group state (requires presentation for auth).
    /// </summary>
    Task<GetGroupStateResponse> GetGroupStateAsync(
        GetGroupStateRequest request,
        CallOptions? callOptions = null,
        CancellationToken cancellationToken = default);
}
