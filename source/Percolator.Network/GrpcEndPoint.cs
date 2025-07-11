using System.Net;

namespace Percolator.Network;

/// <summary>
/// Represents a known gRPC endpoint for a peer, including when it was last seen.
/// This is a value object.
/// </summary>
public record GrpcEndPoint(IPEndPoint EndPoint, DateTimeOffset LastSeen);
