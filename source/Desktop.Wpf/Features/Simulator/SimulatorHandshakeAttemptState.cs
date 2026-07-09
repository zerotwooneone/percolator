namespace Desktop.Wpf.Features.Simulator;

using Percolator.Network;

public sealed record SimulatorHandshakeAttemptState
{
    public Guid CorrelationId { get; init; }

    public byte[]? TargetPublicKeyHash { get; init; }

    public ConnectionMode? SelectedRouteMode { get; init; }

    public string? DirectEndpoint { get; init; }

    public NetworkPeerId? RelayHostPeerId { get; init; }

    public string? Phase { get; init; }

    public DateTimeOffset? NotUntilUtc { get; init; }

    public string? LastError { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}