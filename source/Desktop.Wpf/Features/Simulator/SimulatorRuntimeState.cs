using System;

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
}
