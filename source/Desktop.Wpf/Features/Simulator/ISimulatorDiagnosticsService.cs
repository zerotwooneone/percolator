using ObservableCollections;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorDiagnosticsService
{
    IReadOnlyObservableList<SimulatorDiagnosticEvent> Events { get; }

    void Emit(SimulatorDiagnosticEventType eventType, string message, Guid? peerId = null, Guid? relayHostPeerId = null, Guid? ackId = null, string? contextTag = null);

    void Clear();
}