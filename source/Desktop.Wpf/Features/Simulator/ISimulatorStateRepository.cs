namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorStateRepository
{
    Task<SimulatorStateDto?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SimulatorStateDto state, CancellationToken cancellationToken = default);

    Task<RelayPersistenceDto?> LoadRelayAsync(Guid relayHostPeerId, CancellationToken cancellationToken = default);

    Task SaveRelayAsync(RelayPersistenceDto relay, CancellationToken cancellationToken = default);
}
