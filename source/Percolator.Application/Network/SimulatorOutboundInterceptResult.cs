using System.Net;
using Percolator.Contracts;

namespace Percolator.Application.Network;

/// <summary>
/// Result of simulator outbound interception attempt.
/// Discriminated union style: each case carries the data it needs.
/// </summary>
public abstract record SimulatorOutboundInterceptResult
{
    /// <summary>
    /// The message is not for the simulator; proceed with normal network send.
    /// </summary>
    public sealed record NotForSimulator : SimulatorOutboundInterceptResult;

    /// <summary>
    /// The message targets a simulator-reserved endpoint but no simulated peer exists.
    /// The send should fail fast.
    /// </summary>
    public sealed record Undeliverable(DnsEndPoint Endpoint, string FailureReason) : SimulatorOutboundInterceptResult;

    /// <summary>
    /// The message was successfully delivered to the simulator.
    /// </summary>
    public sealed record DeliveredToSimulator(DeliverOpaqueMessageResponse Response) : SimulatorOutboundInterceptResult;
}
