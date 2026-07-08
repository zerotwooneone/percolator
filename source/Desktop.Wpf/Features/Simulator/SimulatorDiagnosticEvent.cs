using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator;

public sealed record SimulatorDiagnosticEvent(
    DateTimeOffset TimestampUtc,
    SimulatorDiagnosticEventType EventType,
    string Message,
    NetworkPeerId? PeerId = null,
    NetworkPeerId? RelayHostPeerId = null,
    Guid? AckId = null,
    string? ContextTag = null);