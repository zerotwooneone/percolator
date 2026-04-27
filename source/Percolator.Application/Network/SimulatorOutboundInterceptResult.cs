using System.Net;

namespace Percolator.Application.Network;

/// <summary>
/// Represents the three possible outcomes of simulator outbound interception.
/// </summary>
public enum SimulatorOutboundInterceptResultKind
{
    /// <summary>
    /// The message is not for the simulator; proceed with normal network send.
    /// </summary>
    NotForSimulator,

    /// <summary>
    /// The message was successfully delivered to the simulator.
    /// </summary>
    DeliveredToSimulator,

    /// <summary>
    /// The message targets a simulator-reserved endpoint but no simulated peer exists.
    /// The send should fail fast.
    /// </summary>
    Undeliverable
}

/// <summary>
/// Result of simulator outbound interception attempt.
/// </summary>
public sealed record SimulatorOutboundInterceptResult
{
    public SimulatorOutboundInterceptResultKind Kind { get; }
    public string? FailureReason { get; }
    public DnsEndPoint? Endpoint { get; }

    private SimulatorOutboundInterceptResult(
        SimulatorOutboundInterceptResultKind kind,
        string? failureReason = null,
        DnsEndPoint? endpoint = null)
    {
        if (kind == SimulatorOutboundInterceptResultKind.Undeliverable
            && string.IsNullOrWhiteSpace(failureReason))
        {
            throw new ArgumentException("FailureReason must be provided when Kind is Undeliverable.", nameof(failureReason));
        }

        Kind = kind;
        FailureReason = failureReason;
        Endpoint = endpoint;
    }

    public static SimulatorOutboundInterceptResult NotForSimulator() => new(SimulatorOutboundInterceptResultKind.NotForSimulator);
    public static SimulatorOutboundInterceptResult DeliveredToSimulator() => new(SimulatorOutboundInterceptResultKind.DeliveredToSimulator);
    public static SimulatorOutboundInterceptResult Undeliverable(string reason) => new(SimulatorOutboundInterceptResultKind.Undeliverable, reason);
    public static SimulatorOutboundInterceptResult Undeliverable(DnsEndPoint endpoint, string reason) =>
        new(SimulatorOutboundInterceptResultKind.Undeliverable, reason, endpoint);
}
