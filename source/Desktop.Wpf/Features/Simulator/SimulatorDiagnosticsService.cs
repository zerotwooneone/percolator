using ObservableCollections;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorDiagnosticsService : ISimulatorDiagnosticsService
{
    private const int MaxEvents = 2000;

    private readonly ObservableList<SimulatorDiagnosticEvent> _events = new();
    private readonly object _gate = new();

    public IReadOnlyObservableList<SimulatorDiagnosticEvent> Events => _events;

    public void Emit(
        SimulatorDiagnosticEventType eventType,
        string message,
        Guid? peerId = null,
        Guid? relayHostPeerId = null,
        Guid? ackId = null,
        string? contextTag = null)
    {
        var ev = new SimulatorDiagnosticEvent(
            TimestampUtc: DateTimeOffset.UtcNow,
            EventType: eventType,
            Message: message,
            PeerId: peerId,
            RelayHostPeerId: relayHostPeerId,
            AckId: ackId,
            ContextTag: contextTag);

        lock (_gate)
        {
            _events.Add(ev);
            while (_events.Count > MaxEvents) _events.RemoveAt(0);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }
}