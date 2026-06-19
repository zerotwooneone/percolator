using Percolator.Contracts;
using Percolator.Network;

namespace Percolator.Application.Network;

public sealed record SendMessageResponse
{
    public DeliverOpaqueMessageResponse OriginalResponse { get; init; } = null!;
    public GrpcEndPoint? UsedEndpoint { get; init; }
}
