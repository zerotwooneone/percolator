namespace Desktop.Wpf.Features.Simulator;

public enum SimulatorPeerUiState
{
    Ready = 0,
    OutboundPending = 1,
    InboundPending = 2,
    Established = 3,
    Offline = 4,
    Expired = 5
}

public sealed record SimulatorPeerRuntimeState
{
    public SimulatorPeerUiState UiState { get; init; } = SimulatorPeerUiState.Ready;

    public Guid? PendingCorrelationId { get; init; }

    public byte[]? TargetPublicKeyHash { get; init; }

    public ConnectionMode? SelectedRouteMode { get; init; }

    public string? DirectEndpoint { get; init; }

    public Guid? RelayHostPeerId { get; init; }

    public string? Phase { get; init; }

    public DateTimeOffset? NotUntilUtc { get; init; }

    public string? LastError { get; init; }

    public List<SimulatorHandshakeAttemptState> HandshakeAttempts { get; init; } = new();
}

public sealed record SimulatorHandshakeAttemptState
{
    public Guid CorrelationId { get; init; }

    public byte[]? TargetPublicKeyHash { get; init; }

    public ConnectionMode? SelectedRouteMode { get; init; }

    public string? DirectEndpoint { get; init; }

    public Guid? RelayHostPeerId { get; init; }

    public string? Phase { get; init; }

    public DateTimeOffset? NotUntilUtc { get; init; }

    public string? LastError { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
