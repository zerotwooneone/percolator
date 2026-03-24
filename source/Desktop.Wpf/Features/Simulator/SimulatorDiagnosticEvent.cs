namespace Desktop.Wpf.Features.Simulator;

public sealed record SimulatorDiagnosticEvent(
    DateTimeOffset TimestampUtc,
    SimulatorDiagnosticEventType EventType,
    string Message,
    Guid? PeerId = null,
    Guid? RelayHostPeerId = null,
    Guid? AckId = null,
    string? ContextTag = null);