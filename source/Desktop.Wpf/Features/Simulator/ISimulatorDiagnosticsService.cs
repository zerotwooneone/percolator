using ObservableCollections;

using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorDiagnosticsService
{
    IReadOnlyObservableList<SimulatorDiagnosticEvent> Events { get; }

    void Emit(SimulatorDiagnosticEventType eventType, string message, NetworkPeerId? peerId = null, NetworkPeerId? relayHostPeerId = null, Guid? ackId = null, string? contextTag = null);

    void Clear();
}